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
    static readonly Regex Reported=new(@"^(?:said|says|reported|told me|tells me|mentioned)(?: that)? (?:he|she|they|i) ",Options);
    static readonly Regex Pastime=new(@"^(?<activity>.+) is (?:one of )?my (?:favou?rite )?(?:hobb(?:y|ies)|pastimes?|interests?)$",Options);
    static readonly Regex Sight=new(@"^(?:saw|spotted|noticed|observed|caught sight of) ",Options);
    static readonly Regex Proximity=new(@"\b(?:by|beside|near|close to|next to) (?=(?:a|an|the|that|this) )",Options);

    internal static JsonObject? Source(string question,JsonArray pairs)
    {
        if(pairs.Count==0)return null;
        var questions=Sentences(question);
        JsonObject? match=null;
        foreach(var pair in pairs.OfType<JsonObject>()){
            var recipient=Plain((string)pair["recipient"]!);
            if(!questions.Any(line=>SourceQuestion.IsMatch(WithoutAddress(line,recipient)))||!RequestedAccount(pair))continue;
            if(match!=null)return null;
            match=new JsonObject{["source"]=pair["source"]!.DeepClone(),["heardFrom"]=pair["teller"]!.DeepClone(),
                ["addressedToYou"]=true,["subjectInCurrentLine"]=pair["subjects"]!.DeepClone(),
                ["inReplyTo"]=new JsonObject{["source"]=pair["original"]!.DeepClone(),["speaker"]=pair["claimant"]!.DeepClone()}};
        }
        return match;
    }

    static string Plain(string value)=>Words.Replace(value.ToLowerInvariant()," ").Trim();

    static string WithoutAddress(string line,string person)
    {
        if(line.StartsWith(person+" ",StringComparison.Ordinal))return line[(person.Length+1)..];
        if(line.EndsWith(" "+person,StringComparison.Ordinal))return line[..^(person.Length+1)];
        return line;
    }

    static string[] Sentences(string words)=>Sentence.Split(Action.Replace(words," "))
        .Select(Plain).Where(text=>text.Length>0).ToArray();

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
            if(stems.Any(stem=>line=="i "+stem+rest))return true;
            var activity=Pastime.Match(line);
            if(activity.Success && stems.Any(stem=>stem.Length>0 &&
                new[]{stem+"ing",stem.TrimEnd('e')+"ing",stem+stem[^1]+"ing"}.Any(form=>activity.Groups["activity"].Value==form+rest)))return true;
        }
        return false;
    }

    static bool RequestedAccount(JsonObject pair)
    {
        string teller=Plain((string)pair["teller"]!),recipient=Plain((string)pair["recipient"]!),claimant=Plain((string)pair["claimant"]!);
        string request=(string)pair["request"]!,delivered=(string)pair["delivered"]!;
        var player=Sentences(request);
        var resident=Sentences(delivered);
        // The unqualified invitation applies to the immediately preceding first-person statement.
        if(teller.Length==0||recipient.Length==0||claimant.Length==0||player.Length<2||resident.Length is <1 or >2||
            Confidence.IsMatch(request)||Confidence.IsMatch(delivered)||delivered.Contains('?'))return false;
        if(resident.Length==2 && !RepeatsPlayerDetail(resident[1],claimant,player[..^2].Select(line=>WithoutAddress(line,teller))))return false;
        var name=Regex.Escape(recipient);
        var invitation=WithoutAddress(player[^1],teller);
        const string subject=@"(?:it|this|that|the news|what i (?:saw|heard|found|noticed|said))";
        string transfer=$@"(?:tell {name} (?:about {subject}|{subject})|let {name} know(?: about {subject})?|(?:pass|relay) {subject}(?: along| on)? to {name}|share {subject} with {name})";
        if(!Regex.IsMatch(invitation,$@"^(?:(?:will|would|can|could) you (?:please )?|please )?{transfer}(?: please)?$",Options))return false;
        var original=WithoutAddress(player[^2],teller);
        if(!original.StartsWith("i ",StringComparison.Ordinal))return false;
        var retold=WithoutAddress(resident[0],recipient);
        if(!retold.StartsWith(claimant+" ",StringComparison.Ordinal))return false;
        // The addressed cue and its preceding request identify both speakers. Bind the
        // reported subject independently, retaining its complete object, location and
        // qualifications through ordinary paraphrases of a visual observation.
        string account=Reported.Replace(retold[(claimant.Length+1)..],"",1);
        return Account(account)==Account(original[2..]);
    }
}
