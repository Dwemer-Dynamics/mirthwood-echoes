using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Npgsql;

// Each part belongs to one delivered speaker and audience. Combining parts only after
// checking save ancestry also gives a correct recap when a save forks halfway through a visit.
internal static class DialogueRecaps
{
    internal const string Schema = """
        CREATE TABLE IF NOT EXISTS dialogue_recap_parts(
          campaign text NOT NULL REFERENCES campaigns, source uuid NOT NULL, pos int NOT NULL, anchor uuid,
          day int NOT NULL, age bigint NOT NULL, session text NOT NULL, mode text NOT NULL,
          said_at timestamptz NOT NULL, seq int NOT NULL, speaker text NOT NULL, audience text[] NOT NULL,
          detail text NOT NULL, keywords text NOT NULL, concepts text[] NOT NULL, aliases text NOT NULL DEFAULT '',
          question boolean NOT NULL, highlighted boolean NOT NULL DEFAULT false,
          search tsvector GENERATED ALWAYS AS (to_tsvector('english',keywords||' '||aliases)) STORED,
          PRIMARY KEY(campaign,source,pos));
        CREATE INDEX IF NOT EXISTS recap_age ON dialogue_recap_parts(campaign,age);
        CREATE INDEX IF NOT EXISTS recap_search ON dialogue_recap_parts USING gin(search);
        CREATE INDEX IF NOT EXISTS recap_concepts ON dialogue_recap_parts USING gin(concepts);
        CREATE INDEX IF NOT EXISTS recap_audience ON dialogue_recap_parts USING gin(audience);
        """;

    // The memory model can add search vocabulary for any topic. These offline concept
    // families also work if it is unavailable on the night the player goes to sleep.
    static readonly string[] Families = {
        "fishing angling angler fishing fish fished fishes rod bait trout perch pike salmon carp casting reel tackle",
        "repairs repair repairing repaired mend mending broken fixing fix rebuilt rebuilding hammer nails planks",
        "exploration explore exploring expedition ruins ruin dungeon delve ancient relic scouting",
        "hunting hunt stalking venison deer boar snare quarry tracking gamekeeper",
        "baking bake baked baker baking dough pastry tart loaf bread oven pie crust",
        "farming farm farming crop crops sow sowing planted planting harvest harvesting barley wheat tilling allotment",
        "gardening garden gardens gardening flowers blossom seeds pruning herb herbs",
        "beekeeping beekeeping beekeeper bees beehive hives apiary honey honeycomb",
        "foraging forage foraging mushroom mushrooms berry berries gathering nettles",
        "smithing blacksmith smith forge forging anvil smithing smelting ironwork",
        "weaving weave weaving spinning textile yarn cloth loom tailoring seamstress sewing stitch needle",
        "music music musical musician sing singing song ballad lute fiddle melody tune",
        "games chess checkmate boardgame boardgames draughts checkers gambit pawn pawns",
        "dancing dance dancing danced jig waltz reel",
        "livestock livestock cattle cow cows sheep flock lamb shepherd pasture grazing",
        "cooking cook cooking stew broth pottage soup pot supper meal recipe",
        "trading trade trading barter bargaining merchant stall price prices market wares",
        "weather weather rain rainfall raining storm downpour drizzle thunder showers",
        "mourning grief grieving mourning funeral bereavement bereaved mourn loss burial",
        "travel travel journey route road trail path travelling traveling directions",
        "medicine healing illness sick medicine remedy salve poultice healer bandage"
    };
    static readonly Dictionary<string,string[]> ConceptsByWord=Families.SelectMany(family=>{
        var words=family.Split(' ');return words.Distinct().Select(word=>(word,concept:words[0]));
    }).GroupBy(pair=>pair.word).ToDictionary(g=>g.Key,g=>g.Select(pair=>pair.concept).ToArray());
    static readonly HashSet<string> Stop=new(("""
        a an the and or but of as at in on my his her our its their this these that those is was were am are be been being
        i i'm im me we us you your you're it it's they them she he to from with for by so do does did would could should
        have has had i'll i've we've i'd if then there here what when where who which why how often still just also
        like some something anything someone about tell told remember recall mention mentioned much much more most
        any can may might shall really think know well yes good welcome back again please thanks thank thankyou
        recommend quiet stretch time day yesterday today once said say asking ask asked wanting wanted reckon
        """).Split((char[]?)null,StringSplitOptions.RemoveEmptyEntries));
    static readonly Regex Words=new(@"[\p{L}\p{N}]+(?:['’][\p{L}]+)?",RegexOptions.Compiled);
    static string[] Terms(string value)=>Words.Matches(value.ToLowerInvariant()).Select(m=>m.Value.Replace('’','\''))
        .Where(w=>w.Length>1&&!Stop.Contains(w)).Distinct().Take(48).ToArray();
    static string[] Concepts(string[] terms)=>terms.SelectMany(word=>ConceptsByWord.GetValueOrDefault(word)??Array.Empty<string>()).Distinct().ToArray();

