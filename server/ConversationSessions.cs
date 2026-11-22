using System.Text.Json.Nodes;
using Npgsql;

// These projections run inside CueHistory's save ancestry. Audience filtering happens
// before ordering, summarizing or opening a transcript, including after loading an older save.
public static class ConversationSessions
{
    public const string Lines = """
        WITH heard AS (
            SELECT e.id,e.payload->'line' AS line,(e.payload->>'day')::int AS day
            FROM ancestry h JOIN cue_events e ON e.campaign=@c AND e.id=h.id
            WHERE e.payload->>'kind'='group' AND e.payload->'line' ? 'words' AND (e.payload->'line'->'audience') ? @a
                AND (e.payload->>'day')::int<=@day
        ), lines AS (
            SELECT coalesce(t.cue_binding->>'conversation',t.id::text) AS session,'private' AS mode,
                t.id AS source,t.day,t.started AS at,0 AS seq,p.n,p.speaker,p.words,'turn' AS source_type,
                ARRAY['player',@a]::text[] AS audience,p.addressed
            FROM turns t CROSS JOIN LATERAL (VALUES
                (0,@a,t.quest_offer->'offer'->>'terms','player'),
                (1,'player',t.player_text,@a),(2,@a,t.reply,'player')) p(n,speaker,words,addressed)
            WHERE t.actor=@a AND t.day<=@day AND t.state='complete' AND nullif(p.words,'') IS NOT NULL
            UNION ALL
            SELECT coalesce(e.payload->'people'->0->'details'->>'conversation',d.id::text),'private',d.id,d.day,d.observed,0,0,@a,
                d.description,'event',ARRAY['player',@a],'player'
            FROM daily_events d LEFT JOIN cue_events e ON e.campaign=d.campaign AND e.id=d.cue_anchor
            WHERE d.actor=@a AND d.kind='address' AND d.day<=@day
            UNION ALL
            SELECT line->>'session','group',id,day,(line->>'at')::timestamptz,(line->>'seq')::int,0,
                line->>'speaker',line->>'words','group',ARRAY(SELECT jsonb_array_elements_text(line->'audience'))||ARRAY['player'],line->>'to'
            FROM heard
        ), named AS (
            SELECT l.*,CASE WHEN speaker='player' THEN coalesce(nullif(c.player_name,''),'Player') ELSE s.name END AS name
            FROM lines l JOIN campaigns c ON c.id=@c LEFT JOIN actors s ON s.campaign=@c AND s.key=l.speaker
        )
        """;

    // The summaries quote attributed source lines. A missing source on this branch
    // never contributes to a summary, regardless of when the journal last opened.
    const string Bucket = "CASE WHEN day>=@day THEN session WHEN mode='private' THEN 'day-'||day ELSE 'day-'||day||'-'||session END";
    const string Bucketed = Lines + ", bucketed AS (SELECT named.*,"+Bucket+" AS entry FROM named) ";

    public const string Sessions = Bucketed + """
        , ranked AS (
            SELECT *,row_number() OVER(PARTITION BY entry ORDER BY seq,at,source,n) AS first,
                row_number() OVER(PARTITION BY entry,speaker ORDER BY seq,at,source,n) AS speaker_first,
                row_number() OVER(PARTITION BY entry ORDER BY seq DESC,at DESC,source DESC,n DESC) AS last
            FROM bucketed
        ), highlights AS (
            SELECT *,row_number() OVER(PARTITION BY entry ORDER BY
                CASE WHEN last=1 THEN 0 WHEN first=1 THEN 1
                    WHEN EXISTS(SELECT FROM commitments p WHERE p.actor=@a AND (p.source_line=source OR p.turn_id=source)) THEN 2
                    WHEN speaker_first=1 THEN 3 ELSE 4 END,seq DESC,at DESC,n DESC) AS relevance
            FROM ranked
        )
        SELECT entry AS id,max(day) AS day,max(at) AS at,jsonb_build_object('kind',mode,'entries',count(*),
            'visits',count(DISTINCT session),'compacted',max(day)<@day,'knownStart',mode='group' OR bool_or(session<>source::text),
            'fingerprint',md5(string_agg(jsonb_build_array(session,source,n,name,words,relevance<=4)::text,',' ORDER BY seq,at,source,n)),
            'body',string_agg(name||' — '||left(words,180),E'\n' ORDER BY seq,at,source,n)
                FILTER(WHERE relevance<=4),'participants',
            (SELECT jsonb_agg(label ORDER BY label) FROM (
                SELECT DISTINCT CASE WHEN k='player' THEN coalesce(nullif(c.player_name,''),'Player') ELSE a.name END AS label
                FROM bucketed x CROSS JOIN LATERAL unnest(x.audience) k JOIN campaigns c ON c.id=@c
                LEFT JOIN actors a ON a.campaign=@c AND a.key=k WHERE x.entry=r.entry) audience))
            AS doc
        FROM highlights r GROUP BY entry,mode
        """;

