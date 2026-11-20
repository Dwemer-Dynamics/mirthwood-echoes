using System.Text.Json.Nodes;
using Npgsql;

// A save points to one immutable ancestry. Two copies of a save may legitimately add different children.
public static class CueHistory
{
    public const string SaveKey = "mirthwood.echoes.history";
    public const string Schema = """
        CREATE TABLE IF NOT EXISTS cue_events (
            campaign text REFERENCES campaigns, id uuid, parent uuid, payload jsonb NOT NULL,
            PRIMARY KEY(campaign,id), FOREIGN KEY(campaign,parent) REFERENCES cue_events(campaign,id));
        ALTER TABLE campaigns ADD COLUMN IF NOT EXISTS cue_saved uuid;
        ALTER TABLE campaigns ADD COLUMN IF NOT EXISTS cue_live uuid;
        ALTER TABLE campaigns ADD COLUMN IF NOT EXISTS cue_seen timestamptz;
        ALTER TABLE daily_events ADD COLUMN IF NOT EXISTS cue_anchor uuid;
        ALTER TABLE turns ADD COLUMN IF NOT EXISTS cue_binding jsonb;
        ALTER TABLE turns ADD COLUMN IF NOT EXISTS cue_anchor uuid;
        CREATE TABLE IF NOT EXISTS cue_fulfillments (
            commitment uuid REFERENCES commitments(id), campaign text, anchor uuid, day int NOT NULL,
            PRIMARY KEY(commitment,anchor), FOREIGN KEY(campaign,anchor) REFERENCES cue_events(campaign,id));
        CREATE TABLE IF NOT EXISTS cue_unindexed_people (
            campaign text, event uuid, owner text, native_id text, day int NOT NULL, kind text NOT NULL,
            description text NOT NULL, action text NOT NULL, axis smallint, direction smallint,
            PRIMARY KEY(campaign,event,owner,native_id), FOREIGN KEY(campaign,event) REFERENCES cue_events(campaign,id));
        """;
    // Keep the identity pending until a save scan can match both native fields to exactly one resident.
    public const string ProjectResidents = """
        WITH matches AS (
            SELECT p.*,a.key FROM cue_unindexed_people p CROSS JOIN LATERAL
                (SELECT min(key) AS key FROM actors WHERE campaign=p.campaign AND owner=p.owner AND native_id=p.native_id HAVING count(*)=1) a
            WHERE p.campaign=@campaign
        ), projected AS (
            INSERT INTO daily_events(id,campaign,actor,day,kind,description,action_name,growth_axis,growth_direction,cue_anchor)
            SELECT md5(event::text||'/'||key)::uuid,campaign,key,day,kind,description,action,axis,direction,event FROM matches
            ON CONFLICT(id) DO NOTHING
        )
        DELETE FROM cue_unindexed_people p USING matches m WHERE (p.campaign,p.event,p.owner,p.native_id)=(m.campaign,m.event,m.owner,m.native_id);
        """;
    // Scoped CTEs leave legacy journals readable. A compacted page is visible only when every contributing source is visible.
    public const string Scope = """
        WITH RECURSIVE ancestry(id,parent) AS (
            SELECT e.id,e.parent FROM cue_events e JOIN campaigns c ON c.id=e.campaign
            WHERE e.campaign=@c AND e.id=CASE WHEN @explicit THEN @head::uuid
                WHEN c.cue_seen>now()-interval '35 seconds' THEN c.cue_live ELSE c.cue_saved END
            UNION ALL SELECT e.id,e.parent FROM cue_events e JOIN ancestry t ON e.id=t.parent WHERE e.campaign=@c
        ), turns AS (
            SELECT * FROM public.turns WHERE campaign=@c AND (cue_anchor IS NULL OR cue_anchor IN (SELECT id FROM ancestry))
        ), daily_events AS (
            SELECT * FROM public.daily_events WHERE campaign=@c AND (cue_anchor IS NULL OR cue_anchor IN (SELECT id FROM ancestry))
        ), commitments AS (
            SELECT p.id,p.campaign,p.actor,p.day,p.due,p.speaker,p.statement,
                CASE WHEN f.day IS NULL THEN p.status ELSE 'fulfilled' END AS status,p.recorded,coalesce(f.day,p.resolved_day) AS resolved_day,
                CASE WHEN f.day IS NULL THEN p.resolution_source ELSE 'player_report' END AS resolution_source,
                p.turn_id,p.proposal,p.proposal_turn_id,p.provider,p.model
            FROM public.commitments p LEFT JOIN LATERAL
                (SELECT min(r.day) AS day FROM cue_fulfillments r JOIN ancestry h ON h.id=r.anchor WHERE r.commitment=p.id AND r.campaign=@c) f ON true
            WHERE p.campaign=@c AND (p.turn_id IS NULL OR EXISTS(SELECT FROM turns t WHERE t.id=p.turn_id))
        ), personality_turning_points AS (
            SELECT p.* FROM public.personality_turning_points p WHERE campaign=@c AND
                (EXISTS(SELECT FROM turns WHERE id=p.source_id) OR EXISTS(SELECT FROM daily_events WHERE id=p.source_id))
        ), cue_notes AS (
            SELECT campaign,actor,day,kind,id,observed AS at,left(description,190) AS note FROM daily_events WHERE actor=@a AND cue_anchor IS NOT NULL
            UNION ALL SELECT campaign,actor,day,'conversation',id,started,'Player: “'||left(player_text,105)||'” · Reply: “'||left(reply,125)||'”'
            FROM turns WHERE actor=@a AND state='complete' AND cue_anchor IS NOT NULL
        ), cue_parts AS (
            SELECT *,(-1-(row_number() OVER(PARTITION BY day,kind ORDER BY at,id)-1)/3)::int AS part FROM cue_notes
        ), memory_summaries AS (
            SELECT m.* FROM public.memory_summaries m WHERE campaign=@c AND NOT EXISTS(
                SELECT FROM jsonb_array_elements_text(m.sources) s WHERE
                NOT EXISTS(SELECT FROM turns WHERE id=s.value::uuid AND cue_anchor IS NULL) AND NOT EXISTS(SELECT FROM daily_events WHERE id=s.value::uuid AND cue_anchor IS NULL))
            UNION ALL SELECT campaign,actor,day,kind,string_agg(note,E'\n' ORDER BY at,id),count(*)::int,jsonb_agg(id ORDER BY at,id),max(at),part
            FROM cue_parts GROUP BY campaign,actor,day,kind,part
        )
        """;
    public static string Query(string sql) => sql.TrimStart().StartsWith("WITH ",StringComparison.Ordinal)
        ? Scope + ", " + sql.TrimStart()[5..] : Scope + " " + sql;

