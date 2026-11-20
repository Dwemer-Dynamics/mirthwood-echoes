public static class TalkTopics
{
    public const string Instructions="""
        Only when present in availableExtraCards, use Rumors for an explicit request to share gossip, Complain when the player is principally venting a grievance, or Cheer Up when the player is deliberately trying to lift this resident's spirits. Do not invent the content of a rumor. Use the ordinary topics for incidental mentions.
        Choose one topic for the player's principal spoken intent. Small Talk covers greetings, mundane observations, hesitant filler and any speech without a clear main subject. When unrelated topics carry equal weight, choose Small Talk. Leisure covers recreation or hobbies, Intellectual covers how things work or reasoned knowledge, Deep covers meaning, values or moral questions, and Dumb covers deliberately absurd chatter. A passing greeting, aside or narrated gesture does not outweigh a clear main question.
        Use None for a turn consisting entirely of actions between single asterisks. Use Other Interaction only when the entire spoken intent is a direct request for a gift, trade, romance or an errand. Discussing these subjects can still have an ordinary Talk topic.
        """;

    public static string Resolve(string? modelTopic,string words,System.Text.Json.Nodes.JsonArray? cards=null)
    {
        if(modelTopic is "Rumors" or "Complain" or "Cheer Up")return SilentAction(words)?"None":cards?.Any(x=>x?.ToString()==modelTopic)==true?modelTopic:"Small Talk";
        if(modelTopic is not ("Small Talk" or "Leisure" or "Intellectual" or "Deep" or "Dumb" or "None" or "Other Interaction"))
            throw new UserError("The model did not return a recognized Talk topic.",502);
        if(modelTopic=="Other Interaction"||SilentAction(words))return "None";
        return modelTopic=="None"?"Small Talk":modelTopic;
    }

    static bool SilentAction(string words)
    {
        var spans=Mirthwood.Echoes.ActionNotation.Find(words);
        int start=0;
        foreach(var span in spans){
            if(!words.AsSpan(start,span.Open-start).IsWhiteSpace())return false;
            start=span.End+1;
        }
        return spans.Count>0&&words.AsSpan(start).IsWhiteSpace();
    }
}
