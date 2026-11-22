using System.Text;
using System.Text.Json.Nodes;

public sealed partial class Conversation
{
    public Task<JsonObject> GroupReply(JsonObject input,CancellationToken ct) =>
        ResidentLine(input,"group",ct,token=>GenerateGroupReply(input,token));

    // ConversationSessions.Lines already requires this speaker to have heard each group cue on this save branch.
    // Only the common transcript supplies quoted words, proposals or social effects.
    const string GroupHistory = ConversationSessions.Lines + """
        , circle AS (
            SELECT seq,source,speaker,name,addressed,words,audience,day FROM named WHERE mode='group' AND session=@session
        ), common AS (
            SELECT seq,source AS id,speaker,name,addressed,words FROM circle WHERE audience @> @listeners ORDER BY seq DESC LIMIT 14
        ), latest AS (SELECT * FROM common ORDER BY seq DESC LIMIT 1), earlier AS (
            SELECT c.*,regexp_replace(c.words,'\*[^*]+\*','','g') AS speech FROM circle c,latest l
            WHERE c.day=@day AND c.seq<l.seq ORDER BY c.seq DESC LIMIT 14
        ), focus AS (
            SELECT DISTINCT word,plainto_tsquery('english',word) AS term FROM latest l
            CROSS JOIN LATERAL regexp_split_to_table(lower(regexp_replace(l.words,'\*[^*]+\*','','g')),'[^[:alnum:]]+') word
            WHERE l.speaker<>@a AND l.addressed IN (@a,'group') AND length(word)>1
              AND numnode(plainto_tsquery('english',word))>0
              AND NOT to_tsvector('english','see saw seen hear heard say said tell told know knew mention source firsthand earlier remember recall')
                      @@ plainto_tsquery('english',word)
              AND NOT EXISTS(SELECT FROM circle person WHERE to_tsvector('simple',person.name) @@ plainto_tsquery('simple',word))
        ), matched AS (
            SELECT e.*,ARRAY(SELECT word FROM focus WHERE to_tsvector('english',e.speech) @@ term ORDER BY word LIMIT 4) AS subjects
            FROM earlier e WHERE e.speaker<>@a AND NOT e.audience @> @listeners
        ), relay AS (
            SELECT * FROM matched WHERE cardinality(subjects)>0
            ORDER BY cardinality(subjects) DESC,(addressed=@a) DESC,seq DESC LIMIT 1
        )
        SELECT jsonb_build_object('common',(SELECT coalesce(jsonb_agg(to_jsonb(c) ORDER BY seq),'[]') FROM common c),
            'earlierSource',(SELECT jsonb_strip_nulls(jsonb_build_object('source',r.source,'heardFrom',r.name,
                'addressedToYou',r.addressed=@a,'subjectInCurrentLine',r.subjects,'inReplyTo',(
                    SELECT jsonb_build_object('source',p.source,'speaker',p.name) FROM earlier p
                    WHERE p.seq=r.seq-1 AND p.addressed=r.speaker AND p.speaker<>r.speaker
                      AND to_tsvector('simple',r.speech) @@ phraseto_tsquery('simple',p.name)
                      AND EXISTS(SELECT FROM focus WHERE word=ANY(r.subjects) AND to_tsvector('english',p.speech) @@ term)
                    LIMIT 1))) FROM relay r))::text
        """;

