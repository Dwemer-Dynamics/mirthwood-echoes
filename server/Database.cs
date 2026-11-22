using System.Text.Json;
using System.Text.Json.Nodes;
using Npgsql;

public sealed record RuntimePaths(string State, string Game, string Saves);
public sealed class Database(NpgsqlDataSource source)
{
    public NpgsqlDataSource Source => source;
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public async Task Initialize()
    {
        await using var cmd = source.CreateCommand("""
            CREATE TABLE IF NOT EXISTS configuration(key text PRIMARY KEY,value jsonb NOT NULL);
            CREATE TABLE IF NOT EXISTS campaigns(id text PRIMARY KEY, slot int NOT NULL, created text NOT NULL, player text NOT NULL, day int, player_name text NOT NULL);
            CREATE TABLE IF NOT EXISTS slots(slot int PRIMARY KEY, campaign text NOT NULL REFERENCES campaigns);
            CREATE TABLE IF NOT EXISTS actors(campaign text REFERENCES campaigns, key text, owner text NOT NULL, native_id text NOT NULL, name text NOT NULL, kind text NOT NULL,
                job text NOT NULL, town text NOT NULL, present boolean NOT NULL, raw jsonb NOT NULL, PRIMARY KEY(campaign,key));
            CREATE INDEX IF NOT EXISTS actors_native ON actors(campaign,native_id);
            CREATE TABLE IF NOT EXISTS daily_events(id uuid PRIMARY KEY, campaign text, actor text, day int NOT NULL, kind text NOT NULL, description text NOT NULL,
                observed timestamptz NOT NULL DEFAULT now(), FOREIGN KEY(campaign,actor) REFERENCES actors(campaign,key));
            CREATE INDEX IF NOT EXISTS daily_actor ON daily_events(campaign,actor,day DESC);
            CREATE TABLE IF NOT EXISTS turns(id uuid PRIMARY KEY, campaign text, actor text, day int NOT NULL, player_text text NOT NULL, reply text,
                state text NOT NULL, started timestamptz NOT NULL DEFAULT now(), first_ms int, total_ms int,
                FOREIGN KEY(campaign,actor) REFERENCES actors(campaign,key));
            CREATE INDEX IF NOT EXISTS turns_actor ON turns(campaign,actor,day DESC);
            CREATE TABLE IF NOT EXISTS commitments(id uuid PRIMARY KEY, campaign text, actor text, day int NOT NULL, due int, speaker text NOT NULL,
                statement text NOT NULL, status text NOT NULL DEFAULT 'open', recorded timestamptz NOT NULL DEFAULT now(), FOREIGN KEY(campaign,actor) REFERENCES actors(campaign,key));
            CREATE INDEX IF NOT EXISTS commitments_actor ON commitments(campaign,actor,status);
            ALTER TABLE commitments ADD COLUMN IF NOT EXISTS resolved_day int;
            ALTER TABLE commitments ADD COLUMN IF NOT EXISTS resolution_source text;
            ALTER TABLE turns ADD COLUMN IF NOT EXISTS commitment_review text;
            ALTER TABLE turns ADD COLUMN IF NOT EXISTS talk jsonb;
            ALTER TABLE turns ADD COLUMN IF NOT EXISTS quest_offer jsonb;
            CREATE UNIQUE INDEX IF NOT EXISTS quest_offer_next ON turns ((quest_offer->>'parent'))
                WHERE quest_offer ? 'parent' AND state IN ('pending','ready','complete');
            ALTER TABLE turns ADD COLUMN IF NOT EXISTS reaction text;
            ALTER TABLE commitments ADD COLUMN IF NOT EXISTS turn_id uuid REFERENCES turns(id);
            ALTER TABLE commitments ADD COLUMN IF NOT EXISTS proposal text;
            ALTER TABLE commitments ADD COLUMN IF NOT EXISTS proposal_turn_id uuid REFERENCES turns(id);
            ALTER TABLE commitments ADD COLUMN IF NOT EXISTS provider text;
            ALTER TABLE commitments ADD COLUMN IF NOT EXISTS model text;
            CREATE INDEX IF NOT EXISTS commitments_turn ON commitments(turn_id) WHERE turn_id IS NOT NULL;
            ALTER TABLE daily_events ADD COLUMN IF NOT EXISTS action_name text;
            ALTER TABLE daily_events ADD COLUMN IF NOT EXISTS growth_axis smallint;
            ALTER TABLE daily_events ADD COLUMN IF NOT EXISTS growth_direction smallint;
            ALTER TABLE campaigns ADD COLUMN IF NOT EXISTS year_length int;
            ALTER TABLE campaigns ADD COLUMN IF NOT EXISTS save_year int;
            ALTER TABLE campaigns ADD COLUMN IF NOT EXISTS save_day_of_year int;
            CREATE TABLE IF NOT EXISTS actor_backstories(
                campaign text, actor text, body text NOT NULL, grounding jsonb NOT NULL, provider text NOT NULL, model text NOT NULL,
                authored timestamptz NOT NULL DEFAULT now(), PRIMARY KEY(campaign,actor), FOREIGN KEY(campaign,actor) REFERENCES actors(campaign,key));
            CREATE TABLE IF NOT EXISTS memory_summaries(
                campaign text, actor text, day int, kind text, body text NOT NULL, entries int NOT NULL, sources jsonb NOT NULL,
                recorded timestamptz NOT NULL, part int NOT NULL DEFAULT 0, PRIMARY KEY(campaign,actor,day,kind,part),
                FOREIGN KEY(campaign,actor) REFERENCES actors(campaign,key));
            ALTER TABLE memory_summaries ADD COLUMN IF NOT EXISTS part int NOT NULL DEFAULT 0;
            DO $$ BEGIN
              IF EXISTS (SELECT FROM pg_constraint WHERE conrelid='memory_summaries'::regclass AND contype='p' AND cardinality(conkey)=4) THEN
                ALTER TABLE memory_summaries DROP CONSTRAINT memory_summaries_pkey, ADD PRIMARY KEY(campaign,actor,day,kind,part);
              END IF;
            END $$;
            CREATE TABLE IF NOT EXISTS personality_turning_points(
                campaign text, actor text, source_type text, source_id uuid, day int NOT NULL, axis smallint NOT NULL CHECK(axis BETWEEN 0 AND 4),
                direction smallint NOT NULL CHECK(direction IN (-1,1)), reason text NOT NULL, recorded timestamptz NOT NULL DEFAULT now(),
                PRIMARY KEY(campaign,actor,source_type,source_id), FOREIGN KEY(campaign,actor) REFERENCES actors(campaign,key));
            UPDATE turns SET state='interrupted' WHERE state='pending';
            """);
        cmd.CommandText += CueHistory.Schema + CharacterKnowledge.Schema + DialogueRetention.Schema + DialogueRecaps.Schema;
        cmd.CommandText += """
            CREATE TABLE IF NOT EXISTS resident_recaps(
                campaign text,actor text,kind text,entry text,fingerprint text,body text NOT NULL,
                PRIMARY KEY(campaign,actor,kind,entry,fingerprint),FOREIGN KEY(campaign,actor) REFERENCES actors(campaign,key));
            """;
        await cmd.ExecuteNonQueryAsync();
    }