    public const string Daily = """
        WITH ordered AS (
            SELECT *,row_number() OVER(PARTITION BY day ORDER BY observed,id) AS first,
                row_number() OVER(PARTITION BY day ORDER BY observed DESC,id DESC) AS last
            FROM daily_events WHERE actor=@a AND kind<>'address' AND day<=@day
        )
        SELECT day::text AS id,day,max(observed) AS at,jsonb_build_object('kind','daily','entries',count(*),
            'fingerprint',md5(string_agg(jsonb_build_array(id,left(description,190))::text,',' ORDER BY observed,id)),
            'body',string_agg(left(description,190),E'\n' ORDER BY observed,id) FILTER(WHERE first<=2 OR last=1))
            AS doc FROM ordered GROUP BY day
        """;

    internal static string WithRecaps(bool daily)
    {
        var query=daily?Daily:Sessions;
        string best=daily?"s.body":"coalesce(d.body,s.body)";
        return "SELECT r.id,r.day,r.at,(r.doc-'fingerprint')||jsonb_strip_nulls(jsonb_build_object('body',"+best+")) AS doc FROM ("+
            query+") r LEFT JOIN resident_recaps s ON s.campaign=@c AND s.actor=@a AND s.kind=r.doc->>'kind' AND s.entry=r.id AND s.fingerprint=r.doc->>'fingerprint'"+
            (daily?"":" LEFT JOIN dialogue_day_recaps d ON d.campaign=@c AND d.actor=@a AND d.entry=r.id AND d.fingerprint=r.doc->>'fingerprint'");
    }

    public static string Details(bool daily) => daily ? """
        SELECT coalesce(jsonb_agg(jsonb_build_object('text',description,'source',id,'sourceType','event','day',day)
            ORDER BY observed,id),'[]')::text FROM daily_events
        WHERE actor=@a AND kind<>'address' AND day::text=@entry AND day<=@day
        """ : Bucketed + """
        SELECT coalesce(jsonb_agg(jsonb_strip_nulls(jsonb_build_object('speaker',l.name,'speakerKey',l.speaker,'visit',l.session,'text',l.words,'source',l.source,'sourceType',l.source_type,
            'day',l.day,'audience',(SELECT jsonb_agg(CASE WHEN k='player' THEN coalesce(nullif(c.player_name,''),'Player') ELSE a.name END ORDER BY k)
                FROM unnest(l.audience) k JOIN campaigns c ON c.id=@c LEFT JOIN actors a ON a.campaign=@c AND a.key=k),
            'to',CASE WHEN addressed='group' THEN 'Everyone' WHEN addressed='player' THEN (SELECT coalesce(nullif(player_name,''),'Player') FROM campaigns WHERE id=@c)
                ELSE (SELECT a.name FROM actors a WHERE a.campaign=@c AND a.key=addressed) END,
            'nativeHandoff',CASE WHEN t.talk->>'spokenReturn' IS DISTINCT FROM 'true' THEN t.talk->'handoff'->>'cue' END,
            'questOutcome',CASE t.quest_offer->>'action' WHEN 'accept' THEN 'Quest accepted' WHEN 'decline' THEN 'Quest declined' END,
            'commitmentReview',t.commitment_review,'turningPoint',l.source_type='event' OR t.id IS NOT NULL,
            'promises',(SELECT jsonb_agg(jsonb_strip_nulls(jsonb_build_object('speaker',p.speaker,'speakerLabel',p.speaker_label,
                'quote',p.statement,'proposal',p.proposal)) ORDER BY p.recorded,p.id)
                FROM commitments p WHERE p.actor=@a AND (p.turn_id=t.id OR p.source_line=l.source))))
            ORDER BY l.seq,l.at,l.source,l.n),'[]')::text FROM bucketed l
            LEFT JOIN turns t ON l.source_type='turn' AND l.n=2 AND t.id=l.source WHERE l.session=@entry OR l.entry=@entry
        """;

