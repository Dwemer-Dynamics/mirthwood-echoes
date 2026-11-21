using System.Text.Json.Nodes;
using Mirthwood.Echoes;

internal static class TalkEndings
{
    // A parting phrase can never introduce a new job, appointment or promise.
    static readonly string[] Phrases = [
        "Goodbye.", "Farewell.", "Good day.", "Take care.", "Be well.", "Until next time.",
        "Safe travels.", "Mind how you go.", "Goodbye for now.", "Take care, friend.",
        "I'd best be off.", "I'll leave you to it.", "See you around.", "Good night."
    ];
    internal static readonly string Instructions =
        "In farewells choose a short spoken parting for each reaction from these exact phrases: " +
        string.Join(" / ", Phrases) + " Read it after the actual answer. Choose what sounds natural for this resident and time of day. Even a warm reply can end with a plain goodbye. Use friend only when that familiarity is established.";

    static string? KnownPhrase(string words)
    {
        var plain=words.Trim().TrimEnd('.', '!', '…');
        return Array.Find(Phrases,phrase=>plain.Equals(phrase[..^1],StringComparison.OrdinalIgnoreCase));
    }
    internal static bool IsParting(string words)=>KnownPhrase(words)!=null;

    internal static bool KeepsAgreements(string answer,string ending,JsonNode? commitments)
    {
        if(commitments is not JsonArray marks)return true;
        foreach(var mark in marks){
            string quote=mark.S("quote");
            if(mark.S("speaker")=="npc" && quote.Length>0 && !IsParting(quote) &&
                answer.Contains(quote,StringComparison.Ordinal)!=ending.Contains(quote,StringComparison.Ordinal))return false;
        }
        return true;
    }

    // Find the last spoken sentence, leaving marked gestures (including trailing gestures) intact.
    static (int Start,int End) LastSentence(string text)
    {
        int start=0,end=text.Length;
        foreach(var action in ActionNotation.Find(text).AsEnumerable().Reverse()){
            if(!text.AsSpan(action.End+1,end-action.End-1).IsWhiteSpace()){start=action.End+1;break;}
            end=action.Open;
        }
        while(end>start&&char.IsWhiteSpace(text[end-1]))end--;
        for(int i=end-2;i>=start;i--)
            if(".!?…！？。".Contains(text[i])&&char.IsWhiteSpace(text[i+1])){start=i+1;break;}
        while(start<end&&char.IsWhiteSpace(text[start]))start++;
        return (start,end);
    }

    static string Join(string left,string right) =>
        left.Length==0?right:right.Length==0?left:left+" "+right;

    static string RemoveSentence(string text,int start,int end) =>
        Join(text[..start].TrimEnd(),text[end..].TrimStart());

    internal static (string Continuing,string Ending) Prepare(string answer,JsonNode? parting)
    {
        string? chosen=parting is JsonValue value&&value.TryGetValue<string>(out var words)?KnownPhrase(words):null;
        var last=LastSentence(answer);
        var embedded=KnownPhrase(answer[last.Start..last.End]);
        string settled=embedded==null?answer:RemoveSentence(answer,last.Start,last.End);
        // "Good day." can also be a greeting in a conversation that continues.
        string continuing=embedded=="Good day."?answer:settled;
        // Keep the answer to the player's question. Omit a trailing question that invites another turn.
        while(settled.Length>0){
            last=LastSentence(settled);
            if(last.End==last.Start||settled[last.End-1] is not ('?' or '？'))break;
            settled=RemoveSentence(settled,last.Start,last.End);
        }
        return (continuing.Length==0?"I hear you.":continuing,Join(settled,chosen??embedded??"Goodbye."));
    }
}
