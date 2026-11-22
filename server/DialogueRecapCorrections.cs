using System.Text.RegularExpressions;
using Npgsql;

internal static partial class DialogueRecaps
{
    // The marker must retract an account. "I was wrong to lend it" expresses regret,
    // and a bare "no" can simply answer someone else's question.
    static readonly Regex Retraction = new(
        @"^(?:(?:well|sorry|actually)[,\s]+)*(?:I (?:was mistaken|was wrong|misspoke|got that wrong|take that back|stand corrected)|we (?:were mistaken|were wrong|take that back)|scratch that|let me correct that|correction|(?:no,\s*)(?:I|we) (?:did not|didn['’]t|never did))(?=$|[\s]*[.!?,:;—–-]|\s+about\b)",
        RegexOptions.IgnoreCase);
    static readonly Regex Clarification = new(@"^(?:(?:actually|rather|on second thought)\s*[,—:]\s*|I (?:meant to say|should have said)\s+)",RegexOptions.IgnoreCase);

    sealed record Part(int Index,string Text,bool Gesture,bool Question,string[] Terms,string Note,
        string[]? Reference=null,int? Earlier=null,bool NeedsMatch=false)
    {
        public bool Correction => Reference != null;
    }

    static List<Part> Notes(Source row)
    {
        string Describe(string text,bool question,bool gesture,string[] terms) =>
            row.UnresolvedYou && !gesture && text.Length<=144 && Regex.IsMatch(text,@"\byou(?:r|rs|rselves|rself)?\b",RegexOptions.IgnoreCase)
            ? Fragment(text,question) : Detail(text,question,gesture,terms);
        var parts = Parts(row.Words).ToArray();
        var notes = new List<Part>();
        for(int i=0; i<parts.Length; i++){
            int index = i;
            var (words,gesture) = parts[i];
            bool question = !gesture && IsQuestion(words);
            var terms = Terms(words);
            var marker = Retraction.Match(words);
            bool clarify = !marker.Success;
            if(clarify) marker = Clarification.Match(words);
            bool denial = marker.Success && Regex.IsMatch(marker.Value,@"\bno,?\s+(I|we)\b",RegexOptions.IgnoreCase);
            if(gesture || question || !marker.Success || denial && !notes.Any(p=>FirstPerson.IsMatch(p.Text) && !p.Question && !p.Gesture)){
                notes.Add(new(index,words,gesture,question,terms,Describe(words,question,gesture,terms)));
                continue;
            }
            string rest = words[marker.Length..].Trim(' ','.','!',',',':',';','—','–','-');
            if(rest.Length==0 && i+1<parts.Length && !parts[i+1].Gesture && !IsQuestion(parts[i+1].Text) &&
                !Retraction.IsMatch(parts[i+1].Text))
                rest = parts[++i].Text;
            string detail = clarify ? "Clarified their account." : denial || marker.Value.Contains("take that back",StringComparison.OrdinalIgnoreCase)
                ? "Withdrew their earlier account." : "Corrected their earlier account.";
            var reference = Terms(rest);
            if(rest.Length>0) detail += " " + Describe(rest,false,false,reference);

            // Use matching event words, then an unambiguous first-person antecedent
            // for an elliptical self-correction. Never select by sentence length.
            var prior = notes.Where(p=>!p.Gesture && !p.Question && !p.Correction && p.Terms.Length>0 &&
                !p.Note.StartsWith("Answered ",StringComparison.Ordinal)).ToArray();
            var vocabulary = reference.Concat(EventWords.Where(forms=>forms.Intersect(reference).Any()).SelectMany(forms=>forms)).ToHashSet();
            var related = prior.Where(p=>p.Terms.Any(vocabulary.Contains)).ToArray();
            var personal = prior.Where(p=>FirstPerson.IsMatch(p.Text)).ToArray();
            Part? target = related.Length==1 ? related[0] : null;
            if(!clarify && related.Length==0)
                target = prior.Length==1 ? prior[0] : personal.Length==1 ? personal[0] : null;
            // A marker with several possible antecedents qualifies the account as a whole.
            // -1 denotes the preceding statements of this one speaker line, never another turn.
            int? earlier = target?.Index ?? (prior.Length>0 && (!clarify || related.Length>0) ? -1 : null);
            notes.Add(new(index,words,false,false,terms.Concat(reference).Distinct().ToArray(),detail,reference,earlier,clarify));
        }
        // A short withdrawal and its antecedent enter or leave the budget together.
        var accounts = notes.GroupBy(p=>p.Earlier is >=0 ? p.Earlier.Value : p.Index)
            .OrderByDescending(g=>g.Any(p=>p.Correction)).ThenByDescending(g=>g.Max(p=>p.Terms.Length));
        var selected = new List<Part>(4);
        foreach(var account in accounts){
            var partsOfAccount = account.GroupBy(p=>p.Correction).Select(g=>g.Last()).ToArray();
            if(partsOfAccount.Length<=4-selected.Count) selected.AddRange(partsOfAccount);
        }
        return selected.OrderBy(p=>p.Index).ToList();
    }