    public static JsonObject ReadGroup(JsonNode? value)
    {
        if(value is not JsonObject line)throw new UserError("Missing group line.");
        var session=CueHistory.OptionalId(line["session"])??throw new UserError("Missing group conversation.");
        if(line["audience"] is not JsonArray audience || audience.Count is <1 or >3)throw new UserError("Invalid group audience.");
        var heard=audience.Select(node=>node is JsonValue value&&value.TryGetValue<string>(out var key)?key:"").ToArray();
        if(heard.Any(key=>key.Length is 0 or >64 || key is "player" or "group") || heard.Distinct().Count()!=heard.Length)
            throw new UserError("Invalid group resident.");
        string speaker=line.Required("speaker",64),to=line.Required("to",64);
        if((speaker!="player" && !heard.Contains(speaker)) || (to is not ("group" or "player") && !heard.Contains(to)) || to==speaker)
            throw new UserError("A group speaker or addressee is absent.");
        if(line.I("seq")<1 || !DateTimeOffset.TryParse(line.S("at"),out var at))throw new UserError("Invalid group line order.");
        var result=new JsonObject{["session"]=session.ToString(),["seq"]=line.I("seq"),["speaker"]=speaker,["to"]=to,
            ["words"]=line.Required("words",1200),["at"]=at.UtcDateTime.ToString("O"),["audience"]=audience.DeepClone()};
        var learned=CharacterKnowledge.Hints(line["learned"]);
        if(learned.Count>0)result["learned"]=learned;
        if(line["promise"] is JsonValue quote && quote.TryGetValue<string>(out var statement) && statement.Length is >0 and <=320 &&
            DialogueOutput.IsWholeQuote(result.S("words"),statement))result["promise"]=statement;
        JsonObject? Quote(JsonNode? data)=>data is JsonObject obj && CueHistory.OptionalId(obj["source"]) is Guid id
            ?new JsonObject{["source"]=id.ToString(),["quote"]=obj.Required("quote",320)}:null;
        if(Quote(line["priorPromise"]) is JsonObject prior){
            if(Quote(line["priorPromise"]?["proposal"]) is JsonObject proposal)prior["proposal"]=proposal;
            result["priorPromise"]=prior;
        }
        if(result["promise"]!=null&&Quote(line["proposal"]) is JsonObject offer)result["proposal"]=offer;
        return result;
    }

