using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

// A native quest choice needs evidence from the player's own decisive clause.
internal static class QuestDecision
{
    const RegexOptions MatchCase = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
    static readonly Regex Quotation = new("""["“][^"”]*(?:["”]|$)|(?<!\w)['‘].*?(?:['’](?!\w)|$)""", MatchCase);
    static readonly Regex Directive = new(@"\b(classif(y|ication)|return json|ignore (the |your )?instructions)\b", MatchCase);
    static readonly Regex Conditional = new(@"\b(?:if|unless|provided|suppose|assuming|hypothetically)\b", MatchCase);
    static readonly Regex Sentence = new(@"[^.!?;\r\n]+[.!?;]*");
    static readonly Regex ClauseBreak = new(@"[,—–:]|\s+(?:and|but|so|yet)\s+(?=(?:I|we|you|what|when|where|how|why|who|will|would|is|do|can|could)\b)", MatchCase);
    static readonly Regex Preface = new(@"^(?:(?:and|but|so|yet|then)\s+)+", MatchCase);
    static readonly Regex Question = new(@"^(?:what|which|when|where|how|why|who|would|could|can|do|did|does|is|are|will)\b", MatchCase);
    static readonly Regex WorkObject = new(@"^(?:(?:to|on|up)\s+)?(?:(?:the|this|your|that|original|offered)\s+)*(?:(?<detail>[\p{L} '-]+)\s+)?(?:job|work|task|quest|offer|terms)$",MatchCase);
    static readonly Regex OriginalTerms = new(@"\s+(?:as (?:originally )?offered|on (?:the|your|these|those) (?:original )?terms|for (?:this|the offered|the original) (?:job|task|quest))$",MatchCase);
    static readonly HashSet<string> SupplyVerbs=["bring","collect","gather","deliver","fetch","supply","provide"];
    static readonly Regex Objective = new(@"^(?<verb>[\p{L}]+)\s+(?<object>.+)$",MatchCase);
    static readonly Regex SupplyRequest = new(
        @"^(?:(?:(?:could|can|will|would) you|I need you to)\s+)?(?:please\s+)?(?<verb>"+
        string.Join("|",SupplyVerbs)+@")\s+(?:me\s+)?(?<object>.+)$",MatchCase);
    static readonly Regex ObjectAndRecipient = new(@"^(?<object>.+?)(?:\s+(?:to|for)\s+(?<recipient>.+))?$",MatchCase);
    static readonly Regex Determiners = new(@"^(?:(?:all of|all|some|a|an|the|that|this|these|those|your|requested)\s+)+",MatchCase);
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
            NamesJobDecision(ReadOffer(offer),"I will "+question.Groups["action"].Value,"accept");
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
        var offered=ReadOffer(offer);
        if(!decision)return subject=="job" && intent=="counter" &&
            BindsCounter(offered,scene,sentences,start,start+proof.Length,answeringOffer,result.S("termChange"));
        var contrary=intent=="accept"?"decline":"accept";
        if(clauses.Any(c=>!c.Indecisive && NamesJobDecision(offered,c.Words,contrary)))return false;

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
                clause.Words.Contains('\uFFFC') || clause.Indecisive)continue;
            var direct=intent=="accept"?Assent:Refusal;
            if(answersInvitation && DirectReply(direct,clause.Words) &&
                sentences.All(s=>s.Index<=clause.Start && clause.Start<s.Index+s.Length || s.Value.EndsWith('?') ||
                    Clauses(s).All(c=>DirectReply(direct,c.Words)||NamesJobDecision(offered,c.Words,intent))))return true;
            if(NamesJobDecision(offered,clause.Words,intent))return true;
        }
        return false;
    }

    static bool BindsCounter(NativeOffer offer,JsonObject? scene,MatchCollection sentences,int start,int end,bool pending,string term)
    {
        // A second counter can close the native job. Corroborate both sides of
        // the proposal, including when a comma or the order separates them.
        bool found=false,boundWork=false;
        foreach(Match sentence in sentences){
            var clauses=Clauses(sentence).ToArray();
            bool changes=false,unbound=false;
            foreach(var clause in clauses){
                foreach(var part in Conditional.Split(clause.Words).Select(s=>s.Trim()).Where(s=>s.Length>0)){
                    var change=ChangeRequest.Match(part);
                    if(change.Success && (term.Length==0 || change.Groups[term].Success)){
                        changes=true;
                        continue;
                    }
                    var commitment=Regex.Replace(part,@"^I(?:'d| would)\s+","I will ",MatchCase);
                    if(NamesJobDecision(offer,commitment,"accept") || NamesJobDecision(offer,commitment,"decline")){
                        boundWork=true;
                        continue;
                    }
                    if(pending && (DirectReply(Assent,part)||DirectReply(Refusal,part)))continue;
                    unbound=true;
                }
            }
            if(changes){
                if(unbound || clauses.Any(c=>c.Start<start || c.Start+c.Words.Length>end || c.Words.Contains('\uFFFC')))return false;
                found=true;
            }else if(unbound){
                // An independent question can accompany a counter. A statement
                // undertaking different work cannot supply its object.
                if(!sentence.Value.EndsWith('?') || clauses.Any(c=>Choice.IsMatch(c.Words)) ||
                    clauses.Any(c=>Conditional.IsMatch(c.Words)) && !EventWords(scene).Overlaps(Words(sentence.Value)))return false;
            }
        }
        return found && (pending || boundWork);
    }

    readonly record struct WorkStep(string Verb,string Object,string Recipient);
    sealed record NativeOffer(string Title,WorkStep[] Steps);

    static NativeOffer ReadOffer(JsonObject offer)
    {
        var steps=new List<WorkStep>();
        void Read(Match request){
            if(!request.Success)return;
            var verb=request.Groups["verb"].Value.ToLowerInvariant();
            var text=request.Groups["object"].Value.Trim();
            if(!SupplyVerbs.Contains(verb)){
                steps.Add(new(verb,text,""));
                return;
            }
            var target=ObjectAndRecipient.Match(text);
            var direct=target.Groups["object"].Value;
            if(ObjectName(direct).Name is "" or "them" or "it" or "these" or "those")return;
            foreach(var item in Regex.Split(direct,@"\s+and\s+",MatchCase))
                steps.Add(new(verb,item,target.Groups["recipient"].Value));
        }
        foreach(var objective in offer["objectives"]!.AsArray())Read(Objective.Match(objective!.ToString()));
        if(!steps.Any(s=>SupplyVerbs.Contains(s.Verb))){
            // A native imperative or request can name the supplies even if the
            // structured objectives only say to return to the giver. A word
            // elsewhere in the terms can never become the requested object.
            foreach(Match sentence in Sentence.Matches(offer.S("terms")))
                Read(SupplyRequest.Match(TrimProof(sentence.Value)));
        }
        return new(offer.S("title"),steps.ToArray());
    }

    static bool NamesJobDecision(NativeOffer offer,string words,string intent)
    {
        var statement=Choice.Match(words);
        if(!statement.Success)return false;
        string verb=(statement.Groups["choice"].Success?statement.Groups["choice"]:statement.Groups["verb"]).Value.ToLowerInvariant();
        bool refusal=verb is "decline" or "refuse";
        if(refusal && statement.Groups["negative"].Success)return false;
        bool negative=refusal||statement.Groups["negative"].Success;
        if(negative!=(intent=="decline"))return false;
        var remainder=OriginalTerms.Replace(words[statement.Length..].Trim(),"");
        if(verb is "accept" or "decline" or "refuse" or "agree" or "do" or "take" or "undertake" or "complete" or "handle"){
            var target=WorkObject.Match(Regex.Replace(remainder,@"\s+for you$","",MatchCase));
            if(target.Success){
                string name=target.Groups["detail"].Value;
                if(name.Length==0)return true;
                return MatchesObject(name,offer.Steps.Select(o=>o.Object).Append(offer.Title));
            }
        }
        // A promise to perform the actual action on its named object can identify
        // the job even without saying "job", as in "I'll bring the flour."
        if(!SupplyVerbs.Contains(verb))
            return MatchesObject(remainder,offer.Steps.Where(s=>s.Verb==verb).Select(s=>s.Object));
        bool bringsToGiver=remainder.StartsWith("you ",StringComparison.OrdinalIgnoreCase);
        var item=ObjectAndRecipient.Match(bringsToGiver ? remainder[4..] : remainder);
        if(bringsToGiver && item.Groups["recipient"].Success)return false;
        var recipient=bringsToGiver ? "you" : item.Groups["recipient"].Value;
        bool MatchesRecipient(WorkStep step)=>recipient.Length==0 ||
            recipient.Equals(step.Recipient,StringComparison.OrdinalIgnoreCase) ||
            step.Recipient.Length==0 && (recipient.Equals("you",StringComparison.OrdinalIgnoreCase) ||
                offer.Steps.Any(s=>s.Verb=="return"&&s.Object.Equals("to "+recipient,StringComparison.OrdinalIgnoreCase)));
        return MatchesObject(item.Groups["object"].Value,offer.Steps.Where(s=>SupplyVerbs.Contains(s.Verb)&&MatchesRecipient(s)).Select(s=>s.Object));
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

    static bool MatchesObject(string spoken,IEnumerable<string> names)
    {
        var native=names.Select(ObjectName).Where(o=>o.Name.Length>0).ToHashSet();
        bool Matches(string part){
            var asked=ObjectName(part);
            return asked.Name.Length>0 && native.Any(n=>n.Name==asked.Name && (asked.Count.Length==0 || n.Count==asked.Count));
        }
        // The noun following the native action is the thing the player undertook
        // to supply. "Flour recipes" and "flour" identify different things.
        return Matches(spoken) || Regex.Split(spoken,@"\s+and\s+",MatchCase).All(Matches);
    }
}
