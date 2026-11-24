using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.DataProtection;

public sealed record Provider(string Id,string Name,string Url);
public sealed record Profile(string DefaultModel="",string Conversation="",string Memory="",string Endpoint="",string Secret="",string CodexHost="service",string ConversationEffort="");
public sealed record Options(string Provider="nanogpt",int ReplyTokens=96,int DailyDays=2)
{
    public int PositiveDesireCostPercent {get;init;}=25;
}

public sealed class Providers(Database db,IDataProtectionProvider protection,HttpClient http)
{
    // IDs and endpoint conventions from ReignServer / Modules/Platform/LlmProviderProfiles.cs.
    public static readonly Provider[] Catalog=[
        new("nanogpt","NanoGPT","https://nano-gpt.com/api/v1/chat/completions"),
        new("openrouter","OpenRouter","https://openrouter.ai/api/v1/chat/completions"),
        new("ollama_cloud","Ollama Cloud","https://ollama.com/v1/chat/completions"),
        new("openai_compatible","OpenAI-compatible",""),
        new("codex_subscription","Codex subscription","")];
    public const string KeyPurpose = "Mirthwood.Echoes.ProviderKeys.v1";
    readonly IDataProtector vault=protection.CreateProtector(KeyPurpose);
    readonly SemaphoreSlim writes=new(1);
    readonly ProviderQueue queue=new();
    public static Provider Find(string id)=>Catalog.FirstOrDefault(p=>p.Id==id)??throw new UserError("Unknown provider.");
    public async Task<Options> Options()=> (await db.Config("options"))?.Deserialize<Options>(Database.Json)??new();
    async Task<Profile> Get(string id)=> (await db.Config("profile/"+Find(id).Id))?.Deserialize<Profile>(Database.Json)??new();
    public async Task<object> Public(string id)
    {
        var p=await Get(id);
        return new { p.DefaultModel,p.Conversation,p.Memory,p.ConversationEffort,endpoint=id=="openai_compatible"?p.Endpoint:Find(id).Url,hasKey=p.Secret!="",codexHost=id=="codex_subscription"?p.CodexHost:null };
    }
    public async Task SaveOptions(JsonNode? n)
    {
        var opt=new Options(Find(n.Required("provider")).Id,n.I("replyTokens"),n.I("dailyDays"));
        if(opt.ReplyTokens is < 32 or > 256 || opt.DailyDays is < 1 or > 7) throw new UserError("Reply tokens must be 32–256 and daily recall 1–7 days.");
        await writes.WaitAsync();
        try{
            // Older clients save only the original fields. Preserve the current scale on those writes.
            int cost=n is JsonObject fields&&fields.ContainsKey("positiveDesireCostPercent")
                ?n.I("positiveDesireCostPercent"):(await Options()).PositiveDesireCostPercent;
            if(cost is <10 or >100)throw new UserError("Positive conversation desire cost must be 10–100 percent.");
            await db.SetConfig("options",opt with {PositiveDesireCostPercent=cost});
        }finally{writes.Release();}
    }
    static Uri Url(string value)
    {
        if(!Uri.TryCreate(value,UriKind.Absolute,out var url) || !string.IsNullOrEmpty(url.UserInfo) || !string.IsNullOrEmpty(url.Fragment) ||
           !string.IsNullOrEmpty(url.Query) || !(url.Scheme=="https" || url.Scheme=="http" && url.IsLoopback))
            throw new UserError("Use an HTTPS chat-completions endpoint, or HTTP on localhost.");
        return url;
    }
    public async Task Save(string id,JsonNode? n)
    {
        await writes.WaitAsync();
        try
        {
            var p=await Get(id);
            var endpoint=id=="openai_compatible"?n.S("endpoint").Trim():"";
            var url=endpoint==""?null:Url(endpoint);
            var secret=p.Secret;
            if(endpoint!=p.Endpoint && (url?.GetLeftPart(UriPartial.Authority) != (p.Endpoint==""?null:Url(p.Endpoint).GetLeftPart(UriPartial.Authority)))) secret="";
            var key=n.S("apiKey").Trim();
            if(key.Length>8192) throw new UserError("Key is too long.");
            if(key!="") secret=vault.Protect(key);
            if(n.S("clearKey").Equals("true",StringComparison.OrdinalIgnoreCase) || id=="codex_subscription") secret="";
            string Model(string k) {var m=n.S(k).Trim(); return m.Length<=200?m:throw new UserError("Model name is too long.");}
            var host=id=="codex_subscription" && n?["codexHost"]!=null?n.S("codexHost"):p.CodexHost;
            if(host is not ("service" or "windows"))throw new UserError("Choose a listed Codex sign-in.");
            var effort=id=="codex_subscription" && n?["conversationEffort"]!=null?n.S("conversationEffort"):p.ConversationEffort;
            if(effort is not ("" or "low" or "medium" or "high" or "xhigh" or "max" or "ultra"))throw new UserError("Choose a listed conversation reasoning level.");
            await db.SetConfig("profile/"+id,new Profile(Model("defaultModel"),Model("conversation"),Model("memory"),endpoint,secret,host,effort));
        }
        finally{writes.Release();}
    }
    public static string Resolve(Profile p,string purpose)=>purpose switch {
        "conversation" when p.Conversation.Length>0=>p.Conversation,"memory" when p.Memory.Length>0=>p.Memory,_=>p.DefaultModel};
    public async Task<string> Generate(Options options, string system,string player,Func<string,Task> emit,CancellationToken ct,string purpose="conversation",bool residentOpener=false,DialogueDispatch? dispatch=null)
    {
        var provider=Find(options.Provider);
        var p=await Get(provider.Id);
        var model=Resolve(p,purpose);
        if(model=="") throw new UserError("Set a default or "+purpose+" model in LLM Provider / Models.",409);
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(75));
        using var slot=await queue.Enter(provider.Id+"|"+model,purpose=="conversation",deadline.Token);
        if(provider.Id=="codex_subscription") {
            dispatch?.Capture(db,provider.Id,model,system,player);
            await CodexDialogue.Stream(model,system,player,emit,deadline.Token,p.CodexHost,purpose=="conversation"?p.ConversationEffort:"",residentOpener);
            return model;
        }
        var url=Url(provider.Id=="openai_compatible"?p.Endpoint:provider.Url);
        if(p.Secret=="" && provider.Id!="openai_compatible") throw new UserError("Save an API key for "+provider.Name+".",409);
        using var req=new HttpRequestMessage(HttpMethod.Post,url);
        if(p.Secret!="") req.Headers.Authorization=new AuthenticationHeaderValue("Bearer",vault.Unprotect(p.Secret));
        req.Headers.Accept.ParseAdd("text/event-stream, application/json");
        if(provider.Id=="openrouter") req.Headers.Add("X-OpenRouter-Title",ModIdentity.Name);
        req.Content=new StringContent(JsonSerializer.Serialize(new {model,stream=true,max_tokens=options.ReplyTokens,
            messages=new[]{new {role="system",content=system},new {role="user",content=player}}}),Encoding.UTF8,"application/json");
        dispatch?.Capture(db,provider.Id,model,system,player);
        using var res=await http.SendAsync(req,HttpCompletionOption.ResponseHeadersRead,deadline.Token);
        if(!res.IsSuccessStatusCode) throw new UserError(provider.Name+" returned HTTP "+(int)res.StatusCode+". Check the model, endpoint and credential.",502);
        if(res.Content.Headers.ContentType?.MediaType!="text/event-stream")
        {
            var body=await res.Content.ReadFromJsonAsync<JsonObject>(cancellationToken:deadline.Token);
            string answer=body?["choices"]?[0]?["message"].S("content")??"";
            if(answer=="") throw new UserError("The provider returned an empty reply.",502);
            await emit(answer);return model;
        }
        using var lines=new StreamReader(await res.Content.ReadAsStreamAsync(deadline.Token));
        var data=new StringBuilder(); bool finished=false;
        async Task Dispatch()
        {
            string s=data.ToString().Trim();data.Clear();
            if(s=="[DONE]"){finished=true;return;}
            if(s=="") return;
            var packet=JsonNode.Parse(s);
            if(packet?["error"]!=null)throw new UserError("The provider reported a streaming error.",502);
            var choice=(packet?["choices"] as JsonArray)?.FirstOrDefault();
            var piece=choice?["delta"].S("content")??"";
            if(piece!="")await emit(piece);
            if(choice.S("finish_reason")!="")finished=true;
        }
        while(await lines.ReadLineAsync(deadline.Token) is { } line)
        {
            if(line==""){await Dispatch();if(finished)break;}
            else if(line.StartsWith("data:")) {data.AppendLine(line[5..].TrimStart());if(data.Length>65536)throw new InvalidDataException("Oversized provider event.");}
        }
        if(data.Length>0)await Dispatch();
        if(!finished)throw new UserError("The provider stream ended before confirming completion.",502);
        return model;
    }
}

