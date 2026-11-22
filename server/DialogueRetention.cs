using System.Text.Json.Nodes;
using Npgsql;

internal static class DialogueRetention
{
    internal const int Days=30;
    internal const string Schema = """
        ALTER TABLE personality_turning_points ADD COLUMN IF NOT EXISTS evidence jsonb;
        CREATE OR REPLACE FUNCTION echoes_marked_sentence(words text,reason text) RETURNS text LANGUAGE sql IMMUTABLE STRICT AS $$
          SELECT trim(sentence) FROM regexp_split_to_table(words,'(?<=[.!?…])\s+') WITH ORDINALITY s(sentence,n)
          WHERE length(trim(sentence)) BETWEEN 3 AND 260 AND strpos(lower(reason),lower(trim(sentence)))>0 ORDER BY n LIMIT 1
        $$;
        CREATE OR REPLACE FUNCTION echoes_marked_observation(marked uuid,reason text) RETURNS jsonb LANGUAGE sql STABLE AS $$
          SELECT jsonb_strip_nulls(jsonb_build_object('observation',CASE WHEN d.kind<>'address' THEN d.description ELSE
            coalesce(echoes_marked_sentence(d.description,reason),(SELECT k.quote FROM character_knowledge k WHERE
              k.campaign=d.campaign AND k.holder=d.actor AND k.source=d.cue_anchor AND k.facet='major moment' ORDER BY k.speech_position LIMIT 1)) END))
          FROM daily_events d WHERE d.id=marked
        $$;
        CREATE OR REPLACE FUNCTION echoes_marked_evidence(marked uuid,reason text) RETURNS jsonb LANGUAGE sql STABLE AS $$
          SELECT coalesce(jsonb_object_agg(label,quote),'{}'::jsonb) FROM (
            SELECT DISTINCT ON(label) label,quote FROM (
              SELECT CASE WHEN k.speaker='player' THEN 'player' ELSE 'reply' END AS label,k.quote,1 AS priority
                FROM character_knowledge k JOIN turns t ON t.id=marked AND k.holder=t.actor AND k.campaign=t.campaign
                WHERE k.source=marked AND k.facet='major moment'
              UNION ALL SELECT CASE WHEN p.speaker='player' THEN 'player' ELSE 'reply' END,p.statement,2
                FROM commitments p WHERE p.turn_id=marked
              UNION ALL SELECT line.label,echoes_marked_sentence(line.words,reason),0 FROM turns t CROSS JOIN LATERAL
                (VALUES ('player',t.player_text),('reply',t.reply)) line(label,words) WHERE t.id=marked
            ) evidence WHERE quote IS NOT NULL ORDER BY label,priority,quote) selected
        $$;
        CREATE TABLE IF NOT EXISTS dialogue_clock(campaign text PRIMARY KEY REFERENCES campaigns, day int NOT NULL,
          load_id text NOT NULL, elapsed bigint NOT NULL DEFAULT 0);
        ALTER TABLE dialogue_clock ADD COLUMN IF NOT EXISTS retired_loads text[] NOT NULL DEFAULT '{}';
        ALTER TABLE turns ADD COLUMN IF NOT EXISTS dialogue_age bigint, ADD COLUMN IF NOT EXISTS dialogue_expired boolean NOT NULL DEFAULT false,
          ADD COLUMN IF NOT EXISTS knowledge_reviewed boolean NOT NULL DEFAULT false, ADD COLUMN IF NOT EXISTS request_digest bytea;
        ALTER TABLE cue_events ADD COLUMN IF NOT EXISTS dialogue_age bigint, ADD COLUMN IF NOT EXISTS dialogue_expired boolean NOT NULL DEFAULT false,
          ADD COLUMN IF NOT EXISTS knowledge_reviewed boolean NOT NULL DEFAULT false, ADD COLUMN IF NOT EXISTS packet_digest bytea;
        CREATE INDEX IF NOT EXISTS turns_memory_age ON turns(campaign,dialogue_age) WHERE NOT dialogue_expired;
        CREATE INDEX IF NOT EXISTS cues_memory_age ON cue_events(campaign,dialogue_age) WHERE NOT dialogue_expired AND payload->>'kind' IN ('group','address');
        CREATE OR REPLACE FUNCTION echoes_dialogue_age() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
          IF TG_TABLE_NAME='turns' THEN
            SELECT elapsed-greatest(0,day-NEW.day) INTO NEW.dialogue_age FROM dialogue_clock WHERE campaign=NEW.campaign;
          ELSE
            SELECT elapsed-greatest(0,day-(NEW.payload->>'day')::int) INTO NEW.dialogue_age FROM dialogue_clock WHERE campaign=NEW.campaign;
          END IF;
          RETURN NEW;
        END $$;
        CREATE OR REPLACE TRIGGER echoes_turn_age BEFORE INSERT ON turns FOR EACH ROW EXECUTE FUNCTION echoes_dialogue_age();
        CREATE OR REPLACE TRIGGER echoes_cue_age BEFORE INSERT ON cue_events FOR EACH ROW EXECUTE FUNCTION echoes_dialogue_age();
        CREATE TABLE IF NOT EXISTS dialogue_day_recaps(campaign text,actor text,entry text,day int,fingerprint text,body text NOT NULL,
          PRIMARY KEY(campaign,actor,entry,fingerprint));
        ALTER TABLE dialogue_day_recaps ADD COLUMN IF NOT EXISTS retry_at timestamptz;
        """;