    public static NpgsqlCommand Command(NpgsqlConnection connection, string sql, params (string Key, object? Value)[] args)
    {
        var cmd = new NpgsqlCommand(sql, connection);
        foreach (var arg in args) cmd.Parameters.AddWithValue(arg.Key, arg.Value ?? DBNull.Value);
        return cmd;
    }
    public async Task<int> Run(string sql, params (string Key, object? Value)[] args)
    {
        await using var conn = await source.OpenConnectionAsync();
        await using var cmd = Command(conn, sql, args);
        return await cmd.ExecuteNonQueryAsync();
    }
    public async Task<JsonNode?> One(string sql, params (string Key, object? Value)[] args)
    {
        await using var conn = await source.OpenConnectionAsync();
        await using var cmd = Command(conn, sql, args);
        var value = await cmd.ExecuteScalarAsync();
        return value is null or DBNull ? null : JsonNode.Parse((string)value);
    }
    public Task<JsonNode?> ScopedOne(string sql,bool explicitHead,Guid? head,params (string Key,object? Value)[] args) =>
        One(CueHistory.Query(sql),args.Concat(new (string,object?)[]{("explicit",explicitHead),("head",head)}).ToArray());
    public Task<JsonNode?> Config(string key) => One("SELECT value::text FROM configuration WHERE key=@key", ("key",key));
    public Task SetConfig(string key, object value) => Run("""
        INSERT INTO configuration VALUES (@key,@value::jsonb) ON CONFLICT (key) DO UPDATE SET value=EXCLUDED.value
        """, ("key",key),("value",JsonSerializer.Serialize(value,Json)));
}

public sealed class UserError(string message, int status = 400) : Exception(message)
{
    public int Status => status;
}
public static class Fields
{
    public static string S(this JsonNode? n, string key) => (n as JsonObject)?[key]?.ToString() ?? "";
    public static string Clip(string s,int n) => s.Length <= n ? s : s[..n];
    public static string Required(this JsonNode? n,string key,int max=200)
    {
        var value=n.S(key).Trim();
        if (value.Length==0 || value.Length>max) throw new UserError("Invalid " + key + ".");
        return value;
    }
    public static int I(this JsonNode? n, string key, int fallback=-1) => int.TryParse(n.S(key), out var number) ? number : fallback;
    public static Guid Id(this JsonNode? n) => Guid.TryParse(n.S("id"),out var id) && id!=Guid.Empty ? id : throw new UserError("A request ID is required.");
}
