using System.Text.RegularExpressions;
using Npgsql;

internal static partial class DialogueRecaps
{
    // The marker must retract an account. "I was wrong to lend it" expresses regret,
    // and a bare "no" can simply answer someone else's question.
    static readonly Regex Retraction = new(
        @"^(?:(?:well|sorry|actually)[,\s]+)*(?:I (?:was mistaken|was wrong|misspoke|got that wrong|take that back|stand corrected)|we (?:were mistaken|were wrong|take that back)|scratch that|let me correct that|correction|(?:no,\s*)(?:I|we) (?:did not|didn['’]t|never did))(?=$|[\s]*[.!?,:;—–-]|\s+(?:about|regarding|concerning)\b)",
        RegexOptions.IgnoreCase);
    static readonly Regex Clarification = new(@"^(?:(?:actually|rather|on second thought)\s*[,—:]\s*|I (?:meant to say|should have said|meant(?!\s+to\b))\s+)",RegexOptions.IgnoreCase);
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
    sealed record SpokenEvent(string Verb,string[] Object,string[] Qualifier,bool PronounObject,bool ActorReplacement);

    // A named event and a topic have different proof requirements. A failed event
    // comparison must never be retried as an unordered keyword comparison.
    sealed record TargetReference(SpokenEvent? Action,string[] Topic)
    {
        public string[] Search => Action is {} e ? [e.Verb,..e.Object] : Topic;
        public bool Matches(Account a) => Action!=null ? Describes(Action,a.Note) : Topic.Length>0 && Includes(a.Terms,Topic);
    }

    // Compare the predicate at the same grammatical position in both clauses. A pronoun
    // object can leave the object unspecified. An impersonal subject supplies no action.
    static readonly Regex Predicate = new(@"^(?:(?<cleft>(?:it|that) (?:was|is) " + ActorAnswer + @" (?:who|that))|" +
        @"they and others|the (?:speaker|addressee)|(?!(?:it|this|that|the|a|an)\b)[\p{L}’'-]+(?: (?-i:\p{Lu})[\p{L}’'-]*)*)\s+" +
        @"(?:(?:may|might|can|could|will|would|should|must|did|not|never|(?:had|has|have)(?=\s+\w+(?:ed|en)\b)|(?:was|were|is|are)(?=\s+\w+ing\b))\s+)*(?<verb>[\p{L}’'-]+)\b(?<object>.*)$",RegexOptions.IgnoreCase);
    static readonly Regex ObjectReference = new(@"^(?:it|them|this|that|these|those)\b",RegexOptions.IgnoreCase);
    static readonly Regex Qualifier = new(@"\b(?:at|in|on|by|near|beside|alongside|under|beneath|over|above|below|behind|beyond|outside|inside|across|through|around|with|from|to|for|of|before|after|during|until|while|when|unless|because)\b|[,;:]",RegexOptions.IgnoreCase);

    static string[] Phrase(string text) => Words.Matches(text.ToLowerInvariant().Replace('’','\'')
        .Replace("the speaker's","my").Replace("the addressee's","your")).Select(m=>m.Value)
        .Where(word=>word is not ("the" or "a" or "an")).ToArray();

    static SpokenEvent? Event(Match match)
    {
        if(!match.Success) return null;
        string verb = match.Groups["verb"].Value.ToLowerInvariant();
        if(verb is "is" or "are" or "was" or "were" or "am" or "be" or "have" or "has" or "had") return null;
        string rest = match.Groups["object"].Value.Trim();
        int end = Qualifier.Match(rest) is {Success:true} qualifier ? qualifier.Index : rest.Length;
        return new(verb,Phrase(ObjectReference.Replace(rest[..end],"",1)),Phrase(rest[end..]),ObjectReference.IsMatch(rest),match.Groups["cleft"].Success);
    }
    static SpokenEvent? Event(string clause) => Event(Predicate.Match(clause));

    // For an actor question the contiguous predicate is stronger lookup evidence
    // than the same words separated by a landmark. Only ask SQL for an ordering hint.
    static string AskedEventPhrase(string question)
    {
        if(!question.StartsWith("who ",StringComparison.OrdinalIgnoreCase)) return "";
        var clause = Regex.Replace(question,@"^who\s+(?:[\p{L}]+ly\s+)+","who ",RegexOptions.IgnoreCase);
        var match = Predicate.Match(clause);
        return Event(match) is {Object.Length:>0} ? clause[match.Groups["verb"].Index..] : "";
    }