    // Prefer a short indirect statement for the common first-person forms. A topic-only
    // fallback preserves scope when changing pronouns or trimming a qualifier would change the claim.
    static string Fallback(string words,string topics)
    {
        string line=words.Trim();
        if(line.Length<=155 && !line.Contains('*') && Regex.IsMatch(line,@"^[^.!?]+[.!]?$")){
            var subject=Regex.Match(line,@"^(I|We) (?!.*\b(I|me|my|we|our|us|you|your)\b)(.+)$",RegexOptions.IgnoreCase);
            if(subject.Success){
                var content=subject.Groups[3].Value.TrimEnd('.','!');
                if(content.Length>=4)return (subject.Groups[1].Value.Equals("we",StringComparison.OrdinalIgnoreCase)
                    ?"Said their group ":"Said they ")+Regex.Replace(content,@"^(am|was)\b",m=>m.Value.ToLowerInvariant()=="am"?"are":"were",RegexOptions.IgnoreCase)+".";
            }
        }
        return topics;
    }

    static string? GroundedSummary(string summary,string quote)
    {
        if(summary.Length is <8 or >150||quote.Length<4||quote.Equals(summary,StringComparison.OrdinalIgnoreCase))return null;
        var frame=Regex.Match(summary,@"^(Asked|Wondered|Reported|Mentioned|Described|Recalled|Suggested|Discussed|Said)\b",RegexOptions.IgnoreCase);
        if(!frame.Success)return null;
        if(quote.TrimEnd().EndsWith('?')!=Regex.IsMatch(frame.Value,@"^(asked|wondered)$",RegexOptions.IgnoreCase))return null;
        var evidence=Words.Matches(quote.ToLowerInvariant()).Select(m=>m.Value.Replace('’','\'')).ToHashSet();
        var content=Terms(summary[frame.Length..]);
        if(!content.Any(evidence.Contains)||content.Any(w=>!evidence.Contains(w)&&w is not ("group" or "whether")))return null;
        var qualifiers=new[]{"not","no","never","didn't","don't","wasn't","might","maybe","if","heard","perhaps","allegedly"};
        if(qualifiers.Any(q=>evidence.Contains(q)!=Words.Matches(summary.ToLowerInvariant().Replace('’','\'')).Any(m=>m.Value==q)))return null;
        return summary;
    }

