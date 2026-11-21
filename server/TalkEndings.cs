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

    static string WithoutPartings(string text,string? listener,bool continuing,out string? farewell)
    {
        farewell=null;
        for(int cursor=text.Length;cursor>0;){
            var (start,end)=LastSentence(text[..cursor]);
            string sentence=text[start..end];
            string? phrase=KnownPhrase(sentence);
            int comma=sentence.LastIndexOf(',');
            if(phrase==null && listener!=null && comma>0 &&
                sentence[(comma+1)..].Trim().TrimEnd('.', '!', '…').Equals(listener,StringComparison.OrdinalIgnoreCase))
                phrase=KnownPhrase(sentence[..comma]);
            // "Good day." followed by another sentence can be a greeting.
            if(phrase!=null && (phrase!="Good day."||!continuing&&cursor==text.Length)){
                farewell??=sentence;
                text=RemoveSentence(text,start,end);
            }
            cursor=Math.Min(start,text.Length);
        }
        return text;
    }

    internal static (string Continuing,string Ending) Prepare(string answer,JsonNode? parting,string? listener=null)
    {
        string? chosen=parting is JsonValue value&&value.TryGetValue<string>(out var words)?KnownPhrase(words):null;
        string continuing=WithoutPartings(answer,listener,true,out _);
        string settled=WithoutPartings(answer,listener,false,out var embedded);
        // Keep the answer to the player's question. Omit a trailing question that invites another turn.
        while(settled.Length>0){
            var last=LastSentence(settled);
            if(last.End==last.Start||settled[last.End-1] is not ('?' or '？'))break;
            settled=RemoveSentence(settled,last.Start,last.End);
        }
        return (continuing.Length==0?"I hear you.":continuing,Join(settled,embedded??chosen??"Goodbye."));
    }
}
