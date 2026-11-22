using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Npgsql;
using Mirthwood.Echoes;

internal static class CharacterKnowledge
{
    internal const string Instructions = """
        After the spoken fields you may include learned, up to three objects. Each has speaker ("player" or "npc"; in a group "previous" or "npc"), subject ("self" or the exact name explicitly mentioned), facet (favorite food, favorite color, interest, food preference, dislike, background, or major moment), value (short words copied from the quote), and quote (exact full spoken sentence). Use [] unless the speaker clearly reveals a lasting detail. Preserve who likes what, tense, negation and hearsay. A meal order, clothes, a guess, a joke, a hypothetical and unspoken alternatives do not establish preferences. For a major moment use an actual consequential exchange such as a reconciliation or a personal loss. Do not infer another person's preferences from a resident's suggestion. Never say the annotations aloud.
        Character knowledge belongs to its listener. Words someone reported must stay attributed to that speaker. Prefer the latest direct disclosure about the same preference. If a natural opening fits, a remembered detail or your own activity can supply a brief new thought. A conversation recap describes what its named speaker discussed. A question, a recollection, and a witnessed event carry different weight. Prefer a later correction. Answer the current speaker first. Give a volunteered subject a rest after you have mentioned it. A direct question can reopen it.
        """;

    internal const string Schema = """
        CREATE TABLE IF NOT EXISTS character_knowledge(
          campaign text, holder text, subject text NOT NULL, speaker text NOT NULL, facet text NOT NULL,
          value text NOT NULL, stance text NOT NULL, quote text NOT NULL, question text NOT NULL,
          day int NOT NULL, source uuid NOT NULL, anchor uuid, audience text[] NOT NULL,
          learned_at timestamptz NOT NULL, mode text NOT NULL,
          PRIMARY KEY(campaign,holder,source,speaker,facet,value),
          FOREIGN KEY(campaign,holder) REFERENCES actors(campaign,key));
        ALTER TABLE character_knowledge ADD COLUMN IF NOT EXISTS speech_position int NOT NULL DEFAULT 0;
        CREATE INDEX IF NOT EXISTS knowledge_subject ON character_knowledge(campaign,holder,subject,facet,day DESC);
        """;

    internal const string Visible = """
        SELECT k.*,coalesce(person.name,c.player_name) AS subject_name,
          CASE WHEN k.speaker='player' THEN c.player_name ELSE witness.name END AS speaker_name
        FROM character_knowledge k JOIN campaigns c ON c.id=k.campaign
        LEFT JOIN actors person ON person.campaign=k.campaign AND person.key=k.subject
        LEFT JOIN actors witness ON witness.campaign=k.campaign AND witness.key=k.speaker
        WHERE k.campaign=@c AND k.holder=@a AND k.day<=@day
          AND (k.anchor IS NULL OR k.anchor IN (SELECT id FROM ancestry))
        """;

    internal static string Entries(bool moments) => """
        SELECT source::text||'/'||substr(md5(speaker||facet||value),1,12) AS id,day,learned_at AS at,
          jsonb_build_object('kind',CASE WHEN facet='major moment' THEN 'moment' ELSE 'knowledge' END,
          'subject',subject_name,'speakerLabel',speaker_name,'facet',facet,'value',value,'quote',quote,'question',question,
          'stance',stance,'mode',mode,'source',source,'earlier',superseded,'firsthand',speaker=subject) AS doc
        FROM (SELECT v.*,row_number() OVER(PARTITION BY subject,speaker,CASE WHEN facet IN ('interest','dislike') THEN 'interest' ELSE facet END,
          CASE WHEN facet IN ('favorite food','favorite color') THEN '' ELSE lower(value) END
          ORDER BY day DESC,learned_at DESC,speech_position DESC,source DESC)>1 AS superseded
          FROM (SELECT DISTINCT ON (subject,speaker,facet,lower(value),stance) * FROM (
        """ + Visible + ") sightings ORDER BY subject,speaker,facet,lower(value),stance,day DESC,learned_at DESC,speech_position DESC,source DESC) v) history WHERE facet " + (moments ? "=" : "<>") + "'major moment'";

