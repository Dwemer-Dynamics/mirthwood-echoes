using System.Threading.Channels;

public sealed class MemoryCompactor(Database db, Providers providers, ILogger<MemoryCompactor> log) : BackgroundService
{
    readonly SemaphoreSlim rollover=new(1,1);

    readonly Channel<IReadOnlyList<DialogueRecaps.Source>> refinements=Channel.CreateBounded<IReadOnlyList<DialogueRecaps.Source>>(32);

    // Cache the attributed recap of a closed day or conversation. A different visible
    // source set after a rewind or a late arrival always has a different fingerprint.
    async Task SummarizeResidents(CancellationToken ct)
    {
        var residents=await db.One("""
            SELECT coalesce(jsonb_agg(jsonb_build_object('campaign',a.campaign,'actor',a.key,'day',CASE WHEN c.cue_seen>now()-interval '35 seconds' THEN coalesce(d.day,c.day) ELSE c.day END)),'[]')::text
            FROM actors a JOIN campaigns c ON c.id=a.campaign LEFT JOIN dialogue_clock d ON d.campaign=c.id JOIN slots s ON s.campaign=a.campaign
            WHERE EXISTS(SELECT FROM daily_events d WHERE d.campaign=a.campaign AND d.actor=a.key)
                OR EXISTS(SELECT FROM turns t WHERE t.campaign=a.campaign AND t.actor=a.key AND t.state='complete')
                OR EXISTS(SELECT FROM cue_events e WHERE e.campaign=a.campaign AND e.payload->>'kind'='group' AND (e.payload->'line'->'audience') ? a.key)
            """);
        foreach(var resident in residents!.AsArray()){
            ct.ThrowIfCancellationRequested();
            string c=resident.S("campaign"),a=resident.S("actor");
            foreach(bool daily in new[]{true,false}){
                string query=daily?ConversationSessions.Daily:ConversationSessions.Sessions;
                string cutoff="r.day<@day";
                await db.Run(CueHistory.Query("INSERT INTO resident_recaps(campaign,actor,kind,entry,fingerprint,body) "+
                    "SELECT @c,@a,r.doc->>'kind',r.id,r.doc->>'fingerprint',r.doc->>'body' FROM ("+query+") r WHERE "+cutoff+
                    " ON CONFLICT DO NOTHING"),("c",c),("a",a),("day",resident.I("day")),("explicit",false),("head",(Guid?)null));
            }
        }
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
        await foreach(var batch in refinements.Reader.ReadAllAsync(token)){
            using var deadline=CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(35));
            try{await DialogueRecaps.Refine(db,providers,batch,deadline.Token);}
            catch(Exception ex) when(!token.IsCancellationRequested){log.LogDebug(ex,"Kept the source-based daily recap.");}
        }
    }

    internal async Task CloseDays(CancellationToken ct)
    {
        await rollover.WaitAsync(ct);
        try{
            var closed=await DialogueRetention.Maintain(db,ct);
            foreach(var day in closed.GroupBy(r=>(r.Campaign,r.Day,r.Age,r.Mode,r.Branch,Visit:r.Mode=="group"?r.Session:"",
                Listeners:string.Join("/",r.Audience.Order())))){
                foreach(var batch in day.Chunk(24))refinements.Writer.TryWrite(batch);
            }
        }finally{rollover.Release();}
    }

    async Task MaintenanceLoop(CancellationToken stoppingToken)
    {
        while(!stoppingToken.IsCancellationRequested){
            try{
                await CloseDays(stoppingToken);
                await Compact(null,null,null,(await providers.Options()).DailyDays);
                await SummarizeResidents(stoppingToken);
            }
            catch(Exception e) when(!stoppingToken.IsCancellationRequested){log.LogWarning(e,"Could not compact character memories.");}
            await Task.Delay(TimeSpan.FromMinutes(2),stoppingToken);
        }
    }
}