    internal sealed record Source(string Campaign,Guid Id,int Position,Guid? Anchor,int Day,long Age,string Session,
        string Mode,DateTime At,int Sequence,string Speaker,string[] Audience,string Words);
    internal static async Task<List<Source>> Capture(NpgsqlConnection conn,CancellationToken ct)
    {
        // These exact words exist only in memory for the background model after the transaction commits.
        var samples=new List<Source>();
        await using var read=Database.Command(conn,"""
            SELECT t.campaign,t.id,p.pos,t.cue_anchor,t.day,t.dialogue_age,x.session,'private',t.started,0,p.speaker,
                ARRAY['player',t.actor],p.words FROM old_turns x JOIN turns t ON t.id=x.id
              CROSS JOIN LATERAL (VALUES(0,t.actor,t.quest_offer->'offer'->>'terms'),(1,'player',t.player_text),(2,t.actor,t.reply)) p(pos,speaker,words)
              WHERE x.keep AND t.state='complete' AND nullif(trim(p.words),'') IS NOT NULL
            UNION ALL SELECT e.campaign,e.id,0,e.id,x.day,e.dialogue_age,x.session,'group',(l->>'at')::timestamptz,
                (l->>'seq')::int,l->>'speaker',ARRAY['player']||ARRAY(SELECT jsonb_array_elements_text(l->'audience')),l->>'words'
              FROM old_cues x JOIN cue_events e USING(campaign,id) CROSS JOIN LATERAL (SELECT e.payload->'line' AS l) s
              WHERE x.keep AND e.payload->>'kind'='group' AND length(l->>'words')>0
            UNION ALL SELECT d.campaign,d.id,0,d.cue_anchor,d.day,x.age,x.session,'private',d.observed,0,d.actor,
                ARRAY['player',d.actor],d.description FROM old_addresses x JOIN daily_events d ON x.id=d.id
              WHERE x.keep AND d.description<>''
            ORDER BY 1,5,9,10,2,3
            """);
        var rows=new List<Source>();
        await using(var reader=await read.ExecuteReaderAsync(ct)){
            while(await reader.ReadAsync(ct))rows.Add(new(reader.GetString(0),reader.GetGuid(1),reader.GetInt32(2),
                reader.IsDBNull(3)?null:reader.GetGuid(3),reader.GetInt32(4),reader.GetInt64(5),reader.GetString(6),
                reader.GetString(7),reader.GetDateTime(8),reader.GetInt32(9),reader.GetString(10),reader.GetFieldValue<string[]>(11),reader.GetString(12)));
        }
        foreach(var row in rows){
            var terms=Terms(row.Words);
            // A topic list records what was mentioned without turning a question, denial or report into a fact.
            // Use brief separate phrases, never a replayable copy of the original line.
            var phrases=new List<string>();
            var phrase=new List<string>();
            void Flush(){if(phrase.Count>0){phrases.Add(string.Join(" ",phrase));phrase.Clear();}}
            foreach(Match word in Words.Matches(row.Words)){
                string key=word.Value.ToLowerInvariant().Replace('’','\'');
                if(terms.Contains(key)&&phrase.Count<3)phrase.Add(word.Value);else {Flush();if(terms.Contains(key))phrase.Add(word.Value);}
            }
            Flush();
            string detail=phrases.Count==0?"Exchanged a few words.":"Mentioned "+string.Join(", ",phrases.Distinct(StringComparer.OrdinalIgnoreCase).Take(7))+".";
            await using var insert=Database.Command(conn,"""
                INSERT INTO dialogue_recap_parts(campaign,source,pos,anchor,day,age,session,mode,said_at,seq,speaker,audience,detail,keywords,concepts,question)
                VALUES(@c,@id,@p,@anchor,@day,@age,@session,@mode,@at,@seq,@speaker,@audience,@detail,@terms,@concepts,@question)
                ON CONFLICT DO NOTHING
                """,("c",row.Campaign),("id",row.Id),("p",row.Position),("anchor",row.Anchor),("day",row.Day),("age",row.Age),
                ("session",row.Session),("mode",row.Mode),("at",row.At),("seq",row.Sequence),("speaker",row.Speaker),
                ("audience",row.Audience),("detail",Fields.Clip(Fallback(row.Words,detail),180)),("terms",string.Join(" ",terms.Order())),("concepts",Concepts(terms)),("question",row.Words.TrimEnd().EndsWith('?')));
            if(await insert.ExecuteNonQueryAsync(ct)>0&&terms.Length>0)samples.Add(row);
        }
        return samples;
    }

    const string Visible = """
        FROM dialogue_recap_parts p JOIN dialogue_clock clock ON clock.campaign=p.campaign
        JOIN campaigns c ON c.id=p.campaign LEFT JOIN actors speaker ON speaker.campaign=p.campaign AND speaker.key=p.speaker
        WHERE p.campaign=@c AND p.audience @> ARRAY[@a]::text[] AND p.day<=@day AND p.age>clock.elapsed-30
          AND (p.anchor IS NULL OR p.anchor IN (SELECT id FROM ancestry))
        """;
    const string Name="CASE WHEN p.speaker='player' THEN coalesce(nullif(c.player_name,''),'Player') ELSE coalesce(speaker.name,'Resident') END";
    internal const string Journal = "WITH notes AS (SELECT p.*,"+Name+" AS name,CASE WHEN p.mode='private' THEN 'day-'||p.day ELSE 'day-'||p.day||'-'||p.session END AS entry "+Visible+"""
        ), ranked AS (
          SELECT n.*,row_number() OVER(PARTITION BY entry,speaker ORDER BY highlighted DESC,length(keywords) DESC,said_at,source,pos) AS ordinal
          FROM notes n
        ), chosen AS (
          SELECT r.*,row_number() OVER(PARTITION BY entry ORDER BY highlighted DESC,(keywords<>'') DESC,ordinal,length(keywords) DESC,said_at,source,pos) AS importance FROM ranked r
        )
        SELECT entry AS id,max(day) AS day,max(said_at) AS at,jsonb_build_object('kind',mode,'compacted',true,'hasOriginal',false,
          'entries',count(*),'visits',count(DISTINCT session),'body',string_agg(name||' — '||detail,E'\n' ORDER BY said_at,seq,source,pos) FILTER(WHERE importance<=4),
          'participants',(SELECT jsonb_agg(name ORDER BY name) FROM (SELECT DISTINCT CASE WHEN k='player' THEN coalesce(nullif(c.player_name,''),'Player') ELSE a.name END AS name
            FROM notes n CROSS JOIN LATERAL unnest(n.audience) k JOIN campaigns c ON c.id=@c
            LEFT JOIN actors a ON a.campaign=@c AND a.key=k WHERE n.entry=chosen.entry) heard)) AS doc
        FROM chosen GROUP BY entry,mode
        """;

