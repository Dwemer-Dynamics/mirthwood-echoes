using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

// A change of audience can hide a delivered relay from the common transcript. Reopen only
// its attribution when the original player asks the resident how they know the same subject.
internal static class GroupRelay
{
    const RegexOptions Options=RegexOptions.IgnoreCase|RegexOptions.CultureInvariant;
    static readonly Regex Action=new(@"\*[^*]*\*");
    static readonly Regex Sentence=new(@"[.!?…;\r\n]+");
    static readonly Regex SourceQuestion=new(@"^(?:(?:so|and|then) )?(?:(?:who|whom|where|how)\b.{0,70}\b(?:tell|told|mention(?:ed)?|say|said|hear|heard|learn(?:ed)?|know|source)|(?:did|have|has|was|were|is)\b.{0,90}\b(?:hear|heard|mention(?:ed)?|told|tell|firsthand|yourself|there in person))\b",Options);
    static readonly Regex Confidence=new(@"\b(?:privat\w*|secret\w*|confiden\w*|between (?:us|you and me)|keep (?:quiet|it to)|(?:only|just) (?:tell|between)|(?:don.t|do not|never) (?:want|tell|say|share|repeat|mention))\b",Options);
    static readonly Regex Words=new(@"[^\p{L}\p{Nd}]+");
    static readonly Regex Reported=new(@"^(?:said|says|reported|told me|tells me|mentioned)(?: that)? (?:he|she|they) ",Options);
    // A single hobby object may have a proper name, as in "Grand Joust pennants".
    // Ordinary trailing prose cannot establish permission to disclose the later report.
    const string HobbyObject=@"(?:(?i:a|an|the) )?(?:\p{Lu}\p{L}* ){0,3}\p{L}+";
    static readonly Regex Pastime=new(@"^(?<activity>(?i:\p{L}+ing) "+HobbyObject+
        @") (?i:is (?:one of )?my (?:favou?rite )?(?:hobb(?:y|ies)|pastimes?|interests?))$",RegexOptions.CultureInvariant);
    static readonly Regex Sight=new(@"^(?:saw|spotted|noticed|observed|caught sight of) ",Options);
    static readonly Regex Proximity=new(@"\b(?:by|beside|near|close to|next to) (?=(?:a|an|the|that|this) )",Options);
    static readonly Regex ContractedNegative=new(@"\b(did|could)n['’]t\b",Options);
    static readonly Regex Negative=new(@"\b(?:no|not|never|neither|none|nothing|nobody|[a-z]+n t)\b",Options);
    static readonly Regex DeniedSight=new(@"\b(?:(?<scope>did not|could not) (?:see|spot|notice|observe)|(?<scope>never) (?:saw|spotted|noticed|observed))\b",Options);
    static readonly Regex AskedAboutSight=new(@"\b(?:see|saw|seen|spot|spotted|observe|observed)\b",Options);
    static readonly Regex OtherObservation=new(@"^(?:heard|found|met|visited|watched|witnessed) ",Options);
    static readonly Regex Interest=new(@"^(?i:i (?:collect|enjoy|like|love|study|play|read|paint|grow)) "+HobbyObject+"$",RegexOptions.CultureInvariant);
    static readonly Regex ExtraClause=new(@"\b(?:and|but|or|unless|until|though|although|because|however|if|when|whether|please|you|your|must|should|might|would|could|may)\b",Options);
    enum Attribution { Unrelated, Unresolved, Teller, NamedAccount, DeniedSighting }

    internal static JsonObject? Source(string question,JsonArray pairs)
    {
        if(pairs.Count==0)return null;
        var questions=Sentences(question);
        JsonObject? match=null;
        foreach(var pair in pairs.OfType<JsonObject>()){
            var recipient=Plain((string)pair["recipient"]!);
            var inquiry=questions.Select(line=>WithoutAddress(line,recipient)).Where(line=>SourceQuestion.IsMatch(line)).ToArray();
            if(inquiry.Length==0)continue;
            if(inquiry.Length!=1)return null;
            var attribution=RequestedAccount(pair,inquiry[0]);
            if(attribution==Attribution.Unrelated)continue;
            if(attribution==Attribution.Unresolved || match!=null)return null;
            match=new JsonObject{["source"]=pair["source"]!.DeepClone(),["heardFrom"]=pair["teller"]!.DeepClone(),
                ["addressedToYou"]=true,["subjectInCurrentLine"]=pair["subjects"]!.DeepClone()};
            if(attribution is Attribution.NamedAccount or Attribution.DeniedSighting)
                match["inReplyTo"]=new JsonObject{["source"]=pair["original"]!.DeepClone(),["speaker"]=pair["claimant"]!.DeepClone()};
            if(attribution==Attribution.DeniedSighting)match["reportedSighting"]="denied";
        }
        return match;
    }

