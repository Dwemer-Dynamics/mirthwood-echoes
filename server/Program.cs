using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.DataProtection;
using Npgsql;

bool prepareOnly=args is ["--prepare-install"];
var builder=WebApplication.CreateBuilder(new WebApplicationOptions{Args=prepareOnly?[]:args,ContentRootPath=AppContext.BaseDirectory});
var paths=new RuntimePaths(Environment.GetEnvironmentVariable("MIRTHWOOD_ECHOES_STATE")??"/var/lib/mirthwood-echoes",
    Environment.GetEnvironmentVariable("MIRTHWOOD_ECHOES_GAME")??"/mnt/c/SteamLibrary/steamapps/common/Mirthwood",
    Environment.GetEnvironmentVariable("MIRTHWOOD_ECHOES_SAVES")??throw new InvalidOperationException("Configure MIRTHWOOD_ECHOES_SAVES."));
Directory.CreateDirectory(paths.State);
int port=int.Parse(Environment.GetEnvironmentVariable("MIRTHWOOD_ECHOES_PORT")??"17841");
builder.WebHost.ConfigureKestrel(s=>{s.Listen(IPAddress.Loopback,port);s.Limits.MaxRequestBodySize=32*1024;});
builder.Services.AddSingleton(paths);
builder.Services.Configure<HostOptions>(o=>o.ShutdownTimeout=TimeSpan.FromSeconds(85));
builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics",LogLevel.Warning);
builder.Logging.AddFilter("Microsoft.AspNetCore.Routing.EndpointMiddleware",LogLevel.Warning);
builder.Services.AddSingleton(NpgsqlDataSource.Create(Environment.GetEnvironmentVariable("MIRTHWOOD_ECHOES_DB")??"Host=/var/run/postgresql;Database=mirthwood_echoes;Username=mirthwood_echoes;Maximum Pool Size=12"));
builder.Services.AddDataProtection().SetApplicationName(ModIdentity.Id).PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(paths.State,"keys")));
builder.Services.AddSingleton(new HttpClient(new SocketsHttpHandler{AllowAutoRedirect=false,PooledConnectionLifetime=TimeSpan.FromMinutes(5)}){Timeout=Timeout.InfiniteTimeSpan});
builder.Services.AddSingleton<Database>();builder.Services.AddSingleton<Providers>();builder.Services.AddSingleton<Conversation>();
builder.Services.AddSingleton<NativeProfile>();builder.Services.AddSingleton<CharacterJournal>();
builder.Services.AddSingleton<GameProbes>();
builder.Services.AddSingleton<VoiceCast>();
builder.Services.AddSingleton<PocketSpeech>();
builder.Services.AddSingleton<MemoryCompactor>();builder.Services.AddHostedService(s=>s.GetRequiredService<MemoryCompactor>());
builder.Services.AddSingleton<Saves>();builder.Services.AddHostedService(s=>s.GetRequiredService<Saves>());
builder.Services.AddSingleton<ModuleInstaller>();builder.Services.AddHostedService(s=>s.GetRequiredService<ModuleInstaller>());
var app=builder.Build();
await app.Services.GetRequiredService<Database>().Initialize();
await IdentityUpgrade.ReprotectKeys(paths, app.Services.GetRequiredService<Database>(), app.Services.GetRequiredService<IDataProtectionProvider>());
if(prepareOnly){
    var installation=await app.Services.GetRequiredService<ModuleInstaller>().Check(CancellationToken.None);
    Console.WriteLine(installation.Detail);
    Environment.ExitCode=installation.Status is "ready" or "waiting_for_game_exit" or "conflict"?0:1;
    await app.DisposeAsync();
    return;
}
app.Use(async (ctx,next)=>{
    try
    {
        if(ctx.Request.Host.Host is not ("localhost" or "127.0.0.1" or "[::1]"))throw new UserError("Local access only.",403);
        if(ctx.Request.Headers.TryGetValue("Origin",out var origin) && origin.ToString()!="http://"+ctx.Request.Host.Value)
            throw new UserError("Cross-origin request rejected.",403);
        if(ctx.Request.Method is "POST" or "PUT" && !ctx.Request.HasJsonContentType())throw new UserError("JSON content type required.",415);
        ctx.Response.Headers.XContentTypeOptions="nosniff";
        ctx.Response.Headers["Content-Security-Policy"]="default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; connect-src 'self'; object-src 'none'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'";
        if(ctx.Request.Path.StartsWithSegments("/api"))ctx.Response.Headers.CacheControl="no-store";
        await next();
    }
    catch(Exception ex) when(!ctx.Response.HasStarted && !ctx.RequestAborted.IsCancellationRequested)
    {
        int code=ex switch{UserError e=>e.Status,JsonException=>400,BadHttpRequestException=>400,OperationCanceledException=>504,HttpRequestException=>502,_=>500};
        if(code==500)app.Logger.LogError(ex,"Request failed at {Path}",ctx.Request.Path);
        ctx.Response.StatusCode=code;
        await ctx.Response.WriteAsJsonAsync(new{error=ex is UserError?ex.Message:code==504?"The provider timed out.":code==502?"The provider connection failed. Check its endpoint and availability.":code==400?"Invalid request.":"The server could not finish this request."});
    }
});
app.UseDefaultFiles();app.UseStaticFiles();
if(Guid.TryParseExact(Environment.GetEnvironmentVariable("MIRTHWOOD_ECHOES_HARNESS_ID"),"N",out var testRun)
    && app.Services.GetRequiredService<NpgsqlDataSource>().ConnectionString.Contains("Database=echoes_talk_"+testRun.ToString("N"),StringComparison.OrdinalIgnoreCase))
    app.MapGet("/api/test-session",()=>new{run=testRun.ToString("N")});