// Adapted from ReignServer 4d976d6, ProviderMiddleware.AcquireProviderSlot/ReleaseProviderSlot.
// Foreground dialogue has priority over queued background calls to the same model.
internal sealed class ProviderQueue
{
    sealed class State { public int Active, WaitingInteractive; public TaskCompletionSource Changed=new(TaskCreationOptions.RunContinuationsAsynchronously); }
    readonly Dictionary<string,State> states=new();
    static void Signal(State state){var prior=state.Changed;state.Changed=new(TaskCreationOptions.RunContinuationsAsynchronously);prior.TrySetResult();}
    public async Task<IDisposable> Enter(string key,bool interactive,CancellationToken ct)
    {
        State state;
        lock(states){if(!states.TryGetValue(key,out state!)) states[key]=state=new();if(interactive)state.WaitingInteractive++;}
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            while(true)
            {
                Task change;
                lock(states)
                {
                    if(state.Active<4 && (interactive || state.WaitingInteractive==0))
                    {state.Active++;return new Lease(()=>{lock(states){state.Active--;Signal(state);}});}
                    change=state.Changed.Task;
                }
                await change.WaitAsync(timeout.Token);
            }
        }
        finally{lock(states){if(interactive)state.WaitingInteractive--;Signal(state);}}
    }
    sealed class Lease(Action release):IDisposable {public void Dispose()=>release();}
}

