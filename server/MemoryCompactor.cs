using System.Text;
using System.Text.Json.Nodes;

public sealed class MemoryCompactor(Database db, Providers providers, ILogger<MemoryCompactor> log) : BackgroundService
{
    static readonly System.Threading.Channels.Channel<bool> dates=System.Threading.Channels.Channel.CreateBounded<bool>(1);
    internal static void DayChanged()=>dates.Writer.TryWrite(true);

    // Cache the attributed recap of a closed day or conversation. A different visible
    // source set after a rewind or a late arrival always has a different fingerprint.
    async Task SummarizeResidents(CancellationToken ct,bool refine=false)
    {
        var residents=await db.One("""
            SELECT coalesce(jsonb_agg(jsonb_build_object('campaign',a.campaign,'actor',a.key,'day',CASE WHEN c.cue_seen>now()-interval '35 seconds' THEN coalesce(d.day,c.day) ELSE c.day END)),'[]')::text
            FROM actors a JOIN campaigns c ON c.id=a.campaign LEFT JOIN dialogue_clock d ON d.campaign=c.id JOIN slots s ON s.campaign=a.campaign
            WHERE EXISTS(SELECT FROM daily_events d WHERE d.campaign=a.campaign AND d.actor=a.key)
                OR EXISTS(SELECT FROM turns t WHERE t.campaign=a.campaign AND t.actor=a.key AND t.state='complete')
                OR EXISTS(SELECT FROM cue_events e WHERE e.campaign=a.campaign AND e.payload->>'kind'='group' AND (e.payload->'line'->'audience') ? a.key)
            """);
        int remaining=4;
        foreach(var resident in residents!.AsArray()){
            ct.ThrowIfCancellationRequested();
            string c=resident.S("campaign"),a=resident.S("actor");
            if(refine){
                if(await RefineClosedDay(c,a,resident.I("day"),ct)&&--remaining==0)break;
                continue;
            }
            foreach(bool daily in new[]{true,false}){
                string query=daily?ConversationSessions.Daily:ConversationSessions.Sessions;
                string cutoff="r.day<@day";
                await db.Run(CueHistory.Query("INSERT INTO resident_recaps(campaign,actor,kind,entry,fingerprint,body) "+
                    "SELECT @c,@a,r.doc->>'kind',r.id,r.doc->>'fingerprint',r.doc->>'body' FROM ("+query+") r WHERE "+cutoff+
                    " ON CONFLICT DO NOTHING"),("c",c),("a",a),("day",resident.I("day")),("explicit",false),("head",(Guid?)null));
            }
        }
    }