    public static Guid? OptionalId(JsonNode? n)
    {
        if(n is null)return null;
        if(Guid.TryParseExact(n.ToString(),"D",out var id)&&id!=Guid.Empty)return id;
        throw new UserError("Invalid native history identifier.");
    }
}

public sealed partial class Conversation
{
    static readonly HashSet<string> CueKinds = new(StringComparer.Ordinal) {
        "gift","hire","collection","social","work","household","funeral","letter","quest","warning","talk"
    };
    public async Task ObserveCue(JsonNode n)
    {
        var world=n["world"];string c=World(world);var id=n.Id();var parent=CueHistory.OptionalId(n["parent"]);
        var kind=n.Required("kind",24);
        if(!CueKinds.Contains(kind)||parent==id||n["people"] is not JsonArray people||people.Count>2 ||
            (kind=="talk"?people.Count!=0:people.Count==0))throw new UserError("Invalid native event.");
        var canonical=new JsonObject{["day"]=world.I("day"),["kind"]=kind,["people"]=people.DeepClone()};
        foreach(var p in people){p.Required("owner",200);p.Required("nativeId",200);p.Required("description",500);}
        await using var conn=await db.Source.OpenConnectionAsync();
        await using var tx=await conn.BeginTransactionAsync();
        await using(var gate=Database.Command(conn,"SELECT pg_advisory_xact_lock(hashtextextended(@c,17))",("c",c)))await gate.ExecuteNonQueryAsync();
        if(parent.HasValue&&n["parentNotQueued"] is JsonValue flag&&flag.TryGetValue<bool>(out var missing)&&missing){
            // A copied game can bring a marker from a different Echoes database. Its missing past grants no knowledge.
            await using var root=Database.Command(conn,"INSERT INTO cue_events SELECT @c,@p,NULL,'{\"unresolved\":true}'::jsonb FROM campaigns WHERE id=@c ON CONFLICT DO NOTHING",("c",c),("p",parent));
            await root.ExecuteNonQueryAsync();
        }
        await using(var check=Database.Command(conn,"SELECT EXISTS(SELECT FROM campaigns WHERE id=@c) AND (@p::uuid IS NULL OR EXISTS(SELECT FROM cue_events WHERE campaign=@c AND id=@p))",("c",c),("p",parent)))
            if(!Equals(await check.ExecuteScalarAsync(),true))throw new UserError("Waiting for the saved campaign or preceding event.",425);
        await using(var insert=Database.Command(conn,"""
            INSERT INTO cue_events VALUES(@c,@id,@p::uuid,@j::jsonb) ON CONFLICT(campaign,id) DO NOTHING
            """,("c",c),("id",id),("p",parent),("j",canonical.ToJsonString())))await insert.ExecuteNonQueryAsync();
        await using(var fill=Database.Command(conn,"""
            WITH RECURSIVE path(id,parent) AS (SELECT id,parent FROM cue_events WHERE campaign=@c AND id=@p::uuid
                UNION ALL SELECT e.id,e.parent FROM cue_events e JOIN path h ON h.parent=e.id WHERE e.campaign=@c)
            UPDATE cue_events SET parent=@p::uuid,payload=@j::jsonb WHERE campaign=@c AND id=@id AND payload ? 'unresolved'
                AND NOT EXISTS(SELECT FROM path WHERE id=@id)
            """,("c",c),("id",id),("p",parent),("j",canonical.ToJsonString())))await fill.ExecuteNonQueryAsync();
        await using(var match=Database.Command(conn,"SELECT EXISTS(SELECT FROM cue_events WHERE campaign=@c AND id=@id AND parent IS NOT DISTINCT FROM @p::uuid AND payload=@j::jsonb)",
            ("c",c),("id",id),("p",parent),("j",canonical.ToJsonString())))
            if(!Equals(await match.ExecuteScalarAsync(),true))throw new UserError("The native event ID already has different evidence.",409);
        foreach(var p in people){
            var action=kind=="social"?Fields.Clip(p.S("action"),80):"";
            var growth=native.Action(action);
            await using var project=Database.Command(conn,"""
                INSERT INTO cue_unindexed_people(campaign,event,owner,native_id,day,kind,description,action,axis,direction)
                VALUES(@c,@id,@owner,@native,@day,@kind,@desc,@action,@axis::smallint,@dir::smallint)
                ON CONFLICT DO NOTHING
                """,("id",id),("c",c),("day",world.I("day")),("kind",kind),("desc",p.S("description")),("action",action),
                ("axis",growth?.Axis),("dir",growth?.Direction),("owner",p.S("owner")),("native",p.S("nativeId")));
            await project.ExecuteNonQueryAsync();
        }
        await using(var project=Database.Command(conn,CueHistory.ProjectResidents,("campaign",c)))await project.ExecuteNonQueryAsync();
        await tx.CommitAsync();
    }