internal static class CodexDialogue
{
    // Reign's initialize → account/read → thread/start → turn/start transport.
    public static async Task Stream(string model,string system,string player,Func<string,Task> emit,CancellationToken ct,string host,string effort,bool residentOpener=false)
    {
        var windows=host=="windows"?await WindowsCodex.Resolve(ct):null;
        var start=new ProcessStartInfo(windows?.Executable??"codex"){UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true,
            WorkingDirectory=windows?.DirectoryOnHost??Path.Combine(Environment.GetEnvironmentVariable("MIRTHWOOD_ECHOES_STATE")??AppContext.BaseDirectory,"dialogue")};
        Directory.CreateDirectory(start.WorkingDirectory);
        if(windows!=null)WindowsCodex.SetInterop(start);
        foreach (var a in new[]{"-c","features.shell_tool=false","-c","features.unified_exec=false","app-server","--listen","stdio://"}) start.ArgumentList.Add(a);
        using var proc=Process.Start(start)??throw new UserError("Codex runtime unavailable.",503);
        _=proc.StandardError.ReadToEndAsync(ct);
        try
        {
            int seq=0;string thread=""; bool emitted=false;
            async Task Write(object packet){await proc.StandardInput.WriteLineAsync(JsonSerializer.Serialize(packet,Database.Json).AsMemory(),ct);await proc.StandardInput.FlushAsync(ct);}
            async Task<JsonObject> Read()=> JsonNode.Parse(await proc.StandardOutput.ReadLineAsync(ct)??throw new IOException("Codex disconnected."))!.AsObject();
            async Task Notification(JsonNode msg)
            {
                if(msg["id"]!=null && msg["method"]!=null) {await Write(new{id=msg["id"],error=new{code=-32601,message="Tools and approvals are unavailable in NPC dialogue."}});return;}
                var p=msg["params"];if(thread=="" || p.S("threadId")!=thread)return;
                if(msg.S("method")=="item/agentMessage/delta" && p.S("delta")!=""){emitted=true;await emit(p.S("delta"));}
                if(!emitted && msg.S("method")=="item/completed" && p?["item"].S("type")=="agentMessage")
                {var t=p["item"].S("text");if(t!=""){emitted=true;await emit(t);}}
            }
            async Task<JsonNode?> Rpc(string method,object args)
            {
                int id=++seq;await Write(new{id,method,@params=args});
                while(true){var m=await Read();if(m["method"]==null && m.I("id")==id)
                    {if(m["error"]!=null)throw new UserError("Codex rejected "+method+". Check subscription sign-in and model.",502);return m["result"];}
                    await Notification(m);}
            }
            await Rpc("initialize",new{clientInfo=new{name=ModIdentity.Id,title=ModIdentity.Name,version=ModIdentity.Version},capabilities=new{experimentalApi=true}});
            await Write(new{method="initialized",@params=new{}});
            var account=await Rpc("account/read",new{refreshToken=false});
            if(!string.Equals(account?["account"].S("type"),"chatgpt",StringComparison.OrdinalIgnoreCase)) throw new UserError((windows!=null?"Windows Codex":"The "+ModIdentity.Name+" service account")+" needs a Codex subscription sign-in.",409);
            var t=await Rpc("thread/start",new{model,ephemeral=true,cwd=windows?.DirectoryOnWindows??start.WorkingDirectory,approvalPolicy="never",sandbox="read-only",baseInstructions=system,developerInstructions="Roleplay only. Never use tools.",config=new Dictionary<string,object>{["tools.web_search"]=false,["mcp_servers"]=new{} }});
            thread=t?["thread"].S("id")??"";
            var turn=new Dictionary<string,object>{{"threadId",thread},{"input",new[]{new{type="text",text=player}}}};
            if(!string.IsNullOrEmpty(effort))turn["effort"]=effort;
            await Rpc("turn/start",turn);
            Console.WriteLine("Echoes Codex {0} accepted: model={1} effort={2}",residentOpener?"opener":"turn",model,string.IsNullOrEmpty(effort)?"default":effort);
            while(true)
            {
                var m=await Read();await Notification(m);
                if(m.S("method")=="turn/completed" && m["params"].S("threadId")==thread)
                {if(m["params"]?["turn"].S("status")!="completed")throw new UserError("Codex did not complete the reply.",502);break;}
            }
        }
        finally{
            proc.StandardInput.Close();
            if(!proc.HasExited){
                try{await proc.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2));}
                catch(TimeoutException){if(!proc.HasExited)proc.Kill(true);}
            }
        }
    }
}
