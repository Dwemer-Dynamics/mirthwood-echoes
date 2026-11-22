using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Mirthwood.Echoes;
using Npgsql;

// Each part belongs to one delivered speaker and audience. Combining parts only after
// checking save ancestry also gives a correct recap when a save forks halfway through a visit.
internal static partial class DialogueRecaps
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
        UPDATE dialogue_recap_parts SET journal_note=NULL WHERE journal_note IS NOT NULL AND
          (left(detail,5)<>'Said ' OR journal_note NOT IN ('Reported '||substr(detail,6),'Reported that '||substr(detail,6),
            detail,'Said that '||substr(detail,6)));
        ALTER TABLE dialogue_recap_parts ADD COLUMN IF NOT EXISTS corrects_source uuid,
          ADD COLUMN IF NOT EXISTS corrects_pos int, ADD COLUMN IF NOT EXISTS corrects_sentence int,
          ADD COLUMN IF NOT EXISTS possible_targets text[];
        CREATE INDEX IF NOT EXISTS recap_corrections ON dialogue_recap_parts(campaign,corrects_source,corrects_pos,corrects_sentence)
          WHERE corrects_source IS NOT NULL;
        CREATE INDEX IF NOT EXISTS recap_possible_targets ON dialogue_recap_parts USING gin(possible_targets)
          WHERE possible_targets IS NOT NULL;
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
    static readonly HashSet<string> SpeechActs = new("ask asked request requested invite invited invitation permit permitted permission allow allowed refuse refused refusal deny denied denial".Split(' '));

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
        Read fragments in the original speaker's voice and keep nested speech with the person quoted. The original addressee in to can be different from a witness.
        Leave a missing addressee unresolved. A line to the group does not single out one person. Preserve refusals and conditions. A partial note cannot establish consent.
        Use a speaker's later correction when its target is clear. A withdrawn grant or an unclear withdrawal cannot establish current consent.
        Withdrawing a refusal does not grant permission. Preserve uncertainty when the target or the replacement is unclear.
        Another speaker's disagreement remains their own account.
        """;

    // Consume quoted speech before looking for a sentence break. A question inside
    // somebody's reported words belongs to the quoted person.
    static readonly Regex SpeechBoundary = new("""“[^”]*”|"[^"]*"|‘[^’]*’|(?<!\w)'[^'\n]+'(?!\w)|(?<end>(?<=[.!?…])\s+|(?<=[.!?…]["”’'])(?<afterQuote>\s+)|(?<=[.!?…]\))\s+|[\r\n]+)""", RegexOptions.Compiled);
    const string ReportingVerb = @"(?:asked|said|answered|replied|added|whispered|murmured|called|cried|shouted|exclaimed|explained|remarked|insisted|warned|wondered|promised)";
    const string ReportingRecipient = @"(?:(?:to|of)\s+)?(?:me|him|her|them|us|you|(?-i:\p{Lu})[\p{L}’'-]*)";
    static readonly Regex ReportingClause = new(@"\G(?:" + ReportingVerb + @"\b|(?:[\p{L}’'-]+\s+){1,4}" + ReportingVerb +
        @"(?:\s+(?:[\p{L}]+ly|" + ReportingRecipient + @")){0,2}\s*(?=[,;.!?…—]|$))",RegexOptions.IgnoreCase|RegexOptions.Compiled);
    static bool IsQuestion(string sentence) => sentence.TrimEnd(')').EndsWith('?');

    static IEnumerable<string> Sentences(string text)
    {
        int start = 0;
        foreach(Match match in SpeechBoundary.Matches(text)){
            if(!match.Groups["end"].Success || match.Groups["afterQuote"].Success && ReportingClause.IsMatch(text,match.Index+match.Length)) continue;
            if(match.Index>start) yield return text[start..match.Index];
            start = match.Index + match.Length;
        }
        if(start<text.Length) yield return text[start..];
    }

    static IEnumerable<(string Text,bool Gesture)> Parts(string line)
    {
        int start = 0;
        foreach(var action in ActionNotation.Find(line)){
            foreach(var sentence in Sentences(line[start..action.Open].Trim())) yield return (sentence,false);
            yield return (line[(action.Open+1)..action.End], true);
            start = action.End+1;
        }
        foreach(var sentence in Sentences(line[start..].Trim())) yield return (sentence,false);
    }

    static readonly Regex FirstPerson = new(@"^(?<subject>I|we)\s+(?<rest>.+)$", RegexOptions.IgnoreCase);
    static readonly Regex ModalQuestion = new(@"^(may|can|could|might|will|would|shall|should|must)\s+(I|we|you|he|she|they|it)\s+(.+)$", RegexOptions.IgnoreCase);
    static readonly Regex PersonWord = new(@"\b(?:(I|you)\s+(am|are|have|do)(?!['’])\b|I|my|mine|me|you|your|yours)\b(?!['’])", RegexOptions.IgnoreCase);
    // Keep direct or nested quotations in their own voice, including punctuation.
    static readonly Regex KeepPerspective = new(@"[,;:""“”‘]|(?:^|\s)'|\b(?:I|you|we)['’]|\b(?:our|ours|us|ourselves|yourself|yourselves|myself|said|asked|told|claimed)\b", RegexOptions.IgnoreCase);
    static readonly Regex KeepSubject = new(@"\bwe\b|\b(?:I|you)\s+(?!(?:may|might|can|could|shall|should|will|would|must|did|had|am|are|have|do)\b)\p{L}",RegexOptions.IgnoreCase);
    static readonly Regex Qualification = new(@"\b(?:unless|except|only|without|but|never|not|cannot|might|perhaps|doubt\w*|refus\w*|\w+n['’]t)\b", RegexOptions.IgnoreCase);
    static string Role(Match match)
    {
        if(match.Groups[1].Success){
            string person = match.Groups[1].Value.Equals("I",StringComparison.OrdinalIgnoreCase) ? "the speaker" : "the addressee";
            string verb = match.Groups[2].Value.ToLowerInvariant() switch {"have"=>"has","do"=>"does",_=>"is"};
            return person + " " + verb;
        }
        return match.Value.ToLowerInvariant() switch {
            "i" or "me" => "the speaker", "my" or "mine" => "the speaker's",
            "you" => "the addressee", _ => "the addressee's"
        };
    }

    static string Detail(string sentence, bool question, bool gesture, string[] terms)
    {
        if(gesture) return terms.Length == 0 ? "Made a gesture." : "Gestured about " + Fields.Clip(string.Join(", ",terms.Take(8)),116) + ".";
        string clause = sentence.Trim().TrimEnd('.','!','?','…').TrimEnd();
        if(clause.Length == 0) return "Exchanged a few words.";
        if(!question && clause.Equals("yes",StringComparison.OrdinalIgnoreCase)) return "Answered yes.";
        if(!question && clause.Equals("no",StringComparison.OrdinalIgnoreCase)) return "Answered no.";
        if(sentence.Length > 144){
            // Mark a partial account explicitly. Never turn a clipped condition into permission.
            var tokens = Regex.Matches(clause,@"\S+");
            string first = string.Join(" ",tokens.Take(8).TakeWhile(t=>t.Index+t.Length<=55));
            string last = string.Join(" ",tokens.TakeLast(8).Where(t=>t.Index>=clause.Length-50));
            var guard = Qualification.Match(clause,first.Length);
            string middle = guard.Success ? string.Join(" ",tokens.Where(t=>t.Index+t.Length>guard.Index && t.Index+t.Length<=guard.Index+48 &&
                t.Index+t.Length<clause.Length-last.Length).Take(6)) : "";
            var fragments = new[]{first,middle,last}.Where(s=>s.Length>0);
            return (question ? "Partial question" : "Partial account") + " in their words “" + string.Join(" … ",fragments) + "”.";
        }
        if(!KeepPerspective.IsMatch(clause)){
            var modal = ModalQuestion.Match(clause);
            if(question && modal.Success && !KeepSubject.IsMatch(modal.Groups[3].Value)){
                string subject = modal.Groups[2].Value.ToLowerInvariant() switch {
                    "we" => "the speaker and others", "i" => "the speaker", "you" => "the addressee",
                    var pronoun => pronoun
                };
                string predicate = PersonWord.Replace(modal.Groups[3].Value,Role);
                if(predicate.Length<=144) return $"Asked whether {subject} {modal.Groups[1].Value.ToLowerInvariant()} {predicate}.";
            }
            var first = FirstPerson.Match(clause);
            if(!question && first.Success && !KeepSubject.IsMatch(first.Groups["rest"].Value)){
                bool plural = first.Groups["subject"].Value.Equals("we",StringComparison.OrdinalIgnoreCase);
                string rest = PersonWord.Replace(first.Groups["rest"].Value,Role);
                if(!plural) rest = Regex.Replace(rest, @"^(am|was)\b", m => m.Value.Equals("am",StringComparison.OrdinalIgnoreCase) ? "are" : "were", RegexOptions.IgnoreCase);
                if(rest.Length<=160) return "Said they" + (plural ? " and others " : " ") + rest + ".";
            }
            if(!question && !KeepSubject.IsMatch(clause)){
                string roles = PersonWord.Replace(clause,Role);
                if(roles != clause && roles.Length<=170) return "Said " + roles + ".";
            }
        }
        return Fragment(sentence,question);
    }

    static string Fragment(string sentence,bool question) =>
        (question ? "Asked “" : "Recounted “") + sentence.Trim().TrimEnd('.') + "”.";

    // A journal refinement can reframe an existing indirect report. Its protected
    // predicate, punctuation and speech act must agree exactly with the source note.
    static string? JournalNote(string summary, Source row)
    {
        if(summary.Length is <8 or >150) return null;
        string note = row.Note ?? Detail(row.Words,IsQuestion(row.Words),false,Terms(row.Words));
        if(!note.StartsWith("Said ",StringComparison.Ordinal)) return null;
        string predicate = note[5..];
        return summary == note || summary == "Reported " + predicate || summary == "Reported that " + predicate ||
            summary == "Said that " + predicate ? summary : null;
    }

    internal sealed record Source(string Campaign,Guid Id,int Position,Guid? Anchor,int Day,long Age,string Session,
        string Mode,DateTime At,int Sequence,string Speaker,string[] Audience,string Words,Guid? Branch,int Sentence=0)
    {
        public bool UnresolvedYou { get; init; }
        public string? Note { get; init; }
    }
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
            SELECT s.*,coalesce(b.branch,s.anchor),
              s.mode='group' AND NOT coalesce(g.payload->'line'->>'to'=ANY(s.audience),false)
            FROM speech s LEFT JOIN branches b ON (b.campaign,b.id)=(s.campaign,s.anchor)
            LEFT JOIN cue_events g ON s.mode='group' AND (g.campaign,g.id)=(s.campaign,s.id)
            ORDER BY s.campaign,s.day,s.at,s.seq,s.id,s.pos
            """);
        var rows=new List<Source>();
        await using(var reader=await read.ExecuteReaderAsync(ct)){
            while(await reader.ReadAsync(ct))rows.Add(new(reader.GetString(0),reader.GetGuid(1),reader.GetInt32(2),
                reader.IsDBNull(3)?null:reader.GetGuid(3),reader.GetInt32(4),reader.GetInt64(5),reader.GetString(6),
                reader.GetString(7),reader.GetDateTime(8),reader.GetInt32(9),reader.GetString(10),reader.GetFieldValue<string[]>(11),reader.GetString(12),reader.IsDBNull(13)?null:reader.GetGuid(13))
                {UnresolvedYou=reader.GetBoolean(14)});
        }
        foreach(var row in rows){
            foreach(var part in await ResolveNotes(conn,row,ct)){
                var target = part.Target is {Possible:null} definite ? definite : null;
                await using var insert=Database.Command(conn,"""
                    INSERT INTO dialogue_recap_parts(campaign,source,pos,sentence,anchor,day,age,session,mode,said_at,seq,speaker,audience,detail,keywords,concepts,question,
                        corrects_source,corrects_pos,corrects_sentence,possible_targets)
                    VALUES(@c,@id,@p,@sentence,@anchor,@day,@age,@session,@mode,@at,@seq,@speaker,@audience,@detail,@terms,@concepts,@question,@previous,@position,@earlier,@possible)
                    ON CONFLICT DO NOTHING
                    """,("c",row.Campaign),("id",row.Id),("p",row.Position),("anchor",row.Anchor),("day",row.Day),("age",row.Age),
                    ("session",row.Session),("mode",row.Mode),("at",row.At),("seq",row.Sequence),("speaker",row.Speaker),
                    ("audience",row.Audience),("detail",part.Note),("terms",string.Join(" ",part.Terms.Order())),
                    ("concepts",Concepts(part.Terms)),("question",part.Question),("sentence",part.Index),
                    ("previous",target?.Source),("position",target?.Position),("earlier",target?.Sentence),("possible",part.Target?.Possible));
                if(await insert.ExecuteNonQueryAsync(ct)>0 && !part.Gesture && !part.Correction && part.Terms.Length>0 && row.Audience.Length>0)
                    samples.Add(row with {Words=part.Text,Sentence=part.Index,Note=part.Note});
            }
        }
        return samples;
    }

    // The retained group receipt still contains its validated target after its words
    // retire. This also resolves targets for previously compacted rows, without guessing
    // from the witness list when an old receipt has no target.
    const string Eligible = """
        SELECT p.*,CASE WHEN p.speaker='player' THEN coalesce(nullif(c.player_name,''),'Player') ELSE coalesce(speaker.name,'Resident') END AS name,
          contact.key AS to_key,CASE contact.key WHEN 'player' THEN coalesce(nullif(c.player_name,''),'Player') WHEN 'group' THEN 'the group' ELSE recipient.name END AS to_name
        FROM dialogue_recap_parts p JOIN dialogue_clock clock ON clock.campaign=p.campaign
        JOIN campaigns c ON c.id=p.campaign LEFT JOIN actors speaker ON speaker.campaign=p.campaign AND speaker.key=p.speaker
        CROSS JOIN LATERAL (SELECT CASE WHEN p.mode='group' THEN
            (SELECT CASE WHEN g.payload->'line'->>'to'='group' OR g.payload->'line'->>'to'=ANY(p.audience)
              THEN g.payload->'line'->>'to' END FROM cue_events g WHERE (g.campaign,g.id)=(p.campaign,p.source))
          WHEN p.speaker='player' THEN (array_remove(p.audience,'player'))[1] ELSE 'player' END AS key) contact
        LEFT JOIN actors recipient ON recipient.campaign=p.campaign AND recipient.key=contact.key
        WHERE p.campaign=@c AND p.audience @> ARRAY[@a]::text[] AND p.day<=@day AND p.age>clock.elapsed-30
          AND (p.anchor IS NULL OR p.anchor IN (SELECT id FROM ancestry))
        """;
    const string Revision = """
        LEFT JOIN LATERAL (SELECT revision.* FROM (
          SELECT r.* FROM eligible r WHERE r.corrects_source=coalesce(p.corrects_source,p.source) AND r.corrects_pos=coalesce(p.corrects_pos,p.pos)
            AND (r.corrects_sentence=coalesce(p.corrects_sentence,p.sentence)
              OR r.corrects_sentence=-1 AND p.corrects_source IS NULL AND p.sentence<r.sentence AND NOT p.question)
          UNION ALL SELECT r.* FROM eligible r WHERE r.possible_targets @> ARRAY[concat_ws('/',
            coalesce(p.corrects_source,p.source),coalesce(p.corrects_pos,p.pos),coalesce(p.corrects_sentence,p.sentence))]
          ) revision ORDER BY age DESC,said_at DESC,seq DESC,source DESC,pos DESC,sentence DESC LIMIT 1) rev
          ON (rev.source,rev.pos,rev.sentence)<>(p.source,p.pos,p.sentence)
        """;
    const string RevisedDetail = """
        CASE WHEN rev.corrects_sentence=-1 OR rev.possible_targets IS NOT NULL THEN 'A later correction in this account has an unclear target — '
          WHEN rev.detail LIKE 'Clarified their account.%' THEN 'Earlier account, later clarified — '
          ELSE 'Earlier account, later corrected — ' END||p.detail||' '||rev.name||coalesce(' to '||rev.to_name,'')||' — '||rev.detail
        """;
    internal const string Journal = "WITH eligible AS NOT MATERIALIZED ("+Eligible+"), notes AS (SELECT p.*,CASE WHEN rev.source IS NULL THEN coalesce(p.journal_note,p.detail) ELSE "
        +RevisedDetail+"""
         END AS note,rev.source IS NOT NULL AS revised,CASE WHEN p.mode='private' THEN 'day-'||p.day ELSE 'day-'||p.day||'-'||p.session END AS entry,
          p.corrects_source IS NOT NULL AND EXISTS(SELECT FROM eligible base WHERE base.source=p.corrects_source AND base.pos=p.corrects_pos
            AND (base.sentence=p.corrects_sentence OR p.corrects_sentence=-1 AND base.sentence<p.sentence AND NOT base.question)
            AND base.day=p.day AND base.mode=p.mode AND (p.mode='private' OR base.session=p.session)) AS included_with_claim
        FROM eligible p
        """+" "+Revision+"""
        ), ranked AS (
          SELECT n.*,row_number() OVER(PARTITION BY entry,speaker ORDER BY included_with_claim,(highlighted OR revised) DESC,length(keywords) DESC,said_at,source,pos,sentence) AS ordinal
          FROM notes n
        ), chosen AS (
          SELECT r.*,row_number() OVER(PARTITION BY entry ORDER BY included_with_claim,(highlighted OR revised) DESC,(keywords<>'') DESC,ordinal,length(keywords) DESC,said_at,source,pos,sentence) AS importance FROM ranked r
        )
        SELECT entry AS id,max(day) AS day,max(said_at) AS at,jsonb_build_object('kind',mode,'compacted',true,'hasOriginal',false,
          'entries',count(DISTINCT (source,pos)),'visits',count(DISTINCT session),'body',string_agg(name||coalesce(' to '||to_name,'')||' — '||note,E'\n' ORDER BY said_at,seq,source,pos,sentence) FILTER(WHERE importance<=4 AND NOT included_with_claim),
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
        string inquiry = asked.Length>0 ? asked : question;
        bool seeksRequest = Regex.IsMatch(inquiry,@"\b(?:asked|requested)\b|\bdid\s+(?:\w+\s+){1,3}(?:ask|request)\b",RegexOptions.IgnoreCase);
        // An invitation can be worded as a question or a statement. Rank the named
        // event before the wording of the speech act (e.g. which supper was offered).
        string eventQuery = Words.Matches(inquiry.ToLowerInvariant()).Any(m=>SpeechActs.Contains(m.Value))
            ? Query(focus.Where(w=>!SpeechActs.Contains(w)).ToArray()) : "";
        string names = " " + string.Join(" ",Words.Matches(question.ToLowerInvariant())) + " ";
        var sql = $"""
            WITH eligible AS NOT MATERIALIZED ({Eligible}
                AND (@listeners::text[] IS NULL OR p.mode='group' AND p.audience @> @listeners)
            ), matches AS (
              SELECT p.*,(p.speaker=@who) AS addressed,coalesce(strpos(@names,' '||lower(p.to_name)||' ')>0,false) AS named_target,length(p.keywords) AS specificity,
                ts_rank(p.search,to_tsquery('english',@focus)) AS words,coalesce(ts_rank(p.search,to_tsquery('english',nullif(@event,''))),0) AS event_words,
                cardinality(ARRAY(SELECT unnest(p.concepts) INTERSECT SELECT unnest(@subjects::text[]))) AS subject,
                cardinality(ARRAY(SELECT unnest(p.concepts) INTERSECT SELECT unnest(@concepts::text[]))) AS meaning
              FROM eligible p WHERE p.search @@ to_tsquery('english',@terms) OR p.concepts && @concepts
            ), ranked AS (
              SELECT *,dense_rank() OVER(ORDER BY (subject>0 OR words>0) DESC,subject DESC,meaning DESC,named_target DESC,event_words DESC,(question=@requests) DESC,(words>0) DESC) AS tier FROM matches
            ), best AS MATERIALIZED (
              SELECT source,pos,sentence,corrects_source,corrects_pos,corrects_sentence,day,mode,detail,speaker,name,to_key,to_name,
                question,words,addressed,specificity,said_at,seq FROM ranked WHERE tier=1
            ), accounts AS (
              -- Originals are already unique by source position. Only linked amendments can share a root.
              SELECT * FROM best WHERE corrects_source IS NULL
              UNION ALL
              (SELECT DISTINCT ON(r.corrects_source,r.corrects_pos,r.corrects_sentence) r.* FROM best r
                WHERE r.corrects_source IS NOT NULL AND NOT EXISTS (SELECT FROM best b WHERE
                  (b.source,b.pos,b.sentence)=(r.corrects_source,r.corrects_pos,r.corrects_sentence))
                ORDER BY r.corrects_source,r.corrects_pos,r.corrects_sentence,r.words DESC,r.said_at DESC,r.seq DESC,r.source,r.pos,r.sentence)
            ), relevant AS (
              SELECT DISTINCT ON(detail,speaker,to_key) * FROM accounts
              ORDER BY detail,speaker,to_key,question,words DESC,said_at DESC,source,pos,sentence
            ), selected AS (
              SELECT * FROM relevant ORDER BY question,words DESC,addressed DESC,specificity DESC,said_at DESC,source,pos,sentence LIMIT 2
            )
            SELECT jsonb_agg(jsonb_strip_nulls(jsonb_build_object('source',p.source,'position',p.pos,'sentence',p.sentence,'day',p.day,
              'speaker',p.name,'speakerKey',p.speaker,'to',p.to_name,'toKey',p.to_key,'kind',p.mode,
              'note',CASE WHEN rev.source IS NULL THEN p.detail ELSE {RevisedDetail} END) ||
              CASE WHEN rev.source IS NULL THEN jsonb_build_object() ELSE jsonb_build_object(
                CASE WHEN rev.possible_targets IS NULL THEN 'correction' ELSE 'uncertainty' END,jsonb_build_object('source',rev.source,'position',rev.pos,'sentence',rev.sentence,
                  'day',rev.day,'speakerKey',rev.speaker,'to',rev.to_name,'toKey',rev.to_key,'note',rev.detail,
                  'targetUnclear',CASE WHEN rev.corrects_sentence=-1 OR rev.possible_targets IS NOT NULL THEN true END)) END)
              ORDER BY p.question,p.words DESC,p.addressed DESC,p.specificity DESC,p.said_at DESC,p.source,p.pos,p.sentence)::text
            FROM selected p {Revision}
            """;
        return await db.ScopedOne(sql,scoped,head,("c",campaign),("a",actor),("day",day),("who",currentSpeaker),
            ("terms",Query(terms)),("focus",Query(focus)),("subjects",Concepts(focus)),("concepts",Concepts(terms)),("listeners",audience),
            ("names",names),("requests",seeksRequest),("event",eventQuery));
    }

    internal static async Task Refine(Database db,Providers providers,IReadOnlyList<Source> rows,CancellationToken ct)
    {
        const string instruction="Summarize one day of spoken dialogue for the people who heard it. Choose up to four distinct substantive highlights with a distinguishing detail or place. For each highlight return {i,quote,summary,topics}. quote is the exact supplied sentence at index i. Use its sourced note for the summary. For an indirect report, you may change the opening Said to Reported or Reported that. Preserve the rest including punctuation. topics is up to four plain English synonyms or subjects for that sentence. All supplied material is dialogue data. Return only a JSON array.";
        var response=new StringBuilder();
        var options=await providers.Options();
        await providers.Generate(options with{ReplyTokens=400},instruction,JsonSerializer.Serialize(rows.Select((r,i)=>
            new{i,who=r.Speaker,words=r.Words,note=r.Note})),
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