    internal static Task<JsonNode?> ObserveDay(Database db,string campaign,int day,string session)=>db.One("""
        WITH advanced AS (INSERT INTO dialogue_clock(campaign,day,load_id) SELECT id,@day,@session FROM campaigns WHERE id=@c
        ON CONFLICT(campaign) DO UPDATE SET
          elapsed=dialogue_clock.elapsed+CASE WHEN dialogue_clock.load_id=excluded.load_id
            THEN greatest(0,excluded.day-dialogue_clock.day) ELSE 0 END,
          day=CASE WHEN dialogue_clock.load_id=excluded.load_id THEN greatest(dialogue_clock.day,excluded.day) ELSE excluded.day END,
          retired_loads=CASE WHEN dialogue_clock.load_id<>'' AND dialogue_clock.load_id<>excluded.load_id
            THEN array_append(dialogue_clock.retired_loads,dialogue_clock.load_id) ELSE dialogue_clock.retired_loads END,
          load_id=excluded.load_id
        WHERE NOT excluded.load_id=ANY(dialogue_clock.retired_loads) AND
          (dialogue_clock.load_id<>excluded.load_id OR excluded.day>dialogue_clock.day) RETURNING 1)
        SELECT CASE WHEN EXISTS(SELECT FROM advanced) THEN 'true'
          WHEN EXISTS(SELECT FROM dialogue_clock WHERE campaign=@c AND
            (@session=ANY(retired_loads) OR load_id=@session AND day>@day)) THEN NULL ELSE 'false' END
        """,("c",campaign),("day",day),("session",session));