    static readonly Regex EmbeddedClause = new(@"^(?:that\s+(?!(?:was|is)\b)|(?:(?:the|a|an)\s+[\p{L}’'-]+|"+ActorAnswer+@")\s+(?=who\b))",RegexOptions.IgnoreCase);
    static readonly Regex Gerund = new(@"^(?:the\s+)?(?<verb>[\p{L}’'-]+ing)\s+(?:of\s+)?(?<object>(?:the|an?|my|your|our|their|his|her|it|them|this|that|those|these)\b.*)$",RegexOptions.IgnoreCase);
    static readonly Regex ClausalSubject = new(@"^(?:who|whom|what|which|whether|how|where|when|why|I|we|you|he|she|they|it)\b|\s(?:who|that|which)\b",RegexOptions.IgnoreCase);

    static TargetReference Reference(string words,bool named)
    {
        var clause = EmbeddedClause.Replace(words,"",1);
        var gerund = Gerund.Match(clause);
        if(gerund.Success) return new(Event(gerund),[]);
        var action = Event(clause);
        if(!named || clause!=words || ClausalSubject.IsMatch(clause) || action!=null &&
            (action.Object.Length>0 || action.Qualifier.Length>0) &&
            (char.IsUpper(clause[0]) || VerbStem(action.Verb)!=action.Verb || EventWords.Any(forms=>forms.Contains(action.Verb))))
            return new(action,[]);
        return new(null,Terms(clause));
    }

    // Compare regular finite and nonfinite verb forms only in predicate position.
    static string VerbStem(string verb) => verb switch {
        _ when verb.Length>3 && (verb.EndsWith("ied") || verb.EndsWith("ies")) => verb[..^3]+"y",
        _ when verb.Length>4 && verb.EndsWith("ing") => verb[..^3],
        _ when verb.Length>3 && verb.EndsWith("ed") => verb[..^2],
        _ when verb.Length>3 && verb.EndsWith('s') && !verb.EndsWith("ss") => verb[..^1],
        _ => verb
    };

    static bool SameWord(string a,string b) => a==b || EventWords.Any(forms=>forms.Contains(a) && forms.Contains(b));
    static bool Includes(string[] evidence,string[] required) => required.All(word=>evidence.Any(other=>SameWord(word,other)));
    static bool Describes(SpokenEvent action,SpokenEvent prior) =>
        (SameWord(action.Verb,prior.Verb) || VerbStem(action.Verb)==VerbStem(prior.Verb)) &&
        prior.Object.AsSpan().EndsWith(action.Object) && prior.Qualifier.AsSpan().StartsWith(action.Qualifier);
    static bool Describes(SpokenEvent? action,string note)
    {
        if(action==null) return false;
        string? clause = note.StartsWith("Said ",StringComparison.Ordinal) ? note["Said ".Length..] :
            note.StartsWith("Recounted “",StringComparison.Ordinal) ? note["Recounted “".Length..] : null;
        return clause!=null && Event(clause) is {} prior && Describes(action,prior);
    }

