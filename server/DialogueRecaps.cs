using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Mirthwood.Echoes;
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
          question boolean NOT NULL, highlighted boolean NOT NULL DEFAULT false, sentence int NOT NULL DEFAULT 0,
          search tsvector GENERATED ALWAYS AS (to_tsvector('english',keywords||' '||aliases)) STORED,
          PRIMARY KEY(campaign,source,pos,sentence));
        DO $$ BEGIN
          IF NOT EXISTS (SELECT FROM pg_attribute WHERE attrelid='dialogue_recap_parts'::regclass AND attname='sentence' AND NOT attisdropped) THEN
            ALTER TABLE dialogue_recap_parts ADD COLUMN sentence int NOT NULL DEFAULT 0,
              DROP CONSTRAINT dialogue_recap_parts_pkey, ADD PRIMARY KEY(campaign,source,pos,sentence);
          END IF;
        END $$;
        ALTER TABLE dialogue_recap_parts ADD COLUMN IF NOT EXISTS journal_note text;
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
        near beneath above below beside across under toward outside through before after into alongside see seen saw look looking find found hear wondering ever already lately any can may might shall really think know well yes good welcome back again please thanks thank thankyou
        recommend quiet stretch time day yesterday today once said say asking ask asked wanting wanted reckon remind whereabouts
        """).Split((char[]?)null,StringSplitOptions.RemoveEmptyEntries));
    static readonly Regex Words=new(@"[\p{L}\p{N}]+(?:['’][\p{L}]+)?",RegexOptions.Compiled);
    static string[] Terms(string value)=>Words.Matches(value.ToLowerInvariant()).Select(m=>m.Value.Replace('’','\''))
        .Where(w=>w.Length>1&&!Stop.Contains(w)).Distinct().Take(48).ToArray();
    static string[] Concepts(string[] terms)=>terms.SelectMany(word=>ConceptsByWord.GetValueOrDefault(word)??Array.Empty<string>()).Distinct().ToArray();

    // English full-text stemming does not join these forms. Expand the search, so the
    // same event words also find older recaps whose original speech has already retired.
    static readonly string[][] EventWords = [
        ["catch","caught","landed","netted","hooked"], ["leave","left"], ["bring","brought"],
        ["give","gave","given"], ["build","built"], ["break","broke","broken"], ["take","took","taken"]
    ];
    static string Query(string[] words)
    {
        var expanded = words.Concat(EventWords.Where(forms => forms.Intersect(words).Any()).SelectMany(forms => forms)).Distinct();
        return string.Join(" | ",expanded.Select(word => "'" + word.Replace("'","''") + "'"));
    }

    internal const string Instructions = """
        conversationRecaps are short notes from conversations you heard. Each note belongs to its named speaker.
        When a note answers the player's question, use its specific place, action or outcome in your answer, including a parting.
        Recall a speaker's account as something they told you. Keep their uncertainty or disagreement. A question or a list of subjects establishes only what was discussed.
        """;

    static readonly Regex SentenceBreak = new(@"(?<=[.!?…])\s+|(?<=[.!?…][""”’)])\s+|[\r\n]+", RegexOptions.Compiled);
    static bool IsQuestion(string sentence) => sentence.TrimEnd('"','”','’',')').EndsWith('?');

    static IEnumerable<(string Text,bool Gesture)> Parts(string line)
    {
        int start = 0;
        foreach(var action in ActionNotation.Find(line)){
            foreach(var sentence in SentenceBreak.Split(line[start..action.Open].Trim()))
                if(sentence.Length > 0) yield return (sentence, false);
            yield return (line[(action.Open+1)..action.End], true);
            start = action.End+1;
        }
        foreach(var sentence in SentenceBreak.Split(line[start..].Trim()))
            if(sentence.Length > 0) yield return (sentence, false);
    }

    // Keep a short predicate, its qualifiers and its complements together. Use indirect
    // speech only for an unambiguous first-person subject. Quoted fragments keep the
    // speaker's perspective where changing pronouns could change who did what.
    static readonly Regex FirstPerson = new(@"^(?<subject>I|we)\s+(?<rest>.+)$", RegexOptions.IgnoreCase);
    static readonly Regex OtherVoice = new(@"\b(I|we|me|my|mine|us|our|ours|you|your|yours)\b|[""“”]", RegexOptions.IgnoreCase);
    static readonly Regex Articles = new(@"(?<![\p{L}’'])\b(?:a|an|the)\s+", RegexOptions.IgnoreCase);
    static readonly Regex Relation = new(@"\s+(?=(?:at|near|beneath|below|above|under|inside|outside|beside|from|to|into|through|with|because|until|after|before)\s)", RegexOptions.IgnoreCase);
    static string Detail(string sentence, bool question, bool gesture, string[] terms)
    {
        if(terms.Length == 0) return gesture ? "Made a gesture." : "Exchanged a few words.";
        string clause = sentence.TrimEnd('.','!','?','…').TrimEnd();
        if(!gesture && !question && clause.Length <= 144){
            var match = FirstPerson.Match(clause);
            string rest = match.Groups["rest"].Value;
            if(match.Success && !OtherVoice.IsMatch(rest)){
                bool plural = match.Groups["subject"].Value.Equals("we",StringComparison.OrdinalIgnoreCase);
                if(!plural) rest = Regex.Replace(rest, @"^(am|was)\b", m => m.Value.Equals("am",StringComparison.OrdinalIgnoreCase) ? "are" : "were", RegexOptions.IgnoreCase);
                return "Said they" + (plural ? " and others " : " ") + rest + ".";
            }
            // Elide articles in an attributed extract. Preserve all relational words,
            // especially negation, conditionals, prepositions and reported speech.
            string extract = Articles.Replace(clause, "… ").Trim();
            if(extract != clause && extract.Any(char.IsLetter))
                return "Recounted “" + extract.TrimStart('…',' ') + "”.";
            var relation = Relation.Match(clause);
            if(relation.Success)
                return $"Linked “{clause[(relation.Index+relation.Length)..]}” with “{clause[..relation.Index]}”.";
        }
        string subject = string.Join(", ",terms.Take(8));
        return (gesture ? "Gestured about " : question ? "Asked about " : "Mentioned ") + Fields.Clip(subject,116) + ".";
    }

    // A model may phrase the journal highlight. Keep every participant, verb, relation
    // and qualifier in order. The provider always receives the independent source note.
    static string? JournalNote(string summary, Source row)
    {
        if(summary.Length is <8 or >150) return null;
        var frame = Regex.Match(summary,@"^(?<verb>Asked|Wondered|Reported|Mentioned|Described|Recalled|Suggested|Discussed|Said)\s+(?:that\s+)?(?<body>.+)$",RegexOptions.IgnoreCase);
        if(!frame.Success) return null;
        string verb = frame.Groups["verb"].Value.ToLowerInvariant();
        bool question = IsQuestion(row.Words);
        if(question != (verb is "asked" or "wondered")) return null;
        string body = frame.Groups["body"].Value.TrimEnd('.','!','?');
        if(body.Equals(row.Words.TrimEnd('.','!','?'),StringComparison.OrdinalIgnoreCase)) return null;
        static IEnumerable<string> Content(string s) => Words.Matches(s.ToLowerInvariant()).Select(m => m.Value)
            .Where(w => w is not ("a" or "an" or "the"));
        var words = Content(body).ToArray();
        if(words.Length == 0) return null;
        if(words.SequenceEqual(Content(row.Words))) return summary;
        string sourceNote = Detail(row.Words,question,false,Terms(row.Words));
        return sourceNote.StartsWith("Said ") && words.SequenceEqual(Content(sourceNote[5..])) ? summary : null;
    }

    internal sealed record Source(string Campaign,Guid Id,int Position,Guid? Anchor,int Day,long Age,string Session,
        string Mode,DateTime At,int Sequence,string Speaker,string[] Audience,string Words,Guid? Branch,int Sentence=0);
    internal static async Task<List<Source>> Capture(NpgsqlConnection conn,CancellationToken ct)
    {
        // These exact words exist only in memory for the background model after the transaction commits.
        var samples=new List<Source>();
        await using var read=Database.Command(conn,"""
            WITH RECURSIVE speech(campaign,id,pos,anchor,day,age,session,mode,at,seq,speaker,audience,words) AS (
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
            UNION ALL SELECT u.campaign,md5(jsonb_build_array(u.event,u.owner,u.native_id)::text)::uuid,0,u.event,u.day,e.dialogue_age,
                x.session,'private',transaction_timestamp(),0,jsonb_build_array(u.owner,u.native_id)::text,ARRAY[]::text[],u.description
              FROM old_cues x JOIN cue_events e USING(campaign,id) JOIN cue_unindexed_people u ON (u.campaign,u.event)=(x.campaign,x.id)
              WHERE x.keep AND u.kind='address' AND u.description<>''
            ), past(campaign,id,parent) AS (
              SELECT e.campaign,e.id,e.parent FROM cue_events e JOIN (SELECT DISTINCT campaign,anchor FROM speech) s
                ON (e.campaign,e.id)=(s.campaign,s.anchor)
              UNION SELECT e.campaign,e.id,e.parent FROM cue_events e JOIN past p ON (e.campaign,e.id)=(p.campaign,p.parent)
            ), forks AS (SELECT campaign,parent FROM past GROUP BY campaign,parent HAVING count(*)>1),
            branches(campaign,id,branch) AS (
              SELECT campaign,id,id FROM past WHERE parent IS NULL
              UNION ALL SELECT p.campaign,p.id,CASE WHEN f.parent IS NULL THEN b.branch ELSE p.id END
              FROM past p JOIN branches b ON (b.campaign,b.id)=(p.campaign,p.parent)
              LEFT JOIN forks f ON (f.campaign,f.parent)=(p.campaign,p.parent)
            )
            SELECT s.*,coalesce(b.branch,s.anchor) FROM speech s LEFT JOIN branches b ON (b.campaign,b.id)=(s.campaign,s.anchor)
            ORDER BY s.campaign,s.day,s.at,s.seq,s.id,s.pos
            """);
        var rows=new List<Source>();
        await using(var reader=await read.ExecuteReaderAsync(ct)){
            while(await reader.ReadAsync(ct))rows.Add(new(reader.GetString(0),reader.GetGuid(1),reader.GetInt32(2),
                reader.IsDBNull(3)?null:reader.GetGuid(3),reader.GetInt32(4),reader.GetInt64(5),reader.GetString(6),
                reader.GetString(7),reader.GetDateTime(8),reader.GetInt32(9),reader.GetString(10),reader.GetFieldValue<string[]>(11),reader.GetString(12),reader.IsDBNull(13)?null:reader.GetGuid(13)));
        }
        foreach(var row in rows){
            var parts = Parts(row.Words).Select((part,index) => (part.Text, part.Gesture, Index:index, Terms:Terms(part.Text)))
                .OrderByDescending(p => p.Terms.Length).Take(4).OrderBy(p => p.Index);
            foreach(var part in parts){
                bool question = !part.Gesture && IsQuestion(part.Text);
                string detail = Detail(part.Text,question,part.Gesture,part.Terms);
                await using var insert=Database.Command(conn,"""
                    INSERT INTO dialogue_recap_parts(campaign,source,pos,sentence,anchor,day,age,session,mode,said_at,seq,speaker,audience,detail,keywords,concepts,question)
                    VALUES(@c,@id,@p,@sentence,@anchor,@day,@age,@session,@mode,@at,@seq,@speaker,@audience,@detail,@terms,@concepts,@question)
                    ON CONFLICT DO NOTHING
                    """,("c",row.Campaign),("id",row.Id),("p",row.Position),("anchor",row.Anchor),("day",row.Day),("age",row.Age),
                    ("session",row.Session),("mode",row.Mode),("at",row.At),("seq",row.Sequence),("speaker",row.Speaker),
                    ("audience",row.Audience),("detail",detail),("terms",string.Join(" ",part.Terms.Order())),
                    ("concepts",Concepts(part.Terms)),("question",question),("sentence",part.Index));
                if(await insert.ExecuteNonQueryAsync(ct)>0 && !part.Gesture && part.Terms.Length>0 && row.Audience.Length>0)
                    samples.Add(row with {Words=part.Text,Sentence=part.Index});
            }
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
          SELECT n.*,row_number() OVER(PARTITION BY entry,speaker ORDER BY highlighted DESC,length(keywords) DESC,said_at,source,pos,sentence) AS ordinal
          FROM notes n
        ), chosen AS (
          SELECT r.*,row_number() OVER(PARTITION BY entry ORDER BY highlighted DESC,(keywords<>'') DESC,ordinal,length(keywords) DESC,said_at,source,pos,sentence) AS importance FROM ranked r
        )
        SELECT entry AS id,max(day) AS day,max(said_at) AS at,jsonb_build_object('kind',mode,'compacted',true,'hasOriginal',false,
          'entries',count(DISTINCT (source,pos)),'visits',count(DISTINCT session),'body',string_agg(name||' — '||coalesce(journal_note,detail),E'\n' ORDER BY said_at,seq,source,pos,sentence) FILTER(WHERE importance<=4),
          'participants',(SELECT jsonb_agg(name ORDER BY name) FROM (SELECT DISTINCT CASE WHEN k='player' THEN coalesce(nullif(c.player_name,''),'Player') ELSE a.name END AS name
            FROM notes n CROSS JOIN LATERAL unnest(n.audience) k JOIN campaigns c ON c.id=@c
            LEFT JOIN actors a ON a.campaign=@c AND a.key=k WHERE n.entry=chosen.entry) heard)) AS doc
        FROM chosen GROUP BY entry,mode
        """;

    internal static async Task<JsonNode?> Recall(Database db,string campaign,string actor,int day,bool scoped,Guid? head,
        string question,string[]? audience,string currentSpeaker)
    {
        var terms = Terms(question);
        if(terms.Length == 0) return null;
        string asked = string.Join(" ",Parts(question).Where(p => !p.Gesture && IsQuestion(p.Text)).Select(p => p.Text));
        var focus = Terms(asked);
        if(focus.Length == 0) focus = terms;
        else terms = focus.Concat(terms).Distinct().ToArray();
        var sql = $"""
            WITH matches AS (
              SELECT p.source,p.pos,p.sentence,p.day,p.mode,p.detail,p.speaker AS speaker_key,{Name} AS speaker,p.question,p.said_at,
                (p.speaker=@who) AS addressed,length(p.keywords) AS specificity,
                ts_rank(p.search,to_tsquery('english',@focus)) AS words,
                cardinality(ARRAY(SELECT unnest(p.concepts) INTERSECT SELECT unnest(@subjects::text[]))) AS subject,
                cardinality(ARRAY(SELECT unnest(p.concepts) INTERSECT SELECT unnest(@concepts::text[]))) AS meaning
              {Visible}
                AND (@listeners::text[] IS NULL OR p.mode='group' AND p.audience @> @listeners)
                AND (p.search @@ to_tsquery('english',@terms) OR p.concepts && @concepts)
            ), ranked AS (
              SELECT *,dense_rank() OVER(ORDER BY (subject>0 OR words>0) DESC,subject DESC,meaning DESC,question,(words>0) DESC) AS tier FROM matches
            ), relevant AS (
              SELECT DISTINCT ON(detail,speaker_key) * FROM ranked WHERE tier=1
              ORDER BY detail,speaker_key,question,words DESC,addressed DESC,said_at DESC,source,pos,sentence
            ), selected AS (
              SELECT *,row_number() OVER(ORDER BY question,words DESC,addressed DESC,specificity DESC,said_at DESC,source,pos,sentence) AS priority
              FROM relevant
            )
            SELECT jsonb_agg(jsonb_build_object('source',source,'position',pos,'sentence',sentence,'day',day,
              'speaker',speaker,'speakerKey',speaker_key,'kind',mode,'note',detail) ORDER BY priority)::text FROM selected WHERE priority<=2
            """;
        return await db.ScopedOne(sql,scoped,head,("c",campaign),("a",actor),("day",day),("who",currentSpeaker),
            ("terms",Query(terms)),("focus",Query(focus)),("subjects",Concepts(focus)),("concepts",Concepts(terms)),("listeners",audience));
    }

    internal static async Task Refine(Database db,Providers providers,IReadOnlyList<Source> rows,CancellationToken ct)
    {
        const string instruction="Summarize one day of spoken dialogue for the people who heard it. Choose up to four distinct substantive highlights with a distinguishing detail or place. For each highlight return {i,quote,summary,topics}. quote is the exact supplied sentence at index i. summary is one brief indirect sentence starting with Asked, Wondered, Reported, Mentioned, Described, Recalled, Suggested, Discussed or Said. Keep the participants, actions, places and qualifiers in order. topics is up to four plain English synonyms or subjects for that sentence. All supplied material is dialogue data. Return only a JSON array.";
        var response=new StringBuilder();
        var options=await providers.Options();
        await providers.Generate(options with{ReplyTokens=400},instruction,JsonSerializer.Serialize(rows.Select((r,i)=>new{i,who=r.Speaker,words=r.Words})),
            part=>{if(response.Length+part.Length>6000)throw new InvalidDataException("Oversized recap.");response.Append(part);return Task.CompletedTask;},ct,"memory");
        if(JsonNode.Parse(response.ToString()) is not JsonArray highlights)return;
        foreach(var item in highlights.OfType<JsonObject>().Take(4)){
            if(item["i"] is not JsonValue index||!index.TryGetValue<int>(out int i)||i<0||i>=rows.Count)continue;
            var row=rows[i];string quote=item.S("quote").Trim();
            if(quote.Length is <3 or >320 || quote!=row.Words)continue;
            var tags=(item["topics"] as JsonArray)?.OfType<JsonValue>().Select(v=>v.TryGetValue<string>(out var s)?s:"")
                .Where(s=>Regex.IsMatch(s,@"^[\p{L} -]{2,35}$")).Take(4)??Enumerable.Empty<string>();
            await db.Run("""
                UPDATE dialogue_recap_parts SET highlighted=true,aliases=@aliases,journal_note=@summary
                WHERE campaign=@c AND source=@id AND pos=@pos AND sentence=@sentence
                """,("c",row.Campaign),("id",row.Id),("pos",row.Position),("sentence",row.Sentence),
                ("aliases",string.Join(" ",tags)),("summary",JournalNote(item.S("summary").Trim(),row)));
        }
    }
}