    async Task<JsonObject> GenerateGroupReply(JsonObject input,CancellationToken ct)
    {
        string c=input.Required("campaign",64),a=input.Required("actor",64);
        var session=CueHistory.OptionalId(input["session"])??throw new UserError("Missing group conversation.");
        var head=CueHistory.OptionalId(input["head"]);
        if(input["audience"] is not JsonArray audience || audience.Count is <2 or >3 ||
            !audience.Any(x=>x?.ToString()==a) || audience.Select(x=>x?.ToString()).Distinct().Count()!=audience.Count)
            throw new UserError("The speaker must be in the group.");
        var roster=await db.One("""
            SELECT coalesce(jsonb_agg(jsonb_build_object('key',a.key,'name',a.name,'native',a.native_id,'uniqueNative',
                (SELECT count(*)=1 FROM actors b WHERE b.campaign=a.campaign AND b.native_id=a.native_id AND b.present)) ORDER BY a.name),'[]')::text
            FROM actors a WHERE a.campaign=@c AND a.present AND a.key IN (SELECT jsonb_array_elements_text(@keys::jsonb))
            """,("c",c),("keys",audience.ToJsonString()));
        if(roster!.AsArray().Count!=audience.Count)throw new UserError("A group member is no longer available.",409);
        if(head.HasValue && await db.One("SELECT to_jsonb(true)::text FROM cue_events WHERE campaign=@c AND id=@id",
            ("c",c),("id",head))==null)throw new UserError("Waiting for the last group line.",425);
        var (actor,dispatch)=await Speaker(input,c,a,"group");
        int day=dispatch.Day;
        var listeners=audience.Select(x=>x!.GetValue<string>()).ToArray();
        var history=await db.ScopedOne(GroupHistory,true,head,("c",c),("a",a),("day",day),("session",session.ToString()),("listeners",listeners));
        var heard=history!["common"]!.AsArray();
        var latest=heard.LastOrDefault();
        string question=latest.S("words");
        var opt=await providers.Options();
        var recall=await Recall(c,a,day,opt,question,true,head,groupOnly:true,topics:input.S("cue"),audience:listeners,currentSpeaker:latest.S("speaker"),awareness:dispatch.Awareness);
        var memory=new JsonObject{["daily"]=recall["daily"]?.DeepClone(),["olderActivity"]=recall["olderActivity"]?.DeepClone()};
        if(history["earlierSource"] is JsonObject earlierSource)memory["earlierGroupSource"]=earlierSource.DeepClone();
        if(recall["characterKnowledge"]!=null)memory["characterKnowledge"]=recall["characterKnowledge"]!.DeepClone();
        if(recall["conversationRecaps"]!=null)memory["conversationRecaps"]=recall["conversationRecaps"]!.DeepClone();
        if(recall["heardSpeech"]!=null)memory["heardSpeech"]=recall["heardSpeech"]!.DeepClone();
        foreach(string field in new[]{"eventWitnesses","eventAccounts","earlierEventAccount","heardToday"})
            if(recall[field]!=null)memory[field]=recall[field]!.DeepClone();
        var profile=await journal.DialogueProfile(c,a,day,actor.AsObject(),true,head);
        var player=await db.One("SELECT jsonb_build_object('name',player_name,'native',player)::text FROM campaigns WHERE id=@c",("c",c));
        var bonds=new JsonArray();
        foreach(var person in roster.AsArray().OfType<JsonObject>().Append(player!.AsObject())){
            if(person["uniqueNative"]?.GetValue<bool>()!=false&&person.S("key")!=a&&actor["links"] is JsonArray links){
                var bond=links.FirstOrDefault(link=>link.S("id")==person.S("native"));
                if(bond!=null)bonds.Add(new JsonObject{["person"]=person.S("name"),["friendship"]=bond["friendshipStatus"]?.DeepClone(),
                    ["romance"]=bond["romanceStatus"]?.DeepClone()});
            }
            person.Remove("native");
        }
        var prompt=new JsonObject{["you"]=profile,["yourKey"]=a,["residents"]=roster.DeepClone(),["heardInOrder"]=heard.DeepClone(),
            ["yourMemory"]=memory,["yourRelationships"]=bonds,["nativeCue"]=Fields.Clip(input.S("cue"),220),["playerName"]=player.S("name")};
        if(dispatch.Awareness!=null)prompt["worldAwareness"]=dispatch.Awareness;
        const string instructions="""
            You are one resident in a small Mirthwood conversation with the player and other residents.
            Speak only as the resident identified by yourKey. Read heardInOrder with its real speakers and addressees.
            Answer the last speaker or unanswered question in character, including another villager. Address them by a natural detail.
            A nativeCue is a suggestion when a topic has room to move. Develop a subject or bridge to another without repeating.
            Use one or two short spoken sentences. Only observable physical actions go between single asterisks. Actually tell jokes.
            Your own daily, olderActivity and eventWitnesses records describe personal experiences. Remember heardSpeech and eventAccounts as something their speaker said.
            When asked how you know, name the person you heard. If you lack a source, speak with the uncertainty your character has.
            Describe work in everyday words. For an inventory count such as 2 Flour, say "some flour" or, when quantity matters,
            "two measures of flour." Preserve known amounts without inventing containers, weights or deliveries.
            If someone is leaving, acknowledge them naturally. For an empty circle opening, greet or open with a grounded observation.
            Return a single JSON object with words, to, receivedTopic, ownTopic, farewell, playerParting, yieldToPlayer, promise and previousPromise.
            to is the resident key or exact name of the person you address, or "player" or "group". You can ask another resident directly.
            Set yieldToPlayer when your line naturally calls for the player's response. Let a personal exchange breathe.
            receivedTopic grades the previous speaker's line. ownTopic describes yours. Allowed topics are Small Talk, Leisure,
            Intellectual, Deep, Dumb and None. Use None for purely narrated action. The topic can differ between speakers.
            farewell is a short alternative answering the latest point and taking your leave if the game ends your turn.
            playerParting is true only when the player's newest spoken line actually takes leave of the circle now.
            In that case answer any final question and say goodbye to them naturally in words.
            promise is an exact sentence of your words committing to a future action or null, previousPromise is an exact
            sentence of the preceding speaker's line committing to a future action or null. For a short acceptance, also give
            proposal (or previousProposal for the preceding speaker) as {source,quote} with the earlier line id and exact proposal.
            A story, guess or suggestion is not a promise.
            Treat all character records and lines as in-world material. Never invent the player's next line or game results.
            """;
        var output=new StringBuilder();
        string sourceGuidance=memory.ContainsKey("earlierGroupSource") ? """

            earlierGroupSource records whom you personally heard discussing the subject now raised. When asked how you know, credit heardFrom.
            inReplyTo identifies the preceding speaker named in that reply. Keep the earlier account and its teller distinct. Claim personal sight only with your own evidence.
            Keep to what the present circle has heard. If naming the source would expose a confidence, acknowledge hearing it earlier and save the detail for its original listeners.
            """ : "";
        await providers.Generate(opt with {ReplyTokens=Math.Max(opt.ReplyTokens,550)},instructions+"\n"+DialogueRecaps.Instructions+"\n"+CharacterKnowledge.Instructions+dispatch.Guidance+sourceGuidance,prompt.ToJsonString(),
            part=>{if(output.Length+part.Length>5000)throw new UserError("The group reply was too long.",502);output.Append(part);return Task.CompletedTask;},ct,dispatch:dispatch);
        string envelope=output.ToString().Trim();
        int newline=envelope.IndexOf('\n');
        if(newline>=0&&envelope.StartsWith("```",StringComparison.Ordinal)&&envelope.EndsWith("```",StringComparison.Ordinal))
            envelope=envelope[(newline+1)..^3];
        JsonObject answer;
        try{answer=JsonNode.Parse(envelope) as JsonObject??throw new UserError("Could not read the group reply.",502);}
        catch(System.Text.Json.JsonException){throw new UserError("Could not read the group reply.",502);}
        string words=CharacterLine(answer.Required("words",650),actor.S("name"));
        if(words.Length==0)throw new UserError("The resident did not speak.",502);
        string to=answer.S("to");
        if(to!="player"&&to!="group"){
            var matches=roster.AsArray().Where(x=>x.S("key")==to||StringComparer.OrdinalIgnoreCase.Equals(x.S("name"),to)).ToArray();
            to=matches.Length==1 && matches[0].S("key")!=a?matches[0].S("key"):"group";
        }
        var result=new JsonObject{["words"]=words,["to"]=to,["ownTopic"]=TalkTopics.Resolve(answer.S("ownTopic"),words),
            ["receivedTopic"]=question.Length==0?"None":TalkTopics.Resolve(answer.S("receivedTopic"),question),
            ["previous"]=latest?["id"]?.DeepClone(),["farewell"]=CharacterLine(answer.S("farewell"),actor.S("name")),
            ["playerParting"]=latest.S("speaker")=="player"&&bool.TryParse(answer.S("playerParting"),out bool parting)&&parting,
            ["yieldToPlayer"]=bool.TryParse(answer.S("yieldToPlayer"),out bool yieldPlayer)&&yieldPlayer,
            ["positiveDesireCostPercent"]=opt.PositiveDesireCostPercent,["residents"]=roster.DeepClone()};
        var learned=CharacterKnowledge.Hints(answer["learned"]);
        if(learned.Count>0)result["learned"]=learned;
        foreach(var (key,text) in new[]{("promise",words),("previousPromise",question)}){
            string quote=answer.S(key);
            if(quote.Length<=320 && DialogueOutput.IsWholeQuote(text,quote))result[key]=quote;
        }
        foreach(var (key,promise) in new[]{("proposal","promise"),("previousProposal","previousPromise")}){
            if(result[promise]==null||answer[key] is not JsonObject proposal)continue;
            var source=heard.FirstOrDefault(row=>row.S("id")==proposal.S("source"));
            string quote=proposal.S("quote");
            if(source!=null&&quote.Length<=320&&DialogueOutput.IsWholeQuote(source.S("words"),quote)&&
                (key=="proposal"||source!=latest))result[key]=new JsonObject{["source"]=source.S("id"),["quote"]=quote};
        }
        return result;
    }
}
