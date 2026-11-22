using System.Text.RegularExpressions;
using Npgsql;

internal static partial class DialogueRecaps
{
    // The marker must retract an account. "I was wrong to lend it" expresses regret,
    // and a bare "no" can simply answer someone else's question.
    static readonly Regex Retraction = new(
        @"^(?:(?:well|sorry|actually)[,\s]+)*(?:I (?:was mistaken|was wrong|misspoke|got that wrong|take that back|stand corrected)|we (?:were mistaken|were wrong|take that back)|scratch that|let me correct that|correction|(?:no,\s*)(?:I|we) (?:did not|didn['’]t|never did))(?=$|[\s]*[.!?,:;—–-]|\s+(?:about|regarding|concerning)\b)",
        RegexOptions.IgnoreCase);
    static readonly Regex Clarification = new(@"^(?:(?:actually|rather|on second thought)\s*[,—:]\s*|I (?:meant to say|should have said)\s+)",RegexOptions.IgnoreCase);
    static readonly Regex NamedSubject = new(@"^(?:about|regarding|concerning)\s+([^,;:.!?…—–]+)",RegexOptions.IgnoreCase);
    static readonly Regex Ellipsis = new(@"^(?:(?:about\s+)?(?:it|that|this|what (?:I|we) (?:just )?said))?[.!…]*$",RegexOptions.IgnoreCase);
    const string ActorAnswer = @"(?:(?-i:\p{Lu}[\p{L}’'-]*(?: \p{Lu}[\p{L}’'-]*)*)|me|us|you|him|her|them)";
    static readonly Regex EllipticalAnswer = new(@"^(?:(?:no|actually|rather)[,\s]+)?(?:(?:it|that|this|they|those) " +
        @"(?:was|is|were|are|might be|may have been) " + ActorAnswer + "|" + ActorAnswer + @" (?:did|was|were))(?: instead)?[.!…]*$",RegexOptions.IgnoreCase);
    static readonly Regex RefersBack = new(@"\b(?:it|that|this|those|them|there|instead)\b",RegexOptions.IgnoreCase);

    sealed record Part(int Index,string Text,bool Gesture,bool Question,string[] Terms,string Note,
        string[]? Reference=null,int? Earlier=null,bool NeedsMatch=false,bool MatchAll=false)
    {
        public bool Correction => Reference != null;
        public Part? Following { get; init; }
        public PartKey? Target { get; init; }
    }