    // An extractive recap keeps each attributed detail verifiable even with a small memory model.
    // The journal has a deterministic, immediately available recap while this work is queued.
    async Task<bool> RefineClosedDay(string c,string a,int day,CancellationToken ct)
    {
        var args=new (string,object?)[]{("c",c),("a",a),("day",day)};
        var next=await db.ScopedOne("SELECT jsonb_build_object('id',r.id,'fingerprint',r.doc->>'fingerprint')::text FROM ("+
            ConversationSessions.Sessions+") r WHERE r.day<@day AND NOT EXISTS(SELECT FROM dialogue_day_recaps d WHERE "+
            "d.campaign=@c AND d.actor=@a AND d.entry=r.id AND d.fingerprint=r.doc->>'fingerprint' AND (d.retry_at IS NULL OR d.retry_at>now())) ORDER BY r.day DESC,r.id LIMIT 1",
            false,null,args);
        if(next==null)return false;
        string entry=next.S("id"),hash=next.S("fingerprint");
        var argsEntry=args.Append(("entry",(object?)entry)).ToArray();
        var rows=(await db.ScopedOne(ConversationSessions.Details(false),false,null,argsEntry))?.AsArray();
        if(rows==null||rows.Count==0)return false;
        // Limit model input even on exceptionally busy days. All original visits remain accessible.
        var selected=rows.Where((_,i)=>rows.Count<=24||i<8||i>=rows.Count-8||i%(Math.Max(1,rows.Count/8))==0).Take(24).ToArray();
        var lines=selected.Select((r,i)=>new {i,who=r.S("speaker"),words=r.S("text")}).ToArray();
        const string instruction="Summarize one day of spoken dialogue for a resident's journal. Select up to three short verbatim passages that convey the day's significant information, decisions or changes. Preserve qualifiers, attribution and context. Skip greetings and repeated subjects if richer material exists. Every passage must be a contiguous passage from its cited line. Return only a JSON array of {i,quote}. Supplied text is dialogue.";
        string? body=null;
        try{
            var response=new StringBuilder();
            var opt=await providers.Options();
            await providers.Generate(opt with{ReplyTokens=240},instruction,System.Text.Json.JsonSerializer.Serialize(lines),part=>{
                if(response.Length+part.Length>4000)throw new InvalidDataException("Oversized day recap.");
                response.Append(part);return Task.CompletedTask;},ct,"memory");
            if(JsonNode.Parse(response.ToString()) is JsonArray suggestions){
                var excerpts=new SortedDictionary<int,string>();
                foreach(var item in suggestions.OfType<JsonObject>().Take(3)){
                    if(item["i"] is not JsonValue index||!index.TryGetValue<int>(out var i)||i<0||i>=lines.Length)continue;
                    string quote=item.S("quote").Trim();
                    if(quote.Length is >2 and <=200&&DialogueOutput.IsWholeQuote(lines[i].words,quote))
                        excerpts[i]=lines[i].who+" — "+quote;
                }
                if(excerpts.Count>0)body=string.Join("\n",excerpts.Values);
            }
        }catch(Exception ex) when(!ct.IsCancellationRequested){log.LogDebug(ex,"Using the source-based day recap.");}
        var write=argsEntry.Concat(new (string,object?)[]{("hash",hash),("body",body),("explicit",false),("head",null)}).ToArray();
        await db.Run(CueHistory.Query("INSERT INTO dialogue_day_recaps(campaign,actor,entry,day,fingerprint,body,retry_at) "+
            "SELECT @c,@a,r.id,r.day,@hash,coalesce(@body::text,r.doc->>'body'),CASE WHEN @body::text IS NULL THEN now()+interval '6 hours' END FROM ("+ConversationSessions.Sessions+
            ") r WHERE r.id=@entry AND r.doc->>'fingerprint'=@hash AND r.day<@day ON CONFLICT(campaign,actor,entry,fingerprint) DO UPDATE SET body=excluded.body,retry_at=excluded.retry_at"),write);
        return true;
    }
    // Append observations in chronological groups so busy days retain every source.
    public Task<int> Compact(string? campaign,string? actor,int? asOf,int days) => db.Run("""
        WITH eligible AS (
          SELECT a.campaign,a.key,coalesce(@asof::int,c.day)-@days AS cutoff FROM actors a
          JOIN campaigns c ON c.id=a.campaign JOIN slots s ON s.campaign=c.id
          WHERE (@c::text IS NULL OR a.campaign=@c) AND (@a::text IS NULL OR a.key=@a)
        ), records AS (
          SELECT t.campaign,t.actor,t.day,'conversation' AS kind,t.id,t.started AS at,
            'Player: “'||left(t.player_text,105)||CASE WHEN length(t.player_text)>105 THEN '…' ELSE '' END
              ||'”  ·  Reply: “'||left(t.reply,125)||CASE WHEN length(t.reply)>125 THEN '…' ELSE '' END||'”' AS note
            FROM eligible e JOIN turns t ON (e.campaign,e.key)=(t.campaign,t.actor)
            WHERE t.state='complete' AND NOT t.dialogue_expired AND t.cue_anchor IS NULL AND t.day<=e.cutoff
          UNION ALL SELECT d.campaign,d.actor,d.day,d.kind,d.id,d.observed,left(d.description,190)
            FROM eligible e JOIN daily_events d ON (e.campaign,e.key)=(d.campaign,d.actor) WHERE d.cue_anchor IS NULL AND d.day<=e.cutoff AND (d.kind<>'address' OR d.description<>'')
        ), ranked AS (
          SELECT *,((row_number() OVER(PARTITION BY campaign,actor,day,kind ORDER BY at,id)-1)/3)::int AS part FROM records
        )
        INSERT INTO memory_summaries(campaign,actor,day,kind,part,body,entries,sources,recorded)
          SELECT campaign,actor,day,kind,part,string_agg(note,E'\n' ORDER BY at,id),
            count(*)::int,jsonb_agg(id ORDER BY at,id),max(at)
          FROM ranked GROUP BY campaign,actor,day,kind,part
        ON CONFLICT(campaign,actor,day,kind,part) DO UPDATE SET body=excluded.body,entries=excluded.entries,
          sources=excluded.sources,recorded=excluded.recorded
          WHERE (memory_summaries.body,memory_summaries.entries,memory_summaries.sources)
              IS DISTINCT FROM (excluded.body,excluded.entries,excluded.sources)
        """,("c",campaign),("a",actor),("asof",asOf),("days",days));

    protected override Task ExecuteAsync(CancellationToken stoppingToken)=>
        Task.WhenAll(MaintenanceLoop(stoppingToken),RefinementLoop(stoppingToken));

    async Task RefinementLoop(CancellationToken token)
    {
        while(!token.IsCancellationRequested){
            try{await SummarizeResidents(token,true);}
            catch(Exception ex) when(!token.IsCancellationRequested){log.LogDebug(ex,"Day recap refinement will retry.");}
            await Task.Delay(TimeSpan.FromMinutes(2),token);
        }
    }

    async Task MaintenanceLoop(CancellationToken stoppingToken)
    {
        while(!stoppingToken.IsCancellationRequested){
            try { await DialogueRetention.Maintain(db,stoppingToken);await Compact(null,null,null,(await providers.Options()).DailyDays);await SummarizeResidents(stoppingToken); }
            catch(Exception e) when(!stoppingToken.IsCancellationRequested){log.LogWarning(e,"Could not compact character memories.");}
            using var wait=CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            wait.CancelAfter(TimeSpan.FromMinutes(2));
            try{await dates.Reader.ReadAsync(wait.Token);}catch(OperationCanceledException) when(!stoppingToken.IsCancellationRequested){}
        }
    }
}