    static string Tokens(string value)=>Words.Replace(ContractedNegative.Replace(value,"$1 not")," ").Trim();
    static string Plain(string value)=>Tokens(value).ToLowerInvariant();

    static string WithoutAddress(string line,string person)
    {
        if(line.StartsWith(person+" ",StringComparison.OrdinalIgnoreCase))return line[(person.Length+1)..];
        if(line.EndsWith(" "+person,StringComparison.OrdinalIgnoreCase))return line[..^(person.Length+1)];
        return line;
    }

    static string[] Sentences(string words,bool preserveCase=false)=>Sentence.Split(Action.Replace(words," "))
        .Select(text=>preserveCase?Tokens(text):Plain(text)).Where(text=>text.Length>0).ToArray();

    static bool HasUnboundClause(string words,string addressee,string trailingCourtesy)
    {
        // Remove only an actual vocative and a terminal courtesy. Keep internal clause
        // boundaries before normalizing words; a directive can follow an innocuous preface.
        string who=Regex.Escape(addressee);
        var address=$@"^\s*{who}\s*[,:—–]\s*|,\s*(?:{who}|{trailingCourtesy})\s*$";
        return Sentence.Split(Action.Replace(words," ")).Any(line=>
            Regex.Replace(line,address,"",Options).IndexOfAny([',',':','—','–'])>=0);
    }

    static string Account(string predicate)
    {
        if(!Sight.IsMatch(predicate))return predicate;
        return Proximity.Replace(Sight.Replace(predicate,"saw ",1),"near ");
    }

    // A teller may echo a separate detail volunteered before the report. Validate this whole
    // sentence separately; its words can neither identify the report nor enter the wider circle.
    static bool RepeatsPlayerDetail(string extra,string claimant,IEnumerable<string> earlier)
    {
        var subject=new[]{claimant,"he","she","they"}.FirstOrDefault(name=>extra.StartsWith(name+" ",StringComparison.Ordinal));
        if(subject==null)return false;
        var predicate=extra[(subject.Length+1)..];
        if(predicate.EndsWith(" you know",StringComparison.Ordinal))predicate=predicate[..^9];
        int space=predicate.IndexOf(' ');
        if(space<1)return false;
        string verb=predicate[..space],rest=predicate[space..];
        // Compare every word of the object. Permit the regular first-person, third-person or
        // gerund inflection of its verb in a self-description such as a favorite activity.
        var stems=new List<string>{verb};
        if(verb.EndsWith('s'))stems.Add(verb[..^1]);
        if(verb.EndsWith("es",StringComparison.Ordinal))stems.Add(verb[..^2]);
        if(verb.EndsWith("ies",StringComparison.Ordinal))stems.Add(verb[..^3]+"y");
        foreach(var line in earlier){
            if(stems.Any(stem=>line.Equals("i "+stem+rest,StringComparison.OrdinalIgnoreCase)))return true;
            var activity=Pastime.Match(line);
            if(activity.Success && stems.Any(stem=>stem.Length>0 &&
                new[]{stem+"ing",stem.TrimEnd('e')+"ing",stem+stem[^1]+"ing"}.Any(form=>
                    activity.Groups["activity"].Value.Equals(form+rest,StringComparison.OrdinalIgnoreCase))))return true;
        }
        return false;
    }

