public sealed class MemoryCompactor(Database db, Providers providers, ILogger<MemoryCompactor> log) : BackgroundService
{
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
            WHERE t.state='complete' AND t.cue_anchor IS NULL AND t.day<=e.cutoff
          UNION ALL SELECT d.campaign,d.actor,d.day,d.kind,d.id,d.observed,left(d.description,190)
            FROM eligible e JOIN daily_events d ON (e.campaign,e.key)=(d.campaign,d.actor) WHERE d.cue_anchor IS NULL AND d.day<=e.cutoff
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

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while(!stoppingToken.IsCancellationRequested){
            try { await Compact(null,null,null,(await providers.Options()).DailyDays); }
            catch(Exception e) when(!stoppingToken.IsCancellationRequested){log.LogWarning(e,"Could not compact character memories.");}
            await Task.Delay(TimeSpan.FromMinutes(2),stoppingToken);
        }
    }
}