app.MapGet("/api/health",(ModuleInstaller installer)=>new{
    application=ModIdentity.Id, displayName=ModIdentity.Name,
    version=ModIdentity.Version, module=installer.Status
});
app.MapPost("/api/module/check",(ModuleInstaller installer,CancellationToken ct)=>installer.Check(ct));
app.MapGet("/api/options",(Providers p)=>p.Options());
app.MapPut("/api/options",async(JsonObject input,Providers p)=>{await p.SaveOptions(input);return Results.NoContent();});
app.MapGet("/api/providers",()=>Providers.Catalog);
app.MapGet("/api/voices",async(VoiceCast voices)=>new{cast=voices.Roster(),settings=await voices.Settings(),automatic=await voices.AutomaticPlayers()});
app.MapPut("/api/voices/enabled",async(JsonObject input,VoiceCast voices)=>{await voices.Change(input,true);return Results.NoContent();});
app.MapPut("/api/voices/player",async(JsonObject input,VoiceCast voices)=>{await voices.Change(input,false);return Results.NoContent();});
app.MapGet("/api/voices/{id}/sample",(string id,VoiceCast voices)=>Results.File(voices.Sample(id),"audio/wav",enableRangeProcessing:true));
app.MapGet("/api/voices/status",async(PocketSpeech speech,CancellationToken ct)=>new{connected=await speech.Available(ct)});
app.MapPost("/api/voices/speech",(JsonObject input,PocketSpeech speech,CancellationToken ct)=>speech.Speak(input,ct));
app.MapGet("/api/providers/{id}",(string id,Providers p)=>p.Public(id));
app.MapPut("/api/providers/{id}",async(string id,JsonObject input,Providers p)=>{await p.Save(id,input);return Results.NoContent();});
app.MapGet("/api/saves",(Database db)=>db.One("""
    SELECT coalesce(jsonb_agg(jsonb_build_object('id',c.id,'slot',s.slot,'player',c.player_name,'day',c.day) ORDER BY s.slot),'[]')::text
    FROM slots s JOIN campaigns c ON c.id=s.campaign
    """));
app.MapGet("/api/saves/{c}/characters",(string c,Database db)=>db.One("""
    SELECT coalesce(jsonb_agg(jsonb_build_object('key',key,'name',name,'kind',kind,'job',job,'town',town,'nativeId',native_id,'owner',owner)
      ORDER BY lower(name),owner),'[]')::text FROM actors WHERE campaign=@c AND present
    """,("c",c)));
app.MapGet("/api/saves/{c}/characters/{a}",async(string c,string a,Database db)=>
    await db.One("SELECT raw::text FROM actors WHERE campaign=@c AND key=@a AND present",("c",c),("a",a))??throw new UserError("Character not found.",404));