    sealed record PartKey(Guid Source,int Position,int Sentence);

    // A later turn can refer back to an already closed day. The target is one earlier
    // account by this speaker, on this save line, in this conversation or identifiable
    // by its event words. The correction keeps its own witnesses and lifetime.
    static async Task<PartKey?> EarlierAccount(NpgsqlConnection conn,Source row,Part part,CancellationToken ct)
    {
        if(part.Earlier is int sentence) return new(row.Id,row.Position,sentence);
        await using var select = Database.Command(conn,"""
            WITH RECURSIVE past(id,parent) AS (
              SELECT id,parent FROM cue_events WHERE campaign=@c AND id=@anchor
              UNION ALL SELECT e.id,e.parent FROM cue_events e JOIN past ON e.id=past.parent WHERE e.campaign=@c
            )
            SELECT coalesce(p.corrects_source,p.source),coalesce(p.corrects_pos,p.pos),coalesce(p.corrects_sentence,p.sentence),
              (p.mode=@mode AND p.session=@session) AS same_visit,ts_rank(to_tsvector('english',p.keywords),to_tsquery('english',@terms)) AS score
            FROM dialogue_recap_parts p
            WHERE p.campaign=@c AND p.speaker=@who AND p.source<>@source AND NOT p.question
              AND (p.anchor IS NULL OR p.anchor IN (SELECT id FROM past)) AND p.age BETWEEN @age-29 AND @age AND p.day<=@day AND p.said_at<=@at
              AND array_remove(p.audience,p.speaker) && @audience
              AND ((NOT @needs_match AND p.mode=@mode AND p.session=@session) OR to_tsvector('english',p.keywords) @@ to_tsquery('english',@terms))
              AND coalesce(p.corrects_sentence,0)>=0
              AND p.detail !~ '^(Gestured|Made a gesture|Exchanged a few words|Answered (yes|no)\.)'
            ORDER BY score DESC,same_visit DESC,p.age DESC,p.said_at DESC,p.seq DESC,p.source,p.pos,p.sentence DESC LIMIT 2
            """,("c",row.Campaign),("anchor",row.Anchor),("who",row.Speaker),("mode",row.Mode),("source",row.Id),
            ("age",row.Age),("day",row.Day),("at",row.At),("session",row.Session),("audience",row.Audience),("terms",Query(part.Reference!)),("needs_match",part.NeedsMatch));
        await using var reader = await select.ExecuteReaderAsync(ct);
        if(!await reader.ReadAsync(ct)) return null;
        var key = new PartKey(reader.GetGuid(0),reader.GetInt32(1),reader.GetInt32(2));
        bool sameVisit = reader.GetBoolean(3);
        float score = reader.GetFloat(4);
        if(await reader.ReadAsync(ct) && reader.GetFloat(4)==score && reader.GetBoolean(3)==sameVisit) return null;
        return key;
    }
}
