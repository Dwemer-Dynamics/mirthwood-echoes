using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

// A native quest choice needs evidence from the player's own decisive clause.
internal static class QuestDecision
{
    const RegexOptions MatchCase = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
    static readonly Regex Quotation = new("""["“][^"”]*(?:["”]|$)|(?<!\w)['‘].*?(?:['’](?!\w)|$)""", MatchCase);
    static readonly Regex Directive = new(@"\b(classif(y|ication)|return json|ignore (the |your )?instructions)\b", MatchCase);
    static readonly Regex Conditional = new(@"\b(if|unless|provided|suppose|assuming|hypothetically)\b", MatchCase);
    static readonly Regex Sentence = new(@"[^.!?;\r\n]+[.!?;]*");
    static readonly Regex ClauseBreak = new(@"[,—–:]|\s+(?:and|but|so|yet)\s+(?=(?:I|we|you|what|when|where|how|why|who|will|would|is|do|can|could)\b)", MatchCase);
    static readonly Regex Preface = new(@"^(?:(?:and|but|so|yet|then)\s+)+", MatchCase);
    static readonly Regex Question = new(@"^(?:what|which|when|where|how|why|who|would|could|can|do|did|does|is|are|will)\b", MatchCase);
    static readonly Regex OfferWords = new(@"\b(job|work|task|quest|offer|terms|payment|pay|reward|deadline)\b", MatchCase);
    static readonly Regex Invitation = new(@"^(?:(?:will|would|could|can)\s+you\b|(?:are|do)\s+you\b.*\b(?:willing|able|want|like|ready)\b|(?:interested|willing|ready)\s+(?:in|to)\b)", MatchCase);
    static readonly Regex Assent = new(@"^(?:(?:yes|aye|sure)[, ]+)?(?:yes|aye|sure|agreed|okay|of course|absolutely|deal|I accept|I agree|I will|I'll do it|I'll take it|count on me|you can count on me)$", MatchCase);
    static readonly Regex Refusal = new(@"^(?:(?:no|sorry)[, ]+)?(?:no|no thanks|no thank you|I decline|I refuse|I won't|I can't|I'll pass|I won't do it|I can't do it)$", MatchCase);

    static string Straight(string words)=>words.Replace('’','\'');
    static string TrimProof(string words)=>words.Trim().TrimEnd('.',',','!','?',';',':').TrimEnd();

    internal static bool AsksForJob(string reply,string invitation,JsonObject offer)
    {
        // The model identifies its own invitation in this same call. Require the
        // actual last spoken question and a reference to the live job before saving it.
        return invitation.EndsWith('?') && Invitation.IsMatch(invitation) &&
            Sentence.Matches(reply).LastOrDefault()?.Value.Trim()==invitation &&
            (OfferWords.IsMatch(invitation)||MentionsNativeDetail(offer,null,invitation));
    }

    readonly record struct Clause(string Words,int Start,bool Indecisive,bool InformationQuestion);

    static IEnumerable<Clause> Clauses(Match sentence)
    {
        var text=TrimProof(sentence.Value);
        bool qualified=Conditional.IsMatch(text) && !sentence.Value.EndsWith('?');
        var offset=sentence.Index+sentence.Value.IndexOf(text,StringComparison.Ordinal);
        int position=0;
        foreach(var piece in ClauseBreak.Split(text)){
            int begin=text.IndexOf(piece,position,StringComparison.Ordinal);
            position=begin+piece.Length;
            var clause=Preface.Replace(piece.Trim(),"");
            if(clause.Length==0)continue;
            // "Yes, I'll attend the joust" makes one decision about attendance.
            if(position<text.Length && (clause.Equals("yes",StringComparison.OrdinalIgnoreCase)||
                clause.Equals("no",StringComparison.OrdinalIgnoreCase)))continue;
            int index=offset+begin+piece.LastIndexOf(clause,StringComparison.Ordinal);
            bool question=Question.IsMatch(clause)||position==text.Length&&sentence.Value.EndsWith('?');
            bool informationQuestion=question && Regex.IsMatch(clause,@"^(what|which|when|where|how|why|who)\b",MatchCase);
            yield return new(clause,index,question || qualified || Conditional.IsMatch(clause),informationQuestion);
        }
    }