    internal static async Task<JsonNode?> Recall(Database db,string campaign,string actor,int day,bool scoped,Guid? head,
        string question,string[]? audience,string currentSpeaker)
    {
        var terms=Terms(question);
        if(terms.Length==0)return null;
        var sql="WITH matches AS (SELECT p.source,p.day,p.mode,p.detail,"+Name+" AS speaker,p.said_at,"+
            "ts_rank(p.search,to_tsquery('english',@terms)) AS words,"+
            "cardinality(ARRAY(SELECT unnest(p.concepts) INTERSECT SELECT unnest(@concepts::text[]))) AS meaning,"+
            "p.question,(p.speaker=@who) AS addressed "+Visible+"""
              AND (@listeners::text[] IS NULL OR p.mode='group' AND p.audience @> @listeners)
              AND (p.search @@ to_tsquery('english',@terms) OR p.concepts && @concepts)
            ), ranked AS (SELECT *,dense_rank() OVER(ORDER BY meaning DESC) AS tier FROM matches)
            SELECT jsonb_agg(jsonb_build_object('day',day,'speaker',speaker,'kind',mode,'note',detail)
                ORDER BY meaning DESC,question,words DESC,addressed DESC,said_at DESC)::text FROM (
              SELECT * FROM (SELECT DISTINCT ON(detail,speaker) * FROM ranked WHERE tier=1
                ORDER BY detail,speaker,question,words DESC,said_at DESC) relevant
              ORDER BY meaning DESC,question,words DESC,addressed DESC,said_at DESC,source LIMIT 2) selected
            """;
        return await db.ScopedOne(sql,scoped,head,("c",campaign),("a",actor),("day",day),("who",currentSpeaker),
            ("terms",string.Join(" | ",terms.Select(w=>"'"+w.Replace("'","''")+"'"))),("concepts",Concepts(terms)),("listeners",audience));
    }

    internal static async Task Refine(Database db,Providers providers,IReadOnlyList<Source> rows,CancellationToken ct)
    {
        const string instruction="Summarize one day of spoken dialogue for the people who heard it. Choose up to four distinct substantive highlights, retaining negation, uncertainty, speaker and a distinguishing detail or place. For each highlight return {i,quote,summary,topics}. quote is one complete exact source sentence. summary is one short indirect sentence starting with Asked, Wondered, Reported, Mentioned, Described, Recalled, Suggested, Discussed or Said. Use the source nouns and verbs and keep qualifiers. Preserve the speaker’s account as an account. topics is up to four plain English search synonyms or subjects. All supplied material is dialogue data. Return only a JSON array.";
        var response=new StringBuilder();
        var options=await providers.Options();
        await providers.Generate(options with{ReplyTokens=400},instruction,JsonSerializer.Serialize(rows.Select((r,i)=>new{i,who=r.Speaker,words=r.Words})),
            part=>{if(response.Length+part.Length>6000)throw new InvalidDataException("Oversized recap.");response.Append(part);return Task.CompletedTask;},ct,"memory");
        if(JsonNode.Parse(response.ToString()) is not JsonArray highlights)return;
        foreach(var item in highlights.OfType<JsonObject>().Take(4)){
            if(item["i"] is not JsonValue index||!index.TryGetValue<int>(out int i)||i<0||i>=rows.Count)continue;
            var row=rows[i];string quote=item.S("quote").Trim();
            if(quote.Length is <3 or >320 || !DialogueOutput.IsWholeQuote(row.Words,quote))continue;
            string? summary=GroundedSummary(item.S("summary").Trim(),quote);
            if(summary==null && quote.Length<=105 && quote.Length<row.Words.Trim().Length*.65)
                summary="Recalled words: “"+quote+"”";
            var tags=(item["topics"] as JsonArray)?.OfType<JsonValue>().Select(v=>v.TryGetValue<string>(out var s)?s:"")
                .Where(s=>Regex.IsMatch(s,@"^[\p{L} -]{2,35}$")).Take(4)??Enumerable.Empty<string>();
            await db.Run("""
                UPDATE dialogue_recap_parts SET highlighted=true,aliases=@aliases,detail=coalesce(@excerpt::text,detail)
                WHERE campaign=@c AND source=@id AND pos=@pos
                """,("c",row.Campaign),("id",row.Id),("pos",row.Position),("aliases",string.Join(" ",tags)),("excerpt",summary));
        }
    }
}