    static Attribution RequestedAccount(JsonObject pair,string inquiry)
    {
        string teller=Plain((string)pair["teller"]!),recipient=Plain((string)pair["recipient"]!),claimant=Plain((string)pair["claimant"]!);
        string request=(string)pair["request"]!,delivered=(string)pair["delivered"]!;
        var player=Sentences(request,preserveCase:true);
        var resident=Sentences(delivered);
        // The unqualified invitation applies to the immediately preceding first-person statement.
        if(teller.Length==0||recipient.Length==0||claimant.Length==0||player.Length<2)return Attribution.Unrelated;
        var name=Regex.Escape(recipient);
        var invitation=WithoutAddress(player[^1],teller);
        const string subject=@"(?:it|this|that|the news|what i (?:saw|heard|found|noticed|said))";
        string transfer=$@"(?:tell {name} (?:about {subject}|{subject})|let {name} know(?: about {subject})?|(?:pass|relay) {subject}(?: along| on)? to {name}|share {subject} with {name})";
        if(!Regex.IsMatch(invitation,$@"^(?:(?:will|would|can|could) you (?:please )?|please )?{transfer}(?: please)?$",Options))return Attribution.Unrelated;
        var original=WithoutAddress(player[^2],teller).ToLowerInvariant();
        if(!original.StartsWith("i ",StringComparison.Ordinal))return Attribution.Unrelated;
        var preface=player[..^2].Select(line=>WithoutAddress(line,teller)).ToArray();
        // Another permitted account of the subject remains a possible rival even when its
        // wording is too uncertain or private to identify its teller to the larger circle.
        if(resident.Length is <1 or >2 || Confidence.IsMatch(request)||Confidence.IsMatch(delivered)||delivered.Contains('?'))return Attribution.Unresolved;
        if(HasUnboundClause(request,(string)pair["teller"]!,"please") ||
           HasUnboundClause(delivered,(string)pair["recipient"]!,"you know"))return Attribution.Unresolved;
        // Account for the whole request before identifying either person to a new audience.
        // A single-clause self-description can accompany the request. Other directions or
        // qualifications have unknown disclosure scope, even when the final invitation is clear.
        if(preface.Any(line=>ExtraClause.IsMatch(line)|| !(Pastime.IsMatch(line)||Interest.IsMatch(line))))return Attribution.Unresolved;
        var retold=WithoutAddress(resident[0],recipient);
        bool named=retold.StartsWith(claimant+" ",StringComparison.Ordinal);
        var expected=Account(original[2..]);
        bool denied=Negative.IsMatch(expected);
        if(denied!=Negative.IsMatch(inquiry))return Attribution.Unresolved;
        if(denied){
            var denial=DeniedSight.Match(expected);
            var asked=DeniedSight.Match(inquiry);
            if(!denial.Success||denial.Index!=0||ExtraClause.IsMatch(expected[denial.Length..])||
                !asked.Success||asked.Groups["scope"].Value!=denial.Groups["scope"].Value)return Attribution.Unresolved;
            // The same negative verb can belong to the teller or another observer. Bind it
            // to the original claimant (the player asking), with its scope intact.
            string before=inquiry[..asked.Index].TrimEnd(),after=inquiry[(asked.Index+asked.Length)..];
            if(Negative.IsMatch(before)||Negative.IsMatch(after)||
                !(before.EndsWith(" "+claimant,StringComparison.Ordinal)||before.EndsWith(" i",StringComparison.Ordinal)))return Attribution.Unresolved;
        }else if(ExtraClause.IsMatch(expected) || !Sight.IsMatch(expected) &&
            (AskedAboutSight.IsMatch(inquiry)||!OtherObservation.IsMatch(expected)))return Attribution.Unresolved;
        string account=named?Account(Reported.Replace(retold[(claimant.Length+1)..],"",1)):retold;
        if(resident.Length==2){
            // Resolve one indefinite object only if the resulting whole report agrees.
            if(named && (account=="saw something"||account.StartsWith("saw something ",StringComparison.Ordinal)) &&
                resident[1].StartsWith("it was ",StringComparison.Ordinal))
                account="saw "+resident[1][7..]+account["saw something".Length..];
            else if(!RepeatsPlayerDetail(resident[1],claimant,preface))
                return Attribution.Unresolved;
        }
        if(named && account==expected)return denied?Attribution.DeniedSighting:Attribution.NamedAccount;
        if(named || denied)return Attribution.Unresolved;
        // An uncredited description can identify who told this listener without identifying
        // anyone as the witness. The complete observed object and place must still agree.
        var exists=Regex.Match(account,@"^there (?:was|were) (.+)$",Options);
        if(exists.Success && expected=="saw "+Proximity.Replace(exists.Groups[1].Value,"near "))return Attribution.Teller;
        return Attribution.Unresolved;
    }
}