app.MapPost("/api/saves/refresh",async(Saves saves)=>{await saves.Scan();return new{detail=saves.Health};});
app.MapPost("/api/module/heartbeat",async(JsonObject input,Conversation dialogue,GameProbes probes)=>{await dialogue.Heartbeat(input);return new{probe=probes.Poll(input)};});
app.MapPost("/api/diagnostics/probes",(JsonObject input,GameProbes probes)=>probes.Start(input));
app.MapGet("/api/diagnostics/probes/{id:guid}",(Guid id,GameProbes probes)=>probes.Get(id));
app.MapPost("/api/module/probes/{id:guid}/stream",(Guid id,string session,JsonObject turn,HttpContext ctx,GameProbes probes)=>probes.Fixture(id,session,turn,ctx));
app.MapPost("/api/module/probes/{id:guid}/talk",(Guid id,string session,JsonObject turn,GameProbes probes,CancellationToken ct)=>probes.LiveTalk(id,session,turn,ct));
app.MapPost("/api/module/probes/{id:guid}",async(Guid id,JsonObject input,GameProbes probes,Conversation dialogue)=>{await probes.Complete(id,input,dialogue);return Results.NoContent();});
app.MapPost("/api/module/resolve",(JsonObject input,Conversation dialogue)=>dialogue.Resolve(input));
app.MapPost("/api/module/event",async(JsonObject input,Conversation dialogue)=>{await dialogue.Observe(input);return Results.Ok();});
app.MapPost("/api/module/cue",async(JsonObject input,Conversation dialogue)=>{await dialogue.ObserveCue(input);return Results.Ok();});
app.MapPost("/api/dialogue",(JsonObject input,Conversation dialogue,HttpContext ctx)=>dialogue.Speak(input,ctx));
app.MapPost("/api/dialogue/prepare",(JsonObject input,Conversation dialogue)=>dialogue.PrepareTalk(input));
app.MapPost("/api/dialogue/address",(JsonObject input,Conversation dialogue,CancellationToken ct)=>dialogue.AddressPlayer(input,ct));
app.MapPost("/api/dialogue/finish",(JsonObject input,Conversation dialogue)=>dialogue.FinishTalk(input));
app.MapPost("/api/quests/turn",(JsonObject input,Conversation dialogue)=>dialogue.QuestTurn(input));
app.MapPost("/api/quests/finish",(JsonObject input,Conversation dialogue)=>dialogue.FinishQuest(input));
var character=app.MapGroup("/api/saves/{c}/characters/{a}");
character.MapGet("/profile",(string c,string a,CharacterJournal journal)=>journal.Profile(c,a));
character.MapGet("/history/{view}",(string c,string a,string view,string? before,CharacterJournal journal,Conversation clock)=>journal.Entries(c,a,view,before,clock.LiveDay(c)));
character.MapGet("/sources/{type}/{id:guid}",(string c,string a,string type,Guid id,CharacterJournal journal,Conversation clock)=>journal.Source(c,a,type,id,clock.LiveDay(c)));
character.MapGet("/personality",async(string c,string a,CharacterJournal journal,Conversation clock,NativeProfile native)=>{
    var actor=await journal.Actor(c,a);return await journal.Growth(c,a,clock.LiveDay(c)??actor.I("day"),native.Describe(actor));});
character.MapPost("/personality",async(string c,string a,JsonObject input,CharacterJournal journal,Conversation clock)=>{
    await journal.AddTurningPoint(c,a,input,clock.LiveDay(c));return Results.NoContent();});
character.MapPost("/backstory",(string c,string a,CharacterJournal journal,CancellationToken ct)=>journal.GenerateStory(c,a,ct));
app.MapGet("/api/saves/{c}/characters/{a}/commitments",(string c,string a,Conversation dialogue)=>dialogue.Commitments(c,a));
app.MapPost("/api/commitments/{id:guid}/fulfill",async(Guid id,JsonObject input,Conversation dialogue)=>{await dialogue.Fulfill(id,input);return Results.NoContent();});
app.MapGet("/api/diagnostics",async(Database db,Saves saves,ModuleInstaller module,Conversation dialogue)=>new{
    database="PostgreSQL",saveScan=saves.Health,installation=module.Status,game=dialogue.LastBeat,
    counts=await db.One("""
      SELECT jsonb_build_object('characters',(SELECT count(*) FROM actors JOIN slots ON slots.campaign=actors.campaign WHERE present),
       'observations',(SELECT count(*) FROM daily_events),'commitments',(SELECT count(*) FROM commitments WHERE status='open'),
       'replies',(SELECT count(*) FROM turns WHERE state='complete'))::text
      """),
    lastReply=await db.One("SELECT jsonb_build_object('firstMs',first_ms,'totalMs',total_ms,'state',state)::text FROM turns ORDER BY started DESC LIMIT 1")
});
app.Run();