    static PartKey? ChooseTarget(Source row,List<Account> grounded,List<Account> eligible,Part[] local,bool fallback,bool denial,bool useProximity)
    {
        IEnumerable<PartKey> candidates;
        if(grounded.Count>0 && useProximity){
            int Proximity(Account a) => a.Key.Source==row.Id ? 2 : a.SameVisit ? 1 : 0;
            int nearest = grounded.Max(Proximity);
            candidates = grounded.Where(a=>Proximity(a)==nearest).Select(a=>a.Key);
        }else if(grounded.Count>0) candidates=grounded.Select(a=>a.Key);
        else if(!fallback) return null;
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
            bool scoped = named.Success && !bare;
            string scope = scoped ? named.Groups[1].Value.Trim() : rest;
            bool plainMeant = clarify && marker.Value.Trim().Equals("I meant",StringComparison.OrdinalIgnoreCase);
            var reference = bare ? null : Reference(scope,scoped || plainMeant);
            // "I meant every word" and statements of intention are ordinary speech.
            if(plainMeant && reference?.Action==null){
                notes.Add(part);
                continue;
            }
            var inline = scoped ? rest[named.Length..].Trim(' ','.','!',',',':',';','—','–','-') : "";
            var next = inline.Length==0 && i+1<parts.Length ? parts[i+1] : null;
            if(next!=null && (next.Question || next.Gesture || Retraction.IsMatch(next.Text) || Clarification.IsMatch(next.Text))) next=null;
            string following = next?.Text ?? inline;
            bool actorOnly = EllipticalAnswer.IsMatch(following);
            var action = actorOnly ? null : Event(following);
            bool withdrawal = denial || marker.Value.Contains("take that back",StringComparison.OrdinalIgnoreCase) ||
                marker.Value.Contains("scratch that",StringComparison.OrdinalIgnoreCase);
            // A speaker can give a reason for taking back their words. Whether that reason
            // names the item or uses a pronoun does not change which grant was withdrawn.
            bool fallback = bare && (action==null || withdrawal && !action.ActorReplacement);
            bool describesTopic = reference is {Action:null,Topic.Length:>0} && action!=null &&
                (action.PronounObject || Includes(action.Object,reference.Topic));
            if(describesTopic && action is {PronounObject:true,Object.Length:0})
                action = action with {Object=Phrase(scope)};
            var local = notes.Where(p=>!p.Question && !p.Gesture && !p.Correction && p.Terms.Length>0 &&
                !p.Note.StartsWith("Answered ",StringComparison.Ordinal)).ToArray();
            var accounts = local.Select(p=>new Account(new(row.Id,row.Position,p.Index),p.Note,p.Terms,true)).ToList();
            var search = reference?.Search ?? (action==null ? [] : [action.Verb,..action.Object]);
            if(search.Length>0 || local.Length==0) await AddEarlierAccounts(conn,row,search,fallback,accounts,ct);
            var scopedAccounts = reference==null ? accounts : accounts.Where(reference.Matches).ToList();
            var continued = action==null ? [] : scopedAccounts.Where(a=>Describes(action,a.Note)).ToList();
            // A named action always constrains its own antecedent. A replacement supplying
            // the action for a named object must establish that event as well.
            var grounded = reference==null || describesTopic || continued.Count>0 ? continued : scopedAccounts;
            var target = ChooseTarget(row,grounded,accounts,local,fallback,denial,
                reference==null && action is {Object.Length:0});
            bool keepMarker = scoped || plainMeant || target==null;
            string detail = keepMarker ? part.Note : clarify ? "Clarified their account." :
                withdrawal ? "Withdrew their earlier account." : "Corrected their earlier account.";
            if(!keepMarker && rest.Length>0) detail += " " + Detail(rest,false,false,Terms(rest));
            bool repeatsMarker = reference?.Action is {} declared && action!=null &&
                (Describes(action,declared) || Describes(declared,action));
            bool joins = next!=null && (actorOnly && (target!=null || reference!=null) || continued.Count>0 ||
                target==null && (describesTopic || repeatsMarker));
            if(joins){
                detail += " " + next!.Note;
                part = part with {Terms=part.Terms.Concat(next.Terms).Distinct().ToArray()};
                i++;
            }
            if(target==null || target.Possible!=null) detail = "Unclear target. " + detail;
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
              AND p.detail !~ '^(Unclear target\.|Gestured|Made a gesture|Exchanged a few words|Answered (yes|no)\.)'
            """,("c",row.Campaign),("anchor",row.Anchor),("who",row.Speaker),("mode",row.Mode),("source",row.Id),
            ("age",row.Age),("day",row.Day),("at",row.At),("session",row.Session),("audience",row.Audience),("terms",Query(reference)),("fallback",fallback));
        await using var reader = await select.ExecuteReaderAsync(ct);
        while(await reader.ReadAsync(ct)) accounts.Add(new(new(reader.GetGuid(0),reader.GetInt32(1),reader.GetInt32(2)),
            reader.GetString(3),reader.GetString(4).Split(' '),reader.GetBoolean(5)));
    }
}
