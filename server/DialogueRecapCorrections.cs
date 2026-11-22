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

    sealed record Part(int Index,string Text,bool Gesture,bool Question,string[] Terms,string Note)
    {
        public bool Correction { get; init; }
        public PartKey? Target { get; init; }
    }

    sealed record PartKey(Guid Source,int Position,int Sentence)
    {
        public string[]? Possible { get; init; }
        public string Address => $"{Source:D}/{Position}/{Sentence}";
    }
    sealed record Account(PartKey Key,string Note,string[] Terms,bool SameVisit);
    sealed record SpokenEvent(string Verb,string[] Objects,bool PointsBack);

    // Compare the predicate at the same grammatical position in both clauses. A pronoun
    // object can leave the object unspecified. An impersonal subject supplies no action.
    static readonly Regex Predicate = new(@"^(?:(?<cleft>(?:it|that) (?:was|is) " + ActorAnswer + @" (?:who|that))|" +
        @"they and others|the (?:speaker|addressee)|(?!(?:it|this|that|the|a|an)\b)[\p{L}’'-]+(?: (?-i:\p{Lu})[\p{L}’'-]*)*)\s+" +
        @"(?:(?:may|might|can|could|will|would|should|must|did|not|never)\s+)*(?<verb>[\p{L}’'-]+)\b(?<object>.*)$",RegexOptions.IgnoreCase);
    static readonly Regex ObjectReference = new(@"^(?:it|them|this|that|these|those)\b",RegexOptions.IgnoreCase);

    static SpokenEvent? Event(string clause)
    {
        var match = Predicate.Match(clause);
        if(!match.Success) return null;
        string verb = match.Groups["verb"].Value.ToLowerInvariant();
        if(verb is "is" or "are" or "was" or "were" or "am" or "be" or "have" or "has" or "had") return null;
        string rest = match.Groups["object"].Value.Trim();
        return new(verb,Terms(rest),match.Groups["cleft"].Success || ObjectReference.IsMatch(rest));
    }

    static bool SameWord(string a,string b) => a==b || EventWords.Any(forms=>forms.Contains(a) && forms.Contains(b));
    static bool Includes(string[] evidence,string[] required) => required.All(word=>evidence.Any(other=>SameWord(word,other)));
    static bool Describes(SpokenEvent? action,string note)
    {
        if(action==null) return false;
        string? clause = note.StartsWith("Said ",StringComparison.Ordinal) ? note["Said ".Length..] :
            note.StartsWith("Recounted “",StringComparison.Ordinal) ? note["Recounted “".Length..] : null;
        return clause!=null && Event(clause) is {} prior && SameWord(action.Verb,prior.Verb) && Includes(prior.Objects,action.Objects);
    }

    static PartKey? ChooseTarget(Source row,List<Account> grounded,List<Account> eligible,Part[] local,bool fallback,bool denial)
    {
        IEnumerable<PartKey> candidates;
        if(grounded.Count>0){
            int Proximity(Account a) => a.Key.Source==row.Id ? 2 : a.SameVisit ? 1 : 0;
            int nearest = grounded.Max(Proximity);
            candidates = grounded.Where(a=>Proximity(a)==nearest).Select(a=>a.Key);
        }else if(!fallback) return null;
        else if(local.Length>0){
            var personal = denial ? local.Where(p=>FirstPerson.IsMatch(p.Text)).ToArray() : [];
            candidates = (personal.Length==1 ? personal : local).Select(p=>new PartKey(row.Id,row.Position,p.Index));
        }else candidates = eligible.Where(a=>a.SameVisit).Select(a=>a.Key);

        var keys = candidates.Distinct().ToArray();
        if(keys.Length<2) return keys.SingleOrDefault();
        // -1 denotes ambiguity over every preceding statement of this one speaker line.
        if(keys.Length==local.Length && keys.All(k=>k.Source==row.Id && k.Position==row.Position))
            return new(row.Id,row.Position,-1);
        return keys[0] with {Possible=keys.Select(k=>k.Address).Order(StringComparer.Ordinal).ToArray()};
    }

    static Part[] Clauses(Source row) => Parts(row.Words).Select((piece,index)=>{
        var (words,gesture) = piece;
        bool question = !gesture && IsQuestion(words);
        var terms = Terms(words);
        string note = row.UnresolvedYou && !gesture && words.Length<=144 && Regex.IsMatch(words,@"\byou(?:r|rs|rselves|rself)?\b",RegexOptions.IgnoreCase)
            ? Fragment(words,question) : Detail(words,question,gesture,terms);
        return new Part(index,words,gesture,question,terms,note);
    }).ToArray();

    static async Task<List<Part>> ResolveNotes(NpgsqlConnection conn,Source row,CancellationToken ct)
    {
        var parts = Clauses(row);
        var notes = new List<Part>();
        for(int i=0;i<parts.Length;i++){
            var part = parts[i];
            var marker = Retraction.Match(part.Text);
            bool clarify = !marker.Success;
            if(clarify) marker = Clarification.Match(part.Text);
            bool denial = marker.Success && Regex.IsMatch(marker.Value,@"\bno,?\s+(I|we)\b",RegexOptions.IgnoreCase);
            if(part.Gesture || part.Question || !marker.Success || denial && !notes.Any(p=>FirstPerson.IsMatch(p.Text) && !p.Gesture && !p.Question)){
                notes.Add(part);
                continue;
            }
            string rest = part.Text[marker.Length..].Trim(' ','.','!',',',':',';','—','–','-');
            bool bare = !clarify && Ellipsis.IsMatch(rest);
            var named = NamedSubject.Match(rest);
            var subject = named.Success && !bare ? Terms(named.Groups[1].Value) : null;
            var next = (bare || subject!=null) && i+1<parts.Length ? parts[i+1] : null;
            if(next!=null && (next.Question || next.Gesture || Retraction.IsMatch(next.Text) || Clarification.IsMatch(next.Text))) next=null;
            bool actorOnly = next!=null && EllipticalAnswer.IsMatch(next.Text);
            var action = actorOnly ? null : Event(next?.Text ?? rest);
            var local = notes.Where(p=>!p.Question && !p.Gesture && !p.Correction && p.Terms.Length>0 &&
                !p.Note.StartsWith("Answered ",StringComparison.Ordinal)).ToArray();
            var accounts = local.Select(p=>new Account(new(row.Id,row.Position,p.Index),p.Note,p.Terms,true)).ToList();
            bool fallback = bare && action?.PointsBack!=true;
            if(subject!=null || action!=null || local.Length==0)
                await AddEarlierAccounts(conn,row,subject ?? (action==null ? [] : [action.Verb]),fallback,accounts,ct);
            var grounded = accounts.Where(a=>subject!=null ? subject.Length>0 && Includes(a.Terms,subject) : Describes(action,a.Note)).ToList();
            var continued = subject==null ? grounded : grounded.Where(a=>Describes(action,a.Note)).ToList();
            var target = ChooseTarget(row,continued.Count>0 ? continued : grounded,accounts,local,fallback,denial);
            string detail = subject!=null ? part.Note : clarify ? "Clarified their account." :
                denial || marker.Value.Contains("take that back",StringComparison.OrdinalIgnoreCase) ? "Withdrew their earlier account." : "Corrected their earlier account.";
            if(subject==null && rest.Length>0) detail += " " + Detail(rest,false,false,Terms(rest));
            if(target==null) detail = part.Note;
            bool joins = next!=null && target!=null && (actorOnly || continued.Count>0);
            if(joins){
                detail += " " + next!.Note;
                part = part with {Terms=part.Terms.Concat(next.Terms).Distinct().ToArray()};
                i++;
            }
            if(target?.Possible!=null) detail = "Unclear target. " + detail;
            notes.Add(part with {Correction=true,Note=detail,Target=target});
        }
        // A short withdrawal and its antecedent enter or leave the four-part budget together.
        var groups = notes.GroupBy(p=>p.Target is {Possible:null,Sentence:>=0} k && k.Source==row.Id && k.Position==row.Position ? k.Sentence : p.Index)
            .OrderByDescending(g=>g.Any(p=>p.Correction)).ThenByDescending(g=>g.Max(p=>p.Terms.Length));
        var selected = new List<Part>(4);
        foreach(var group in groups){
            var members = group.GroupBy(p=>p.Correction).Select(g=>g.Last()).ToArray();
            if(members.Length<=4-selected.Count) selected.AddRange(members);
        }
        return selected.OrderBy(p=>p.Index).ToList();
    }

    // SQL restricts the search to sourced words, this listener group and this save line.
    // Same-visit rows may supply a fallback. They compete on the same evidence as inline rows.
    static async Task AddEarlierAccounts(NpgsqlConnection conn,Source row,string[] reference,bool fallback,List<Account> accounts,CancellationToken ct)
    {
        if(!fallback && reference.Length==0) return;
        await using var select = Database.Command(conn,"""
            WITH RECURSIVE past(id,parent) AS (
              SELECT id,parent FROM cue_events WHERE campaign=@c AND id=@anchor
              UNION ALL SELECT e.id,e.parent FROM cue_events e JOIN past ON e.id=past.parent WHERE e.campaign=@c
            )
            SELECT coalesce(p.corrects_source,p.source),coalesce(p.corrects_pos,p.pos),coalesce(p.corrects_sentence,p.sentence),
              p.detail,p.keywords,p.mode=@mode AND p.session=@session
            FROM dialogue_recap_parts p
            WHERE p.campaign=@c AND p.speaker=@who AND p.source<>@source AND NOT p.question
              AND (p.anchor IS NULL OR p.anchor IN (SELECT id FROM past)) AND p.age BETWEEN @age-29 AND @age AND p.day<=@day AND p.said_at<=@at
              AND array_remove(p.audience,p.speaker) && @audience
              AND ((@fallback AND p.mode=@mode AND p.session=@session) OR p.search @@ to_tsquery('english',nullif(@terms,'')))
              AND p.possible_targets IS NULL AND coalesce(p.corrects_sentence,0)>=0
              AND p.detail !~ '^(Gestured|Made a gesture|Exchanged a few words|Answered (yes|no)\.)'
            """,("c",row.Campaign),("anchor",row.Anchor),("who",row.Speaker),("mode",row.Mode),("source",row.Id),
            ("age",row.Age),("day",row.Day),("at",row.At),("session",row.Session),("audience",row.Audience),("terms",Query(reference)),("fallback",fallback));
        await using var reader = await select.ExecuteReaderAsync(ct);
        while(await reader.ReadAsync(ct)) accounts.Add(new(new(reader.GetGuid(0),reader.GetInt32(1),reader.GetInt32(2)),
            reader.GetString(3),reader.GetString(4).Split(' '),reader.GetBoolean(5)));
    }
}