    internal static bool Allows(JsonObject offer, JsonObject? scene, string speech, string evidence,
        JsonObject result, bool answeringOffer, bool aware)
    {
        string intent=result.S("intent"),subject=result.S("subject");
        speech=Straight(speech);
        evidence=Straight(evidence.Trim());
        if(evidence.Length==0 || !speech.Contains(evidence,StringComparison.OrdinalIgnoreCase) || Directive.IsMatch(speech))return false;
        bool decision=intent is "accept" or "decline";
        if(!aware)return !Regex.IsMatch(speech,"[“”\u0022]|(?:^|\\s)['‘].+['’](?=[\\s.,?!)]|$)",MatchCase) &&
            !(decision && Conditional.IsMatch(speech));
        if(subject is not ("" or "job"))return false;

        // Retain offsets and punctuation outside quotations. A quoted question cannot
        // provide the target for another clause or suppress a separate real answer.
        var unquoted=Quotation.Replace(speech,m=>m.Length>=2 && m.Value[^2] is '.' or '?' or '!'
            ? new string('\uFFFC',m.Length-1)+"." : new string('\uFFFC',m.Length));
        var proof=TrimProof(evidence);
        int start=unquoted.IndexOf(proof,StringComparison.OrdinalIgnoreCase);
        if(proof.Length==0 || start<0 ||
            unquoted.IndexOf(proof,start+proof.Length,StringComparison.OrdinalIgnoreCase)>=0)return false;
        var sentences=Sentence.Matches(unquoted);
        if(decision && sentences.Any(s=>!s.Value.EndsWith('?') && Regex.IsMatch(s.Value.TrimStart(),
            @"^(?:but\s+)?(?:only\s+)?(?:if|unless|provided|assuming)\b",MatchCase)))return false;
        var clauses=sentences.SelectMany(Clauses).ToArray();
        if((sentences.Count>1 || clauses.Length>1) && subject!="job")return false;

        // Only the verified clause can supply a job reference. The words of a
        // different sentence (including a question about a quest item) never qualify it.
        bool directAnswer=answeringOffer;
        foreach(var clause in clauses){
            bool answersInvitation=directAnswer;
            directAnswer=answeringOffer && clause.InformationQuestion;
            if(clause.Start<start || clause.Start+clause.Words.Length>start+proof.Length ||
                clause.Words.Contains('\uFFFC') || decision&&clause.Indecisive)continue;
            if((intent=="accept"&&Refusal.IsMatch(clause.Words))||(intent=="decline"&&Assent.IsMatch(clause.Words)))continue;
            if(answersInvitation && (intent=="accept"?Assent.IsMatch(clause.Words):intent=="decline"&&Refusal.IsMatch(clause.Words)))return true;
            if(MentionsNativeDetail(offer,scene,clause.Words,result.S("offerDetail")))return true;
            if(EventWords(scene).Overlaps(Words(clause.Words)))continue;
            if(OfferWords.IsMatch(clause.Words))return true;
            // A change to unstated terms can still be a counteroffer. It must be
            // explicitly classified as job-related and name no other advertised event.
            if(intent=="counter" && subject=="job" &&
                result.S("termChange") is "reward" or "amount" or "destination" or "deadline" or "work")return true;
        }
        return false;
    }

    static HashSet<string> Words(string value)=>Regex.Matches(value.ToLowerInvariant(),@"[\p{L}\p{N}]+")
        .Select(m=>m.Value).Where(w=>w.Length>2 && w is not ("the" or "and" or "for" or "with" or "from" or "this" or "that" or "your"))
        .ToHashSet(StringComparer.Ordinal);

    static HashSet<string> EventWords(JsonObject? scene)=>Words(string.Join(" ",
        (scene?["today"] as JsonArray??[]).Select(e=>e.S("title"))
        .Append(scene?["nextNotice"].S("title")).Append(scene?["previousNotice"].S("title"))
        .Concat((scene?["nextByKind"] as JsonObject??[]).Select(p=>p.Value.S("title")))));

    static bool MentionsNativeDetail(JsonObject offer,JsonObject? scene,string clause,string citedDetail="")
    {
        var detail=Words(offer.S("title")+" "+string.Join(" ",offer["objectives"]!.AsArray()));
        // Some generated offers put their named item or place only in the spoken terms.
        if(citedDetail.Length is >0 and <=160){
            var phrase=@"(?<!\w)"+Regex.Escape(citedDetail)+@"(?!\w)";
            if(Regex.IsMatch(offer.S("terms"),phrase,MatchCase) && Regex.IsMatch(clause,phrase,MatchCase))
                detail.UnionWith(Words(citedDetail));
        }
        detail.ExceptWith(["you","some","bring","collect","deliver","get","find","take","return","provide","help","needed"]);
        var words=Words(clause);
        var eventWords=EventWords(scene);
        detail.ExceptWith(eventWords);
        return detail.Overlaps(words);
    }
}