    // Never replace an actor's saved identity with their display name. Names in reports must resolve uniquely.
    internal sealed record Line(Guid Source, Guid? Anchor, int Day, DateTimeOffset At, string Speaker, string Words, string[] Audience, string Mode,string To="");
    internal sealed record Detail(string Subject,string Facet,string Value,string Stance,string Quote,string Question="");

    static readonly Regex Favorite = new(@"^(?:(?<who>my|[\p{L}-]+['’]s)\s+favou?rite\s+(?<facet>food|dish|meal|colou?r)\s+(?<tense>is|was|used to be)\s+(?<value>.+?)|(?<value>.+?)\s+(?<tense>is|was|used to be)\s+(?<who>my|[\p{L}-]+['’]s)\s+favou?rite(?:\s+(?<facet>food|dish|meal|colou?r))?)[.!…]*$",RegexOptions.IgnoreCase);
    static readonly Regex DeniedFavorite=new(@"^(?<value>.+?) (?:is no longer|is not|isn't|isn’t) my favou?rite (?<facet>food|dish|meal|colou?r)(?: anymore)?[.!…]*$",RegexOptions.IgnoreCase);
    static readonly Regex Interest = new(@"^(?<who>I|[\p{L}-]+(?:\s+[\p{L}-]+){0,3})\s+(?<verb>used to (?:like|love|enjoy)|(?:do not|don't|don’t|no longer) (?:like|love|enjoy)|dislike[sd]?|hate[sd]?|like[sd]?|love[sd]?|enjoy(?:s|ed)?)\s+(?<value>[^.!?;]+)[.!…]*$",RegexOptions.IgnoreCase);
    static readonly Regex Uncertain = new(@"\b(?:if|maybe|perhaps|might|pretend|jok(?:e|ing)|suppose|imagine|unless)\b|[?“”""]",RegexOptions.IgnoreCase);
    static readonly Regex Major = new(@"\b(?:I forgive you|please forgive me|we made peace|we are engaged|we are married|we have reconciled|my (?:son|daughter|child) was born|my (?:mother|father|sister|brother|wife|husband|child) (?:died|passed away))\b",RegexOptions.IgnoreCase);
    static readonly Regex Color = new(@"^(?:red|green|blue|black|white|gold|golden|silver|yellow|purple|pink|orange|brown|grey|gray|teal|violet|indigo|crimson)$",RegexOptions.IgnoreCase);

    static string Clean(string value)=>value.Trim().TrimEnd('.','!','…').Trim();
    static string Facet(string value)=>value.Contains("colo",StringComparison.OrdinalIgnoreCase)?"favorite color":"favorite food";
    static string Named(string who)=>who.Equals("my",StringComparison.OrdinalIgnoreCase)||who.Equals("I",StringComparison.OrdinalIgnoreCase)?"self":Regex.Replace(who,@"['’]s$","",RegexOptions.IgnoreCase);
    static bool PlainValue(string value)=>value.Length is >0 and <=80&&!Regex.IsMatch(value,@"[,;:?!*]|\b(?:but|because|though|except|instead|actually|not|never|says?|said|told|would|could|that|your|their|his|her)\b",RegexOptions.IgnoreCase);

    internal static JsonArray Hints(JsonNode? supplied)
    {
        var hints=new JsonArray();
        if(supplied is not JsonArray items)return hints;
        foreach(var item in items.OfType<JsonObject>().Take(3)){
            string quote=item.S("quote"),value=item.S("value"),subject=item.S("subject"),facet=item.S("facet").ToLowerInvariant(),speaker=item.S("speaker");
            if(quote.Length is <2 or >260||value.Length is <1 or >80||subject.Length is <1 or >80||
                facet is not ("favorite food" or "favorite color" or "interest" or "food preference" or "dislike" or "background" or "major moment")||
                speaker is not ("player" or "npc" or "previous"))continue;
            hints.Add(new JsonObject{["speaker"]=speaker,["subject"]=subject,["facet"]=facet,["value"]=value,["quote"]=quote});
        }
        return hints;
    }

    static IEnumerable<string> SpokenSentences(string words)
    {
        int start=0;
        var speech=new List<string>();
        foreach(var action in ActionNotation.Find(words)){
            speech.Add(words[start..action.Open]);start=action.End+1;
        }
        speech.Add(words[start..]);
        return speech.SelectMany(span=>Regex.Split(span,@"(?<=[.!?…])\s+")).Select(s=>s.Trim()).Where(s=>s.Length>0);
    }