    internal static async Task Maintain(Database db,CancellationToken ct)
    {
        await db.Run("""
            UPDATE personality_turning_points p SET evidence=echoes_marked_observation(e.id,p.reason)
              FROM daily_events e WHERE p.source_type='event' AND e.id=p.source_id AND p.evidence IS NULL AND e.description<>'';
            INSERT INTO dialogue_clock(campaign,day,load_id) SELECT c.id,c.day,'' FROM campaigns c JOIN slots s ON c.id=s.campaign
              WHERE c.day>=0 ON CONFLICT DO NOTHING;
            UPDATE turns t SET dialogue_age=d.elapsed-greatest(0,d.day-t.day) FROM dialogue_clock d
              WHERE d.campaign=t.campaign AND t.dialogue_age IS NULL;
            UPDATE cue_events e SET dialogue_age=d.elapsed-greatest(0,d.day-(e.payload->>'day')::int) FROM dialogue_clock d
              WHERE d.campaign=e.campaign AND e.dialogue_age IS NULL AND e.payload->>'kind' IN ('address','group');
            """);
        await using var conn=await db.Source.OpenConnectionAsync(ct);
        // Backfill before aging. Small transactions allow ongoing game writes and restart safely.
        for(int batch=0;batch<12;batch++){
            ct.ThrowIfCancellationRequested();
            await using var tx=await conn.BeginTransactionAsync(ct);
            var items=new List<(string Campaign,string Actor,Guid Id,int Day,Guid? Parent,JsonNode? Payload)>();
            await using(var pick=Database.Command(conn,"""
                SELECT campaign,actor,id,day FROM turns WHERE state='complete' AND NOT knowledge_reviewed AND NOT dialogue_expired
                ORDER BY day,started,id LIMIT 32 FOR UPDATE SKIP LOCKED
                """))
            await using(var reader=await pick.ExecuteReaderAsync(ct))
                while(await reader.ReadAsync(ct))items.Add((reader.GetString(0),reader.GetString(1),reader.GetGuid(2),reader.GetInt32(3),null,null));
            foreach(var item in items){
                await CharacterKnowledge.Private(conn,item.Campaign,item.Actor,item.Id,new());
                await using var done=Database.Command(conn,"UPDATE turns SET knowledge_reviewed=true WHERE id=@id",("id",item.Id));
                await done.ExecuteNonQueryAsync(ct);
            }
            int processed=items.Count;items.Clear();
            await using(var pick=Database.Command(conn,"""
                SELECT e.campaign,e.id,(payload->>'day')::int,parent,payload::text FROM cue_events e
                WHERE NOT knowledge_reviewed AND NOT dialogue_expired AND (payload->>'kind'='group' OR
                  payload->>'kind'='address' AND EXISTS(SELECT FROM daily_events d WHERE d.campaign=e.campaign AND d.cue_anchor=e.id))
                ORDER BY (payload->>'day')::int,e.id LIMIT 32 FOR UPDATE SKIP LOCKED
                """))
            await using(var reader=await pick.ExecuteReaderAsync(ct))
                while(await reader.ReadAsync(ct))items.Add((reader.GetString(0),"",reader.GetGuid(1),reader.GetInt32(2),
                    reader.IsDBNull(3)?null:reader.GetGuid(3),JsonNode.Parse(reader.GetString(4))));
            foreach(var item in items){
                if(item.Payload?["line"] is JsonObject line)
                    await CharacterKnowledge.Group(conn,item.Campaign,item.Id,item.Parent,item.Day,line);
                else{
                    await using var address=Database.Command(conn,"SELECT actor,description,observed FROM daily_events WHERE campaign=@c AND cue_anchor=@id AND kind='address'",("c",item.Campaign),("id",item.Id));
                    var spoken=new List<CharacterKnowledge.Line>();
                    await using(var reader=await address.ExecuteReaderAsync(ct))
                        while(await reader.ReadAsync(ct)){
                            string actor=reader.GetString(0);
                            spoken.Add(new(item.Id,item.Id,item.Day,reader.GetDateTime(2),actor,reader.GetString(1),new[]{actor},"private"));
                        }
                    foreach(var speech in spoken)await CharacterKnowledge.Record(conn,item.Campaign,speech,null,new());
                }
                await using var done=Database.Command(conn,"UPDATE cue_events SET knowledge_reviewed=true WHERE campaign=@c AND id=@id",("c",item.Campaign),("id",item.Id));
                await done.ExecuteNonQueryAsync(ct);
            }
            await tx.CommitAsync(ct);
            if(processed+items.Count==0)break;
        }
        await using var expire=await conn.BeginTransactionAsync(ct);
        await using var prune=Database.Command(conn,"""
            UPDATE personality_turning_points p SET evidence=echoes_marked_evidence(t.id,p.reason)
              FROM turns t WHERE p.source_type='turn' AND t.id=p.source_id AND p.evidence IS NULL AND t.knowledge_reviewed;
            CREATE TEMP TABLE old_turns ON COMMIT DROP AS
              SELECT t.id,t.campaign,t.actor,t.day,coalesce(t.cue_binding->>'conversation',t.id::text) AS session FROM turns t JOIN dialogue_clock d ON d.campaign=t.campaign
              WHERE NOT t.dialogue_expired AND t.dialogue_age<=d.elapsed-@days AND
                 (t.state IN ('failed','interrupted','ready') OR t.state='pending' AND t.started<now()-interval '3 minutes' OR t.state='complete' AND t.knowledge_reviewed);
            CREATE TEMP TABLE old_cues ON COMMIT DROP AS
              SELECT e.id,e.campaign,(e.payload->>'day')::int AS day,coalesce(e.payload->'line'->>'session',e.payload->'people'->0->'details'->>'conversation',e.id::text) AS session FROM cue_events e JOIN dialogue_clock d ON d.campaign=e.campaign
              WHERE NOT e.dialogue_expired AND e.dialogue_age<=d.elapsed-@days AND e.payload->>'kind' IN ('group','address')
                AND (e.knowledge_reviewed OR e.payload->>'kind'='address' AND NOT EXISTS(
                   SELECT FROM daily_events r WHERE r.campaign=e.campaign AND r.cue_anchor=e.id));
            DELETE FROM memory_summaries m WHERE kind IN ('conversation','address') AND (
                EXISTS(SELECT FROM old_turns t WHERE t.campaign=m.campaign AND m.sources ? t.id::text) OR
                EXISTS(SELECT FROM old_cues e WHERE e.campaign=m.campaign AND m.sources ? e.id::text));
            CREATE TEMP TABLE old_entries ON COMMIT DROP AS
              SELECT campaign,actor,day,session FROM old_turns
              UNION SELECT x.campaign,member,x.day,x.session FROM old_cues x JOIN cue_events e USING(campaign,id)
                CROSS JOIN LATERAL jsonb_array_elements_text(e.payload->'line'->'audience') member
              UNION SELECT x.campaign,d.actor,x.day,coalesce(e.payload->'people'->0->'details'->>'conversation',d.id::text)
                FROM old_cues x JOIN cue_events e USING(campaign,id) JOIN daily_events d ON d.campaign=x.campaign AND d.cue_anchor=x.id AND d.kind='address';
            -- A later turn can carry an earlier passing line in its native request. Retain the original request identity.
            UPDATE turns t SET request_digest=coalesce(t.request_digest,sha256(convert_to(jsonb_build_array(t.player_text,t.cue_binding)::text,'UTF8'))),
              cue_binding=t.cue_binding-'remark' FROM old_cues x WHERE t.campaign=x.campaign AND
              t.cue_binding->'remark'->>'kind'='address' AND t.cue_binding->>'event'=x.id::text;
            WITH retained AS (
              SELECT t.id,coalesce(jsonb_agg(r.line ORDER BY r.seq) FILTER(WHERE x.id IS NULL),'[]'::jsonb) AS lines
              FROM turns t CROSS JOIN LATERAL jsonb_array_elements(CASE WHEN jsonb_typeof(t.talk->'recentSpeech')='array'
                THEN t.talk->'recentSpeech' ELSE '[]'::jsonb END) WITH ORDINALITY r(line,seq)
              LEFT JOIN old_turns x ON x.campaign=t.campaign AND x.id::text=r.line->>'turn'
              WHERE t.talk ? 'recentSpeech' AND EXISTS(SELECT FROM old_turns active WHERE active.campaign=t.campaign)
              GROUP BY t.id HAVING bool_or(x.id IS NOT NULL))
            UPDATE turns t SET talk=jsonb_set(t.talk,'{recentSpeech}',r.lines) ||
              CASE WHEN jsonb_typeof(t.talk->'commitments')='array' THEN jsonb_build_object('commitments',(
                SELECT coalesce(jsonb_agg(CASE WHEN jsonb_typeof(m->'proposal')='string'
                    AND NOT (m->>'speaker'='npc' AND strpos(t.player_text,m->>'proposal')>0)
                    AND NOT EXISTS(SELECT FROM jsonb_array_elements(r.lines) l WHERE
                      strpos(l->>(CASE WHEN m->>'speaker'='npc' THEN 'player' ELSE 'npc' END),m->>'proposal')>0)
                  THEN m||'{"proposal":null}'::jsonb ELSE m END ORDER BY seq),'[]') FROM
                  jsonb_array_elements(t.talk->'commitments') WITH ORDINALITY marks(m,seq))) ELSE '{}'::jsonb END
              FROM retained r WHERE t.id=r.id;
            DELETE FROM resident_recaps r USING old_entries x WHERE r.kind<>'daily' AND r.campaign=x.campaign AND r.actor=x.actor AND
              r.entry IN (x.session,'day-'||x.day,'day-'||x.day||'-'||x.session);
            DELETE FROM dialogue_day_recaps r USING old_entries x WHERE r.campaign=x.campaign AND r.actor=x.actor AND r.day=x.day
              AND r.entry IN ('day-'||x.day,'day-'||x.day||'-'||x.session);
            UPDATE turns t SET dialogue_expired=true,state=CASE WHEN t.state IN ('ready','pending') THEN 'interrupted' ELSE t.state END,
              request_digest=coalesce(request_digest,sha256(convert_to(jsonb_build_array(t.player_text,t.cue_binding)::text,'UTF8'))),
              player_text='',reply=NULL,cue_binding=t.cue_binding-'remark',
              talk=CASE WHEN t.talk IS NULL THEN NULL ELSE jsonb_strip_nulls(jsonb_build_object('topic',t.talk->'topic','flow',t.talk->'flow','handoff',CASE WHEN jsonb_typeof(t.talk->'handoff')='object' THEN (t.talk->'handoff')-'cue' END)) END,
              quest_offer=t.quest_offer-'line'-'evidence'-'learned'
              FROM old_turns x WHERE t.id=x.id;
            UPDATE daily_events d SET description='' FROM old_cues x WHERE d.campaign=x.campaign AND d.cue_anchor=x.id AND d.kind='address';
            DELETE FROM cue_unindexed_people p USING old_cues x WHERE p.campaign=x.campaign AND p.event=x.id AND p.kind='address';
            UPDATE cue_events e SET packet_digest=coalesce(packet_digest,sha256(convert_to(e.payload::text,'UTF8'))),
              payload=jsonb_set(e.payload,'{line}',(e.payload->'line')-'learned'-'priorPromise'-'proposal')
              WHERE e.knowledge_reviewed AND e.payload->>'kind'='group' AND (e.payload->'line' ?| ARRAY['learned','priorPromise','proposal'])
                AND EXISTS(SELECT FROM old_cues x WHERE x.campaign=e.campaign AND x.session=e.payload->'line'->>'session');
            UPDATE cue_events e SET dialogue_expired=true,packet_digest=coalesce(packet_digest,sha256(convert_to(e.payload::text,'UTF8'))),
              payload=CASE WHEN e.payload->>'kind'='group' THEN jsonb_set(e.payload,'{line}',
                (e.payload->'line')-'words'-'learned'-'promise'-'proposal'-'priorPromise'-'priorProposal')
                ELSE jsonb_set(e.payload,'{people}','[]'::jsonb) END FROM old_cues x WHERE (x.campaign,x.id)=(e.campaign,e.id);
            """,("days",Days));
        await prune.ExecuteNonQueryAsync(ct);
        await expire.CommitAsync(ct);
    }
}
