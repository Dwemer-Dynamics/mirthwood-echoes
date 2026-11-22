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
    static readonly Regex WorkObject = new(@"^(?:(?:to|on|up)\s+)?(?:(?:the|this|your|that|original|offered)\s+)*(?:(?<detail>[\p{L} '-]+)\s+)?(?:job|work|task|quest|offer|terms)$",MatchCase);
    static readonly Regex OriginalTerms = new(@"\s+(?:as (?:originally )?offered|on (?:the|your) (?:original )?terms|for you)$",MatchCase);
    static readonly HashSet<string> SupplyVerbs=["bring","collect","gather","deliver","fetch","supply","provide"];
    static readonly Regex Objective = new(@"^(?<verb>[\p{L}]+)\s+(?<object>.+)$",MatchCase);
    static readonly Regex Determiners = new(@"^(?:(?:you|all of|all|some|a|an|the|that|this|these|those|your|requested)\s+)+",MatchCase);
    static readonly string[] Counts=["","one","two","three","four","five","six","seven","eight","nine","ten","eleven","twelve"];
    static readonly Regex Quantity = new(@"^(?:(?<count>\d+)\s*(?:[x×]\s*)?|(?<count>"+string.Join("|",Counts.Skip(1))+@")\s+)(?=[\p{L}])",MatchCase);
    static readonly Regex ChangeRequest = new(
        @"^(?:(?:(?:could|can|would|will) you|you(?: (?:could|can|would|will))?|please)\s+)?"+
        @"(?:(?<reward>pay (?:me )?(?:more|less|double|extra|twice as much|\d+(?: gold| coins?)?)|give me (?:more|double|extra)(?: (?:pay|gold|coins?))?)|"+
        @"(?:raise|lower|double|increase|reduce|change|extend|move) (?:the |your |my |our )?"+
        @"(?:(?<reward>pay|price|reward|payment)|(?<amount>amount|quantity)|(?<deadline>deadline)|(?<destination>destination)|(?<work>terms))(?: (?:by|to|until) .+)?)$",MatchCase);
    static readonly Regex Invitation = new(@"^(?:(?:will|would|could|can) you(?: (?:be (?:willing|able)|like) to)?|(?:are you (?:willing|able|ready)|do you want) to) (?<action>.+)\?$", MatchCase);
    static readonly Regex Assent = new(@"^(?:(?:yes|aye|sure)[, ]+)?(?:yes|aye|sure|agreed|okay|of course|absolutely|deal|I accept|I agree|I will|I'll do it|I'll take it|count on me|you can count on me)$", MatchCase);
    static readonly Regex Refusal = new(@"^(?:(?:no|sorry)[, ]+)?(?:no|no thanks|no thank you|I decline|I refuse|I won't|I can't|I'll pass|I won't do it|I can't do it)$", MatchCase);
    static readonly Regex OnTerms = new(@",?\s+on (?:those|these|your|the(?: original)?) terms$",MatchCase);
    static bool DirectReply(Regex polarity,string clause)=>polarity.IsMatch(OnTerms.Replace(clause,""));
    // Only a first-person choice or undertaking can operate a native button. Capture
    // both polarity and the verb so merely mentioning an item cannot authorize work.
    static readonly Regex Choice = new(
        @"^I\s+(?<choice>accept|decline|refuse|agree)\b|^I(?:(?<negative>\s+won't|\s+can't|\s+cannot|(?:'ll|\s+(?:will|can|shall|do))\s+not|\s+don't(?:\s+(?:want|agree)\s+to)?)|(?<positive>'ll|\s+will|\s+can|\s+promise\s+to|'m\s+going\s+to|\s+am\s+going\s+to))\s+(?<verb>[\p{L}]+)\b",MatchCase);

    static string Straight(string words)=>words.Replace('’','\'');
    static string TrimProof(string words)=>words.Trim().TrimEnd('.',',','!','?',';',':').TrimEnd();

    internal static bool AsksForJob(string reply,string invitation,JsonObject offer)
    {
        // The model identifies its own invitation in this same call. Require the
        // actual last spoken question and a reference to the live job before saving it.
        var question=Invitation.Match(invitation);
        return question.Success && Sentence.Matches(reply).LastOrDefault()?.Value.Trim()==invitation &&
            NamesJobDecision(offer,"I will "+question.Groups["action"].Value,"accept","");
    }

    readonly record struct Clause(string Words,int Start,bool Indecisive,bool Question,bool InformationQuestion);

    static IEnumerable<Clause> Clauses(Match sentence)
    {
        var text=TrimProof(sentence.Value);
        var offset=sentence.Index+sentence.Value.IndexOf(text,StringComparison.Ordinal);
        // "Yes, on those terms" is a single reply to the pending offer. Its stated
        // object must stay within the quoted proof just like a named quest item.
        if(OnTerms.IsMatch(text) && (DirectReply(Assent,text)||DirectReply(Refusal,text))){
            bool asks=sentence.Value.EndsWith('?');
            yield return new(text,offset,asks,asks,false);
            yield break;
        }
        int position=0;
        var pieces=ClauseBreak.Split(text);
        // An attached condition still qualifies the work when it ends in a question
        // mark. An independent question such as "Do you know if ...?" has its own verb.
        bool qualified=pieces.Any(p=>Conditional.IsMatch(p) && !Question.IsMatch(Preface.Replace(p.Trim(),"")));
        foreach(var piece in pieces){
            int begin=text.IndexOf(piece,position,StringComparison.Ordinal);
            position=begin+piece.Length;
            var clause=Preface.Replace(piece.Trim(),"");
            if(clause.Length==0)continue;
            // A comma-prefixed interjection takes the object of what follows it.
            // A standalone direct reply keeps its own sentence and may answer the offer.
            if(position<text.Length && (Assent.IsMatch(clause)||Refusal.IsMatch(clause)))continue;
            int index=offset+begin+piece.LastIndexOf(clause,StringComparison.Ordinal);
            bool question=Question.IsMatch(clause)||position==text.Length&&sentence.Value.EndsWith('?');
            bool informationQuestion=question && Regex.IsMatch(clause,@"^(what|which|when|where|how|why|who)\b",MatchCase);
            yield return new(clause,index,question || qualified,question,informationQuestion);
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
        if(decision && sentences.Any(s=>Regex.IsMatch(s.Value.TrimStart(),
            @"^(?:but\s+)?(?:only\s+)?(?:if|unless|provided|assuming)\b",MatchCase) &&
            !(s.Value.EndsWith('?') && ClauseBreak.Split(s.Value).Any(p=>Question.IsMatch(p.TrimStart())))))return false;
        var clauses=sentences.SelectMany(Clauses).ToArray();
        if((sentences.Count>1 || clauses.Length>1) && subject!="job")return false;
        string detail=result.S("offerDetail");
        if(decision){
            var contrary=intent=="accept"?"decline":"accept";
            if(clauses.Any(c=>!c.Indecisive && NamesJobDecision(offer,c.Words,contrary,detail)))return false;
        }

        // Only the verified clause can supply a job reference. The words of a
        // different sentence (including a question about a quest item) never qualify it.
        bool directAnswer=answeringOffer,previousQuestion=false;
        foreach(var clause in clauses){
            bool explicitChoice=clause.Words.Equals("I accept",StringComparison.OrdinalIgnoreCase) ||
                clause.Words.Equals("I decline",StringComparison.OrdinalIgnoreCase) || clause.Words.Equals("I refuse",StringComparison.OrdinalIgnoreCase);
            bool answersInvitation=directAnswer || answeringOffer && previousQuestion && explicitChoice;
            directAnswer=answeringOffer && clause.InformationQuestion;
            previousQuestion=clause.Question;
            if(clause.Start<start || clause.Start+clause.Words.Length>start+proof.Length ||
                clause.Words.Contains('\uFFFC') || decision&&clause.Indecisive)continue;
            if(decision){
                var direct=intent=="accept"?Assent:Refusal;
                if(answersInvitation && DirectReply(direct,clause.Words) &&
                    sentences.All(s=>s.Index<=clause.Start && clause.Start<s.Index+s.Length || s.Value.EndsWith('?') ||
                        Clauses(s).All(c=>DirectReply(direct,c.Words)||NamesJobDecision(offer,c.Words,intent,detail))))return true;
                if(NamesJobDecision(offer,clause.Words,intent,detail))return true;
            }else if(subject=="job" && !EventWords(scene).Overlaps(Words(clause.Words)) &&
                ProposesChange(clause.Words,result.S("termChange")))return true;
        }
        return false;
    }

    static bool ProposesChange(string clause,string term)
    {
        // Repeated counters can decline the native job. A model's term category
        // cannot turn an unrelated condition into a request to change the offer.
        var condition=Conditional.Match(clause);
        var proposed=condition.Success ? clause[(condition.Index+condition.Length)..].Trim() : clause;
        var change=ChangeRequest.Match(proposed);
        return change.Success && (term.Length==0 || change.Groups[term].Success);
    }

    static bool NamesJobDecision(JsonObject offer,string words,string intent,string citedDetail)
    {
        var statement=Choice.Match(words);
        if(!statement.Success)return false;
        string verb=(statement.Groups["choice"].Success?statement.Groups["choice"]:statement.Groups["verb"]).Value.ToLowerInvariant();
        bool refusal=verb is "decline" or "refuse";
        if(refusal && statement.Groups["negative"].Success)return false;
        bool negative=refusal||statement.Groups["negative"].Success;
        if(negative!=(intent=="decline"))return false;
        var remainder=words[statement.Length..].Trim();
        var objectives=offer["objectives"]!.AsArray().Select(o=>Objective.Match(o!.ToString())).Where(m=>m.Success).ToArray();
        if(verb is "accept" or "decline" or "refuse" or "agree" or "do" or "take" or "undertake" or "complete" or "handle"){
            var target=WorkObject.Match(OriginalTerms.Replace(remainder,""));
            if(target.Success){
                string name=target.Groups["detail"].Value;
                if(name.Length==0)return true;
                return MatchesObject(offer,name,citedDetail,objectives.Select(o=>o.Groups["object"].Value).Append(offer.S("title")));
            }
        }
        // A promise to perform the actual action on its named object can identify
        // the job even without saying "job", as in "I'll bring the flour."
        var targetPhrase=Regex.Match(remainder,@"^(?<object>.+?)(?:\s+(?<qualifier>(?:to|for|about|because|instead|before|after|with)\s+.+))?$",MatchCase);
        var qualifier=targetPhrase.Groups["qualifier"].Value;
        if(qualifier.Length>0 && !qualifier.Equals("to you",StringComparison.OrdinalIgnoreCase) &&
            !qualifier.Equals("for you",StringComparison.OrdinalIgnoreCase) &&
            !offer.S("terms").Contains(qualifier,StringComparison.OrdinalIgnoreCase))return false;
        bool SameAction(string native)=>native.Equals(verb,StringComparison.OrdinalIgnoreCase) ||
            SupplyVerbs.Contains(verb)&&SupplyVerbs.Contains(native.ToLowerInvariant());
        var names=objectives.Where(o=>SameAction(o.Groups["verb"].Value)).Select(o=>o.Groups["object"].Value).ToArray();
        if(names.Length==0 && !(SupplyVerbs.Contains(verb)&&SupplyVerbs.Overlaps(Words(offer.S("terms")))))return false;
        return MatchesObject(offer,targetPhrase.Groups["object"].Value,citedDetail,names);
    }

    static HashSet<string> Words(string value)=>Regex.Matches(value.ToLowerInvariant(),@"[\p{L}\p{N}]+")
        .Select(m=>m.Value).Where(w=>w.Length>2 && w is not ("the" or "and" or "for" or "with" or "from" or "this" or "that" or "your"))
        .ToHashSet(StringComparer.Ordinal);

    static HashSet<string> EventWords(JsonObject? scene)=>Words(string.Join(" ",
        (scene?["today"] as JsonArray??[]).Select(e=>e.S("title"))
        .Append(scene?["nextNotice"].S("title")).Append(scene?["previousNotice"].S("title"))
        .Concat((scene?["nextByKind"] as JsonObject??[]).Select(p=>p.Value.S("title")))));

    static (string Name,string Count) ObjectName(string phrase)
    {
        string name=Determiners.Replace(phrase.Trim(),"").ToLowerInvariant();
        var quantity=Quantity.Match(name);
        if(!quantity.Success)return (name,"");
        var count=quantity.Groups["count"].Value;
        var number=Array.IndexOf(Counts,count);
        return (name[quantity.Length..],number>0?number.ToString():count);
    }

    static bool MatchesObject(JsonObject offer,string spoken,string citedDetail,IEnumerable<string> names)
    {
        var native=names.Select(ObjectName).Where(o=>o.Name.Length>0).ToHashSet();
        // Some native offers name the object only in their prose. A quoted detail
        // must be that entire object and also occur verbatim in the original terms.
        if(citedDetail.Length is >0 and <=160 && Regex.IsMatch(offer.S("terms"),@"(?<!\w)"+Regex.Escape(citedDetail)+@"(?!\w)",MatchCase)){
            var cited=ObjectName(citedDetail);
            if(cited.Name.Length>0 && (native.Count==0 || native.Contains(cited) || native.Contains((cited.Name,""))))native.Add(cited);
        }
        bool Matches(string part){
            var asked=ObjectName(part);
            return asked.Name.Length>0 && native.Any(n=>n.Name==asked.Name && (asked.Count.Length==0 || n.Count==asked.Count));
        }
        // The noun following the native action is the thing the player undertook
        // to supply. "Flour recipes" and "flour" identify different things.
        return Matches(spoken) || Regex.Split(spoken,@"\s+and\s+",MatchCase).All(Matches);
    }
}