    static bool SpecificDetail(string value)=>PlainValue(value)&&!Regex.IsMatch(value,
        @"\b(?:you|them|him|her|us)\b|^(?:it|here|there|this|that|these|those|when|how|what|who|the way|to (?:be|think|say))\b",RegexOptions.IgnoreCase);

    static IEnumerable<Detail> Details(Line line,Line? prior,JsonArray hints)
    {
        string? lastQuestion=prior==null?null:SpokenSentences(prior.Words).LastOrDefault();
        foreach(string quote in SpokenSentences(line.Words)){
            if(quote.Length is <2 or >260||!DialogueOutput.IsWholeQuote(line.Words,quote)||Uncertain.IsMatch(quote))continue;
            var hint=hints.OfType<JsonObject>().FirstOrDefault(h=>h.S("quote")==quote);
            string claim=Regex.Replace(quote,@"^(?:actually|honestly|these days|well|in fact)[, ]+","",RegexOptions.IgnoreCase);
            var denied=DeniedFavorite.Match(claim);
            if(denied.Success&&PlainValue(denied.Groups["value"].Value)){
                yield return new("self",Facet(denied.Groups["facet"].Value),denied.Groups["value"].Value,"retracted",quote);
                continue;
            }
            var match=Favorite.Match(claim);
            if(match.Success){
                string value=Clean(match.Groups["value"].Value),who=Named(match.Groups["who"].Value),facet=match.Groups["facet"].Value;
                if(!PlainValue(value))continue;
                if(facet.Length==0){
                    if(Color.IsMatch(value)&&hint?.S("facet")=="favorite color")facet="color";
                    else if(hint?.S("facet")=="favorite food")facet="food";
                    else{yield return new(who,"favorite",value,match.Groups["tense"].Value.Equals("is",StringComparison.OrdinalIgnoreCase)?"current":"former",quote);continue;}
                }
                yield return new(who,Facet(facet),value,match.Groups["tense"].Value.Equals("is",StringComparison.OrdinalIgnoreCase)?"current":"former",quote);
                continue;
            }
            match=Interest.Match(claim);
            if(match.Success){
                var verb=match.Groups["verb"].Value.ToLowerInvariant();string value=Clean(match.Groups["value"].Value);
                if(!SpecificDetail(value))continue;
                bool dislikes=Regex.IsMatch(verb,@"^(?:don|do not|dislike|hate)");
                bool food=hint?.S("facet")=="food preference"||Regex.IsMatch(value,@"\b(?:tart|pie|bread|soup|stew|cake|cheese|apples?|berries|custard|porridge|baked fish)\b",RegexOptions.IgnoreCase);
                yield return new(Named(match.Groups["who"].Value),dislikes?"dislike":food?"food preference":"interest",value,
                    verb.StartsWith("used to")||verb.StartsWith("no longer")||verb=="loved"||verb=="liked"||verb=="enjoyed"?"former":"current",quote);
                continue;
            }
            // A short answer can only identify a preference for people who heard its question.
            if(prior!=null&&prior.Speaker!=line.Speaker&&(prior.To is "" or "group"||prior.To==line.Speaker)&&(line.Speaker=="player"||prior.Audience.Contains(line.Speaker))&&lastQuestion is {Length:<=240}&&lastQuestion.EndsWith('?')&&
                Regex.Match(lastQuestion,@"\byour favou?rite (food|dish|meal|colou?r)\b",RegexOptions.IgnoreCase) is {Success:true} question){
                string value=Clean(quote);
                if(PlainValue(value)&&value.Split(' ').Length<=4&&
                    (Facet(question.Groups[1].Value)=="favorite color"?Color.IsMatch(value):
                      hint?.S("facet")=="favorite food"||Regex.IsMatch(value,@"\b(?:tart|pie|bread|fish|soup|stew|cake|cheese|apples?|berries|custard|porridge|venison)\b",RegexOptions.IgnoreCase)))
                    yield return new("self",Facet(question.Groups[1].Value),value,"current",quote,lastQuestion);
            }
            if(Major.IsMatch(quote)){yield return new("self","major moment",quote,"reported",quote);continue;}
            if(hint==null||!quote.Contains(hint.S("value"),StringComparison.OrdinalIgnoreCase))continue;
            string subject=hint.S("subject"),category=hint.S("facet");
            if(subject!="self"&&!Regex.IsMatch(quote,@"(?<!\p{L})"+Regex.Escape(subject)+@"(?!\p{L})",RegexOptions.IgnoreCase))continue;
            if(category=="background"&&subject=="self"&&Regex.IsMatch(claim,@"^I (?:grew up|was born|was raised|trained|worked|lived)\b",RegexOptions.IgnoreCase))
                yield return new(subject,category,hint.S("value"),"reported",quote);
            else if(category is "favorite food" or "favorite color" or "interest" or "dislike" && SpecificDetail(hint.S("value")) &&
                Regex.IsMatch(quote,@"\b(?:favou?rite|fond of|partial to|enjoy|keen on|care for|love|dislike|can't stand|hates?|likes?)\b",RegexOptions.IgnoreCase)
                && !Regex.IsMatch(quote,@"\b(?:but|never|not|no longer|used to|brother|sister|mother|father|wife|husband)\b|n['’]t\b",RegexOptions.IgnoreCase)
                && (subject=="self"?Regex.IsMatch(claim,@"^(?:I(?:'m|’m| am) (?:fond of|partial to|keen on)|I (?:enjoy|love|like|dislike|prefer)|my favou?rite)\b",RegexOptions.IgnoreCase):
                    Regex.IsMatch(claim,@"^"+Regex.Escape(subject)+@"(?:['’]s|\s+(?:is|has|likes|loves|enjoys|dislikes|hates))\b",RegexOptions.IgnoreCase))
                && (!category.StartsWith("favorite")||Regex.IsMatch(quote,@"\bfavou?rite\b",RegexOptions.IgnoreCase)))
                yield return new(subject,category,hint.S("value"),"current",quote);
        }
    }