    static bool MatchesReference(string[] reference,string[] evidence,bool requireAll,int minimum=1)
    {
        bool Heard(string word) => evidence.Contains(word) || EventWords.Any(forms=>forms.Contains(word) && forms.Any(evidence.Contains));
        return reference.Length>0 && (requireAll ? reference.All(Heard) : reference.Where(Heard).Take(minimum).Count()==minimum);
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
            bool elliptical = Ellipsis.IsMatch(rest);
            var subject = NamedSubject.Match(rest);
            bool named = subject.Success && !elliptical;
            var reference = Terms(named ? subject.Groups[1].Value : rest);
            string detail = clarify ? "Clarified their account." : denial || marker.Value.Contains("take that back",StringComparison.OrdinalIgnoreCase)
                ? "Withdrew their earlier account." : "Corrected their earlier account.";
            if(named) detail = Describe(words,false,false,terms);
            if(!named && rest.Length>0) detail += " " + Describe(rest,false,false,Terms(rest));
            // Only the marker can require a particular target. A subsequent clause
            // can identify the earlier event or supply a replacement, or stand alone.
            bool needsMatch = clarify || !elliptical;
            Part? following = null;
            bool actorOnly = false;
            if((elliptical || named) && i+1<parts.Length && !parts[i+1].Gesture && !IsQuestion(parts[i+1].Text) &&
                !Retraction.IsMatch(parts[i+1].Text) && !Clarification.IsMatch(parts[i+1].Text)){
                string next = parts[i+1].Text;
                var nextTerms = Terms(next);
                following = new(i+1,next,false,false,nextTerms,Describe(next,false,false,nextTerms));
                actorOnly = EllipticalAnswer.IsMatch(next);
                if(elliptical && !actorOnly) reference = nextTerms;
            }

            // Use matching event words, then an unambiguous first-person antecedent
            // for an elliptical self-correction. Never select by sentence length.
            var prior = notes.Where(p=>!p.Gesture && !p.Question && !p.Correction && p.Terms.Length>0 &&
                !p.Note.StartsWith("Answered ",StringComparison.Ordinal)).ToArray();
            // Without a backward reference, one shared word can just be a change of subject.
            int minimum = !needsMatch && following!=null && !RefersBack.IsMatch(following.Text) ? 2 : 1;
            var related = prior.Where(p=>MatchesReference(reference,p.Terms,named,minimum)).ToArray();
            var personal = prior.Where(p=>FirstPerson.IsMatch(p.Text)).ToArray();
            Part? target = related.Length==1 ? related[0] : null;
            if(!needsMatch && related.Length==0)
                target = prior.Length==1 ? prior[0] : denial && personal.Length==1 ? personal[0] : null;
            // A marker with several possible antecedents qualifies the account as a whole.
            // -1 denotes the preceding statements of this one speaker line, never another turn.
            int? earlier = target?.Index ?? (prior.Length>1 && !needsMatch ? -1 : null);
            // An explicit but ambiguous local subject cannot pick an older turn by proximity.
            var lookup = related.Length>1 ? [] : reference;
            if(following!=null){
                bool supports = actorOnly || (named ? MatchesReference(reference,following.Terms,true) : related.Length>0);
                if(supports){
                    detail += " " + following.Note;
                    words += " " + following.Text;
                    i++;
                    following = null;
                }else if(named || earlier.HasValue) following = null;
            }
            notes.Add(new(index,words,false,false,Terms(words),detail,lookup,earlier,needsMatch,named){Following=following});
        }
        // For a later-turn correction the database can supply the event match.
        // Its following clause needs its own slot unless that match establishes a connection.
        return notes.Any(p=>p.Following!=null) ? notes : SelectNotes(notes);
    }

    static List<Part> SelectNotes(List<Part> notes)
    {
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

    sealed record PartKey(Guid Source,int Position,int Sentence)
    {
        public bool ContinuesFollowing { get; init; }
        public string[]? Possible { get; init; }
        public string Address => $"{Source:D}/{Position}/{Sentence}";
    }

    static async Task<List<Part>> ResolveNotes(NpgsqlConnection conn,Source row,CancellationToken ct)
    {
        var notes = Notes(row);
        var resolved = new List<Part>(notes.Count);
        int combined = -1;
        foreach(var part in notes){
            if(part.Index==combined) continue;
            var target = part.Correction ? await EarlierAccount(conn,row,part,ct) : null;
            string note = part.NeedsMatch && target==null ? Detail(part.Text,part.Question,part.Gesture,part.Terms) : part.Note;
            var terms = part.Terms;
            if(part.Following is {} following && target is {ContinuesFollowing:true}){
                note += " " + following.Note;
                terms = terms.Concat(following.Terms).Distinct().ToArray();
                combined = following.Index;
            }
            if(target?.Possible!=null) note = "Unclear target. " + note;
            resolved.Add(part with {Note=note,Terms=terms,Target=target});
        }
        return notes.Any(p=>p.Following!=null) ? SelectNotes(resolved) : resolved;
    }

    // A later turn can refer back to an already closed day. The target is one earlier
    // account by this speaker, on this save line, in this conversation or identifiable
    // by its event words. The correction keeps its own witnesses and lifetime.
    static async Task<PartKey?> EarlierAccount(NpgsqlConnection conn,Source row,Part part,CancellationToken ct)
    {
        if(part.Earlier is int sentence) return new(row.Id,row.Position,sentence);
        var reference = part.Reference!;
        if(part.NeedsMatch && reference.Length==0) return null;
        string query = part.MatchAll ? string.Join(" & ",reference.Select(word=>"("+Query([word])+")")) : Query(reference);
        var overlap = part.Following is {} next && !RefersBack.IsMatch(next.Text) ? reference.Select(word=>Query([word])).ToArray() : null;
        await using var select = Database.Command(conn,"""
            WITH RECURSIVE past(id,parent) AS (
              SELECT id,parent FROM cue_events WHERE campaign=@c AND id=@anchor
              UNION ALL SELECT e.id,e.parent FROM cue_events e JOIN past ON e.id=past.parent WHERE e.campaign=@c
            ), candidates AS (
            SELECT coalesce(p.corrects_source,p.source) AS root,coalesce(p.corrects_pos,p.pos) AS pos,coalesce(p.corrects_sentence,p.sentence) AS sentence,
              (p.mode=@mode AND p.session=@session) AS same_visit,CASE WHEN hit.matches THEN ts_rank(doc.words,to_tsquery('english',@terms)) ELSE 0 END AS score
            FROM dialogue_recap_parts p
            CROSS JOIN LATERAL (SELECT to_tsvector('english',p.keywords) AS words) doc
            CROSS JOIN LATERAL (SELECT doc.words @@ to_tsquery('english',@terms) AND (@overlap::text[] IS NULL OR
              EXISTS(SELECT FROM unnest(@overlap::text[]) AS t(term) WHERE doc.words @@ to_tsquery('english',t.term) OFFSET 1)) AS matches) hit
            WHERE p.campaign=@c AND p.speaker=@who AND p.source<>@source AND NOT p.question
              AND (p.anchor IS NULL OR p.anchor IN (SELECT id FROM past)) AND p.age BETWEEN @age-29 AND @age AND p.day<=@day AND p.said_at<=@at
              AND array_remove(p.audience,p.speaker) && @audience
              AND ((NOT @needs_match AND p.mode=@mode AND p.session=@session) OR hit.matches)
              AND p.possible_targets IS NULL AND coalesce(p.corrects_sentence,0)>=0
              AND p.detail !~ '^(Gestured|Made a gesture|Exchanged a few words|Answered (yes|no)\.)'
            ), accounts AS (
              SELECT DISTINCT ON(root,pos,sentence) * FROM candidates ORDER BY root,pos,sentence,score DESC,same_visit DESC
            )
            SELECT root,pos,sentence,same_visit,score FROM accounts
            ORDER BY (NOT @needs_match AND same_visit) DESC,score DESC,same_visit DESC FETCH FIRST 1 ROW WITH TIES
            """,("c",row.Campaign),("anchor",row.Anchor),("who",row.Speaker),("mode",row.Mode),("source",row.Id),
            ("age",row.Age),("day",row.Day),("at",row.At),("session",row.Session),("audience",row.Audience),("terms",query),
            ("overlap",overlap),("needs_match",part.NeedsMatch));
        await using var reader = await select.ExecuteReaderAsync(ct);
        if(!await reader.ReadAsync(ct)) return null;
        PartKey Current() => new(reader.GetGuid(0),reader.GetInt32(1),reader.GetInt32(2));
        var first = Current() with {ContinuesFollowing=reader.GetFloat(4)>0};
        bool bareInVisit = !part.NeedsMatch && reader.GetBoolean(3);
        if(!await reader.ReadAsync(ct)) return first;
        if(!bareInVisit) return null;
        var possible = new List<string>{first.Address};
        do { possible.Add(Current().Address); } while(await reader.ReadAsync(ct));
        return first with {Possible=possible.ToArray()};
    }
}