    internal static async Task RecordPromises(NpgsqlConnection conn,string campaign,Guid id,Guid? parent,int day,JsonObject line)
    {
        async Task<JsonNode?> Earlier(JsonNode evidence,JsonNode spoken){
            if(parent==null)return null;
            await using var find=Database.Command(conn,"""
                WITH RECURSIVE past AS (
                    SELECT e.* FROM cue_events e WHERE campaign=@c AND id=@parent
                    UNION ALL SELECT e.* FROM cue_events e JOIN past p ON p.parent=e.id AND e.campaign=@c)
                SELECT payload::text FROM past WHERE id=@source AND payload->>'kind'='group'
                    AND payload->'line'->>'session'=@session AND (payload->'line'->>'seq')::int<@seq
                    AND (@speaker='player' OR (payload->'line'->'audience') ? @speaker)
                """,("c",campaign),("parent",parent),("source",CueHistory.OptionalId(evidence["source"])),("session",line.S("session")),
                ("seq",spoken.I("seq")),("speaker",spoken.S("speaker")));
            return await find.ExecuteScalarAsync() is string raw?JsonNode.Parse(raw):null;
        }
        async Task Store(Guid source,JsonNode spoken,string quote,int spokenDay,JsonNode? proposal){
            if(!DialogueOutput.IsWholeQuote(spoken.S("words"),quote))return;
            var offer=proposal==null?null:await Earlier(proposal,spoken);
            if(offer!=null && (offer["line"].S("speaker")==spoken.S("speaker")||
                offer["line"].S("to")!="group"&&offer["line"].S("to")!=spoken.S("speaker")||
                !DialogueOutput.IsWholeQuote(offer["line"].S("words"),proposal.S("quote"))))offer=null;
            bool needsTerms=DialogueOutput.RequiresProposal(quote);
            if(needsTerms&&offer==null)return;
            int? due=Conversation.DueTomorrow(quote,spokenDay);
            int? offerDue=offer==null?null:Conversation.DueTomorrow(proposal.S("quote").TrimEnd('?'),offer.I("day"));
            await using var cmd=Database.Command(conn,"""
                WITH RECURSIVE past(id,parent) AS (
                    SELECT e.id,e.parent FROM cue_events e WHERE e.campaign=@c AND e.id=@id
                    UNION ALL SELECT e.id,e.parent FROM cue_events e JOIN past p ON p.parent=e.id AND e.campaign=@c)
                INSERT INTO commitments(id,campaign,actor,day,speaker,statement,group_anchor,source_line,speaker_label,addressed_to,proposal,proposal_line,due)
                SELECT md5(@id::text||'/'||@source::text||'/'||w.key)::uuid,@c,w.key,@day,@speaker,@quote,@id,@source,
                    CASE WHEN @speaker='player' THEN c.player_name ELSE speaker.name END,
                    CASE WHEN @target='player' THEN c.player_name WHEN @target='group' THEN 'Everyone' ELSE recipient.name END,
                    CASE WHEN @offerHeard::jsonb ? w.key THEN @proposal::text END,
                    CASE WHEN @offerHeard::jsonb ? w.key THEN @offer::uuid END,
                    coalesce(@due::int,CASE WHEN @offerHeard::jsonb ? w.key THEN @offerDue::int END)
                FROM jsonb_array_elements_text(@heard::jsonb) w(key) JOIN campaigns c ON c.id=@c
                LEFT JOIN actors speaker ON speaker.campaign=@c AND speaker.key=@speaker
                LEFT JOIN actors recipient ON recipient.campaign=@c AND recipient.key=@target
                WHERE (NOT @needsTerms OR @offerHeard::jsonb ? w.key) AND NOT EXISTS(SELECT FROM commitments old JOIN past ON old.group_anchor=past.id
                    WHERE old.campaign=@c AND old.actor=w.key AND old.source_line=@source)
                ON CONFLICT(id) DO NOTHING
                """,("source",source),("c",campaign),("day",spokenDay),("speaker",spoken.S("speaker")),("target",spoken.S("to")),
                ("quote",quote),("id",id),("heard",spoken["audience"]!.ToJsonString()),
                ("offerHeard",offer?["line"]?["audience"]?.ToJsonString()??"[]"),("proposal",proposal.S("quote")),
                ("offer",offer==null?null:CueHistory.OptionalId(proposal?["source"])),("needsTerms",needsTerms),("due",due),("offerDue",offerDue));
            await cmd.ExecuteNonQueryAsync();
        }
        if(line["promise"]!=null)await Store(id,line,line.S("promise"),day,line["proposal"]);
        if(parent==null||line["priorPromise"] is not JsonObject prior)return;
        var packet=await Earlier(prior,line);
        if(packet!=null)await Store(Guid.Parse(prior.S("source")),packet["line"]!,prior.S("quote"),packet.I("day"),prior["proposal"]);
    }
}