    internal static async Task Record(NpgsqlConnection conn,string campaign,Line line,Line? prior,JsonArray hints)
    {
        foreach(var fact in Details(line,prior,hints).Distinct().Take(3)){
            var audience=fact.Question.Length==0?line.Audience:line.Audience.Intersect(prior!.Audience).ToArray();
            if(audience.Length==0)continue;
            await using var cmd=Database.Command(conn,"""
                WITH candidates AS (
                    SELECT key FROM actors WHERE campaign=@c AND kind IN ('NPC','Child','Replacement') AND lower(name)=lower(@who)
                    UNION ALL SELECT 'player' FROM campaigns WHERE id=@c AND lower(player_name)=lower(@who)
                ), person AS (SELECT CASE WHEN @who='self' THEN @speaker
                    WHEN count(*)=1 THEN min(key) END AS subject FROM candidates)
                INSERT INTO character_knowledge(campaign,holder,subject,speaker,facet,value,stance,quote,question,day,source,anchor,audience,learned_at,mode,speech_position)
                SELECT @c,h.key,person.subject,@speaker,@facet,@value,@stance,@quote,@question,@day,@source,@anchor::uuid,@aud,@at,@mode,@position
                FROM actors h CROSS JOIN person WHERE h.campaign=@c AND h.key=ANY(@aud) AND person.subject IS NOT NULL
                ON CONFLICT DO NOTHING
                """,("c",campaign),("who",fact.Subject),("speaker",line.Speaker),("facet",fact.Facet),("value",fact.Value),
                ("stance",fact.Stance),("quote",fact.Quote),("question",fact.Question),("day",line.Day),("source",line.Source),
                ("anchor",line.Anchor),("position",line.Words.IndexOf(fact.Quote,StringComparison.Ordinal)),("aud",audience),("at",line.At.UtcDateTime),("mode",line.Mode));
            await cmd.ExecuteNonQueryAsync();
        }
    }