    static JsonObject? CueBinding(JsonNode n)
    {
        if(n["context"] is null)return null;
        if(n["context"] is not JsonObject b)throw new UserError("Invalid Talk context.");
        var session=CueHistory.OptionalId(b["session"])??throw new UserError("A play session is required.");
        var result=new JsonObject{["session"]=session.ToString(),["head"]=CueHistory.OptionalId(b["head"])?.ToString()};
        if(b["cards"] is JsonArray cards){
            if(cards.Count>3||cards.Any(x=>x?.ToString() is not ("Rumors" or "Complain" or "Cheer Up")))throw new UserError("Invalid native topic cards.");
            result["cards"]=cards.DeepClone();
        }
        if(b["event"]!=null)result["event"]=CueHistory.OptionalId(b["event"])?.ToString();
        if(b["remark"] is {} remark){
            if(remark.Required("kind",20) is not ("remark" or "greeting" or "warning" or "gift" or "hire" or "card" or "giftReply" or "collectionReply"))throw new UserError("Unknown native remark.");
            result["remark"]=new JsonObject{["kind"]=remark.S("kind"),["text"]=remark.Required("text",460),
                ["owner"]=remark.Required("owner"),["nativeId"]=remark.Required("nativeId")};
        }
        return result;
    }
    async Task<JsonObject?> CurrentCue(string c,string a,JsonObject? binding)
    {
        if(binding==null)return null;
        var cue=new JsonObject();
        if(binding["event"]!=null){
            var fact=await db.ScopedOne("""
                SELECT jsonb_strip_nulls(jsonb_build_object('kind',d.kind,'observation',d.description,'details',p->'details'))::text
                FROM daily_events d JOIN actors a ON (a.campaign,a.key)=(d.campaign,d.actor)
                JOIN cue_events e ON (e.campaign,e.id)=(d.campaign,d.cue_anchor) CROSS JOIN LATERAL jsonb_array_elements(e.payload->'people') p
                WHERE d.campaign=@c AND d.actor=@a AND d.cue_anchor=@id AND p->>'owner'=a.owner AND p->>'nativeId'=a.native_id
                """,
                true,CueHistory.OptionalId(binding["head"]),("c",c),("a",a),("id",CueHistory.OptionalId(binding["event"])));
            if(fact!=null)cue["event"]=fact;
            else{
                var found=await db.One("SELECT to_jsonb(true)::text FROM cue_events WHERE campaign=@c AND id=@id",("c",c),("id",CueHistory.OptionalId(binding["event"])));
                if(found==null)throw new UserError("Waiting for this native observation to reach the journal.",425);
                throw new UserError("This event does not belong to the resident in this save.",409);
            }
        }
        if(binding["remark"] is {} r){
            var owner=await db.One("SELECT to_jsonb(true)::text FROM actors WHERE campaign=@c AND key=@a AND owner=@owner AND native_id=@id",
                ("c",c),("a",a),("owner",r.S("owner")),("id",r.S("nativeId")));
            if(owner==null)throw new UserError("This remark belongs to a different resident.",409);
            string kind=r.S("kind");cue["kind"]=kind;
            if(kind is "giftReply" or "collectionReply"){
                if(cue["event"]?["kind"]?.ToString()!=(kind=="giftReply"?"gift":"collection"))throw new UserError("The spoken line needs its matching transaction.",409);
                cue["spokenOpener"]=r.S("text");
            }else cue[kind is "greeting" or "card"?"observedAction":kind=="hire"?"priceQuote":"nativeCue"]=r.S("text");
        }
        return cue.Count>0?cue:null;
    }
}