    internal static async Task Private(NpgsqlConnection conn,string c,string a,Guid id,JsonArray hints)
    {
        await using var cmd=Database.Command(conn,"""
            SELECT day,started,cue_anchor,player_text,coalesce(reply,''),cue_binding->>'conversation',cue_binding->>'head'
            FROM turns WHERE id=@id AND campaign=@c AND actor=@a AND state='complete' AND NOT dialogue_expired
            """,("id",id),("c",c),("a",a));
        Line player,npc;string? session;Guid? head;
        await using(var read=await cmd.ExecuteReaderAsync()){
            if(!await read.ReadAsync())return;
            player=new(id,read.IsDBNull(2)?null:read.GetGuid(2),read.GetInt32(0),read.GetDateTime(1),"player",read.GetString(3),new[]{a},"private");
            npc=player with {Speaker=a,Words=read.GetString(4)};
            session=read.IsDBNull(5)?null:read.GetString(5);head=!read.IsDBNull(6)&&Guid.TryParse(read.GetString(6),out var parsed)?parsed:null;
        }
        Line? prior=null;
        if(session!=null&&head!=null){
            await using var last=Database.Command(conn,CueHistory.Query(ConversationSessions.Lines+"""
                SELECT words,source FROM named WHERE session=@session AND mode='private' AND speaker=@a AND source<>@id AND at<@before
                ORDER BY at DESC,n DESC,source DESC LIMIT 1
                """),("explicit",true),("head",head),("c",c),("a",a),("day",player.Day),("session",session),("id",id),("before",player.At.UtcDateTime));
            await using var reader=await last.ExecuteReaderAsync();
            if(await reader.ReadAsync())prior=npc with {Words=reader.GetString(0),Source=reader.GetGuid(1)};
        }
        await Record(conn,c,player,prior,ForSpeaker(hints,"player"));
        await Record(conn,c,npc,player,ForSpeaker(hints,"npc"));
    }

    internal static Task<JsonNode?> Recall(Database db,string c,string a,int day,bool scoped,Guid? head,string question,string? topics,string[]? audience,string currentSpeaker)
    {
        var terms=Regex.Matches(question.ToLowerInvariant(),@"[\p{L}\p{N}]{3,}").Select(m=>m.Value)
            .Where(s=>s is not ("the" or "and" or "what" or "you" or "your" or "that" or "have" or "for" or "with" or "was" or "how" or "remember")).Distinct().Take(12);
        string Area(string words){
            if(Regex.IsMatch(words,@"\b(colou?rs?|hue|dye)\b",RegexOptions.IgnoreCase))return "favorite color";
            if(Regex.IsMatch(words,@"\b(foods?|meals?|lunch|supper|dinner|dish|dessert|bake[rd]?|bakery|treats?|eat|feast|cooking)\b",RegexOptions.IgnoreCase))return "favorite food";
            if(Regex.IsMatch(words,@"\b(leisure|hobbies|hobby|pastime|fish(?:ing)?|chess|free (?:time|afternoon)|quiet afternoon|by the river)\b",RegexOptions.IgnoreCase))return "interest";
            return "";
        }
        // A speaker may explicitly invite this circle to hear their own private detail.
        bool share=audience!=null&&Regex.IsMatch(question,
            @"^(?:please |can you |could you |you (?:may|can) )?(?:tell|remind) (?:everyone|the group|them both|both of them) (?:about |what )?my favou?rite (?:food|dish|meal|colou?r)(?: is)?[.?!]?$",RegexOptions.IgnoreCase);
        string focus=Regex.IsMatch(question,@"\b(my|mine|I like|I enjoy)\b",RegexOptions.IgnoreCase)?currentSpeaker:
            Regex.IsMatch(question,@"\byour\b",RegexOptions.IgnoreCase)?a:"";
        return db.ScopedOne("WITH visible AS ("+Visible+"""
            ), current_beliefs AS (
                SELECT DISTINCT ON (subject,speaker,
                   CASE WHEN facet IN ('interest','dislike') THEN 'interest' ELSE facet END,
                   CASE WHEN facet IN ('favorite food','favorite color') THEN '' ELSE lower(value) END) *
                FROM visible
                ORDER BY subject,speaker,CASE WHEN facet IN ('interest','dislike') THEN 'interest' ELSE facet END,
                   CASE WHEN facet IN ('favorite food','favorite color') THEN '' ELSE lower(value) END,
                   day DESC,learned_at DESC,speech_position DESC,source DESC
            ), ranked AS (
                SELECT b.*,to_tsvector('english',value||' '||facet||' '||subject_name) @@ to_tsquery('english',@terms) AS exact,
                   (facet=@area OR @area='favorite food' AND facet='food preference') AND @area<>'' AS subject_match FROM current_beliefs b WHERE @aud::text[] IS NULL OR (mode='group' AND audience @> @aud::text[]) OR (@share AND subject=@who AND speaker=subject AND facet=@area)
            ), picked AS (
                SELECT * FROM ranked k WHERE (exact OR subject_match OR facet=@cue AND stance='current')
                    AND (exact OR subject_match OR NOT EXISTS (
                        SELECT FROM (SELECT reply FROM turns WHERE actor=@a AND state='complete'
                            ORDER BY started DESC LIMIT 5) r WHERE strpos(lower(r.reply),lower(k.value))>0)
                        AND NOT EXISTS (SELECT FROM (SELECT e.payload->'line'->>'words' AS words FROM cue_events e
                            JOIN ancestry h ON h.id=e.id WHERE e.campaign=@c AND e.payload->>'kind'='group'
                              AND e.payload->'line'->>'speaker'=@a ORDER BY (e.payload->'line'->>'at') DESC LIMIT 5) g
                            WHERE strpos(lower(g.words),lower(k.value))>0))
                ORDER BY subject_match DESC,(subject=@focus) DESC,(facet=@area) DESC,exact DESC,(speaker=subject) DESC,(stance='current') DESC,day DESC,learned_at DESC,source DESC LIMIT 2
            )
            SELECT coalesce(jsonb_agg(jsonb_strip_nulls(jsonb_build_object('about',subject_name,'saidBy',speaker_name,
                'how',CASE WHEN subject=speaker THEN 'self disclosure' ELSE 'reported by speaker' END,'detail',facet,
                'value',value,'stance',stance,'day',day,'quote',quote,'question',nullif(question,'')))),'[]')::text FROM picked
            """,scoped,head,("c",c),("a",a),("day",day),("terms",string.Join(" | ",terms)),
            ("area",Area(question)),("cue",Area(topics??"")),("aud",audience),("share",share),("who",currentSpeaker),("focus",focus));
    }

    internal static JsonArray ForSpeaker(JsonArray hints,string speaker)=>new(hints.Where(h=>h.S("speaker")==speaker).Select(h=>h!.DeepClone()).ToArray());
    internal static Line FromGroup(Guid id,int day,JsonNode line)=>new(id,id,day,DateTimeOffset.Parse(line.S("at")),line.S("speaker"),line.S("words"),
        line["audience"]!.AsArray().Select(n=>n!.GetValue<string>()).ToArray(),"group",line.S("to"));

    internal static async Task Group(NpgsqlConnection conn,string c,Guid id,Guid? parent,int day,JsonNode line)
    {
        var heard=FromGroup(id,day,line);
        var previous=new List<Line>();
        if(parent!=null&&line.I("seq")>1){
            await using var cmd=Database.Command(conn,CueHistory.Query("""
                SELECT e.id,(payload->>'day')::int,(payload->'line')::text FROM cue_events e JOIN ancestry h ON e.id=h.id
                WHERE e.campaign=@c AND payload->>'kind'='group' AND payload->'line'->>'session'=@session
                  AND payload->'line' ? 'words' AND (@speaker='player' OR payload->'line'->'audience' ? @speaker) ORDER BY (payload->'line'->>'seq')::int DESC LIMIT 2
                """),("c",c),("a",heard.Audience[0]),("speaker",heard.Speaker),("session",line.S("session")),("explicit",true),("head",parent));
            await using var reader=await cmd.ExecuteReaderAsync();
            while(await reader.ReadAsync())previous.Add(FromGroup(reader.GetGuid(0),reader.GetInt32(1),JsonNode.Parse(reader.GetString(2))!));
        }
        var hints=Hints(line["learned"]);
        await Record(conn,c,heard,previous.FirstOrDefault(),ForSpeaker(hints,"npc"));
        if(previous.Count>0&&hints.Any(h=>h.S("speaker")=="previous"))
            await Record(conn,c,previous[0],previous.ElementAtOrDefault(1),ForSpeaker(hints,"previous"));
    }
}
