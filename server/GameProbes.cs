using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text;

public sealed class GameProbes(CharacterJournal journal,NativeProfile native,Providers providers,VoiceCast voices)
{
    sealed class Probe(string mode,string run)
    {
        public Guid Id=Guid.NewGuid();
        public string Mode=mode,Run=run,Session="";
        public DateTime Created=DateTime.UtcNow;
        public JsonNode? Result;
        public int ProviderCalls;
        public JsonObject? LiveAnswer;
    }
    readonly object gate=new();
    readonly List<Probe> recent=[];
    static string State(Probe p)=>p.Result!=null?(p.Result["passed"]?.GetValue<bool>()==true?"passed":"failed"):
        DateTime.UtcNow-p.Created>TimeSpan.FromMinutes(p.Mode is "relationships" or "socialbalance" or "interactions" or "quests" or "voices"?10:3)?"expired":p.Session==""?"waiting":"running";
    static JsonNode View(Probe p)=>JsonSerializer.SerializeToNode(new{id=p.Id,mode=p.Mode,state=State(p),created=p.Created,result=p.Result},Database.Json)!;
    public JsonNode Start(JsonObject input)
    {
        var mode=input.Required("mode",15);
        if(mode is not ("runtime" or "world" or "interface" or "provider" or "relationships" or "socialbalance" or "interactions" or "quests" or "voices"))throw new UserError("Unknown game check.");
        lock(gate){
            if(recent.LastOrDefault() is { } active&&State(active) is "waiting" or "running")throw new UserError("A game check is already waiting.",409);
            var run=input.S("run");if(run!=""&&!Guid.TryParseExact(run,"N",out _))throw new UserError("Invalid isolated run.");
            if(mode is "provider" or "relationships" or "interactions" or "quests"&&run=="")throw new UserError("The live provider test needs an isolated save.");
            if(mode=="socialbalance"&&run=="")throw new UserError("The native balance comparison needs an isolated save.");
            if(mode=="voices"&&run=="")throw new UserError("Voice playback needs an isolated save.");
            var p=new Probe(mode,run);recent.Add(p);if(recent.Count>8)recent.RemoveAt(0);return View(p);
        }
    }
    public JsonNode Get(Guid id){lock(gate)return View(recent.Find(p=>p.Id==id)??throw new UserError("Unknown game check.",404));}
    public JsonNode? Poll(JsonObject heartbeat)
    {
        if(!Guid.TryParse(heartbeat.S("session"),out _))return null;
        lock(gate){
            var p=recent.LastOrDefault();
            if(p==null||State(p) is not ("waiting" or "running")||(p.Mode is "world" or "interface" or "provider" or "relationships" or "socialbalance" or "interactions" or "quests" or "voices")&&heartbeat["world"]==null&&heartbeat["probeWorld"]==null)return null;
            if(p.Run!=""&&p.Run!=heartbeat.S("isolatedRun"))return null;
            if(p.Session!=""&&p.Session!=heartbeat.S("session"))return null;
            p.Session=heartbeat.S("session");return new JsonObject{["id"]=p.Id.ToString(),["mode"]=p.Mode};
        }
    }
    public async Task<JsonObject> LiveTalk(Guid id,string session,JsonObject turn,CancellationToken ct)
    {
        const string words="When work is done, would you rather fish or play chess?";
        var p=Issued(id,session);
        if(p.Mode!="provider"||p.Run==""||turn.S("text")!=words)throw new UserError("This turn does not match the isolated provider test.");
        var person=turn.Required("resident",100);
        var topics=NativeProfile.LiveTopics(turn["topicFit"])??throw new UserError("The native topic readings are missing.");
        if(Interlocked.Exchange(ref p.ProviderCalls,1)!=0)throw new UserError("The live test already issued its model request.",409);
        var options=await providers.Options();
        const string instructions="""
            Roleplay as this Mirthwood resident. In one JSON object give topic, warm, neutral and cool as string fields. Write three short alternative in-character replies to the same words, for a favorable, indifferent, and unfavorable reaction respectively. The game will show the line matching its own roll. Keep the same personal facts across alternatives. Enclose any action in *single asterisks*. Omit scores and topic labels from the lines.
            """;
        var wire=new StringBuilder();
        var model=await providers.Generate(options with {ReplyTokens=240},instructions+"\n"+TalkTopics.Instructions+"\n"+JsonSerializer.Serialize(new {resident=person,currentTopicFit=topics}),words,part=>{
            if(wire.Length+part.Length>8192)throw new UserError("The live test response was too long.",502);
            wire.Append(part);return Task.CompletedTask;
        },ct);
        var data=JsonNode.Parse(wire.ToString()) as JsonObject??throw new UserError("The live test response was unreadable.",502);
        var answer=new JsonObject{["topic"]=TalkTopics.Resolve(data.S("topic"),words),["provider"]=options.Provider,["model"]=model};
        foreach(var key in new[]{"warm","neutral","cool"})answer[key]=data.Required(key,500);
        lock(gate)p.LiveAnswer=answer;
        return answer;
    }
    Probe Issued(Guid id,string session)
    {
        lock(gate){
            var p=recent.Find(x=>x.Id==id);
            return p!=null&&State(p)=="running"&&p.Session==session?p:throw new UserError("This game check is no longer pending.",409);
        }
    }
    public async Task Fixture(Guid id,string session,JsonObject turn,HttpContext ctx)
    {
        var probe=Issued(id,session);
        if(probe.Mode!="interface")throw new UserError("This check has no fixture stream.",409);
        if(probe.Run!=""&&turn["voice"]!=null&&turn.S("text")=="I am ready."){
            await Task.Delay(1200,ctx.RequestAborted);
            throw new UserError("The test voice is unavailable.",503);
        }
        if(turn.S("campaign")!="fixture"||turn.S("actor")!="resident"||turn.S("text")!="*I lift my hat.* I’ll collect the flour. Is the mill open?"
            ||turn.ContainsKey("commitment"))throw new UserError("The fixture expected ordinary player speech.");
        ctx.Response.ContentType="application/x-ndjson";
        var stream=new DialogueOutput(async part=>{
            await ctx.Response.WriteAsync(JsonSerializer.Serialize(new{type="delta",text=part})+"\n",ctx.RequestAborted);await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
        });
        if(probe.Run!="")await Task.Delay(2400,ctx.RequestAborted);
        await stream.Push("{\"reply\":\"*I nod");
        await Task.Delay(180,ctx.RequestAborted);
        await stream.Push(".* The mill is open.\",\"commitments\":[{\"speaker\":\"player\",\"quote\":\"I’ll collect the flour.\"}]}");
        var checkedReply=stream.Finish(turn.S("text"),id,null);
        if(checkedReply.Status!="reviewed"||checkedReply.Items.Count!=1)throw new InvalidOperationException("Fixture agreement failed transcript validation.");
        await ctx.Response.WriteAsync("{\"type\":\"done\"}\n",ctx.RequestAborted);
    }
    public async Task Complete(Guid id,JsonObject input,Conversation dialogue)
    {
        var p=Issued(id,input.Required("session",64));
        if(input["compressed"] is JsonValue){
            if(p.Run=="")throw new UserError("Compressed reports require an isolated game check.");
            try{
                byte[] bytes=Convert.FromBase64String(input.Required("compressed",32000));
                using var encoded=new MemoryStream(bytes);
                using var gzip=new System.IO.Compression.GZipStream(encoded,System.IO.Compression.CompressionMode.Decompress);
                using var report=new MemoryStream();var buffer=new byte[8192];int n;
                while((n=await gzip.ReadAsync(buffer))>0){
                    if(report.Length+n>512*1024)throw new UserError("The isolated report exceeds the limit.");
                    report.Write(buffer,0,n);
                }
                input=JsonNode.Parse(report.ToArray()) as JsonObject??throw new UserError("Invalid isolated report.");
            }catch(Exception e) when(e is FormatException or InvalidDataException or JsonException){throw new UserError("Unreadable isolated report.");}
            if(input.S("session")!=p.Session)throw new UserError("The isolated report belongs to another session.",409);
        }
        var checks=new List<object>();bool pass=true;
        void Check(string name,bool ok,string detail){checks.Add(new{name,passed=ok,detail});pass&=ok;}
        Check("Installed release",input.S("version")==ModIdentity.Version,input.S("version"));
        Check("Native social observation hook",input["socialHook"]?.GetValue<bool>()==true,"NPCChatController.ReceiveResponse");
        Check("Native hired-work observation hook",input["workHook"]?.GetValue<bool>()==true,"NPCController.AddToHiringItemList");
        Check("Unity scene",input.S("scene").Length>0,input.S("scene")+" · Unity "+input.S("unity"));
        if(p.Mode=="voices"){
            var rows=input["voiceRows"]?.AsArray().OfType<JsonObject>().ToArray() ?? [];
            Check("Original cast through FMOD",rows.Length>=3&&rows.All(r=>r.I("events",0)==36&&r["played"]?.GetValue<bool>()==true),string.Join(", ",rows.Select(r=>r.S("actor")+" ("+r.S("profile")+")")));
            var residents=rows.Where(r=>r["player"]?.GetValue<bool>()==false).ToArray();
            Check("Residents draw from their matching voice casts",residents.Length>=2&&residents.All(r=>
                voices.Roster().Any(v=>v.Id==r.S("profile")&&v.Sex==(r["male"]?.GetValue<bool>()==true?"Male":"Female"))),
                string.Join(", ",residents.Select(r=>r.S("actor")+" "+r.S("profile"))));
            var w=input["voiceWorld"];
            if(w!=null){
                string campaign=Saves.Campaign(w.I("slot"),w.S("created"),w.S("player"));
                var settings=await voices.Settings();
                var playerSetting=settings.Players.GetValueOrDefault(campaign)??settings.Default;
                string? selected=playerSetting.Voice??(await voices.AutomaticPlayers()).GetValueOrDefault(campaign);
                Check("Web preview and in-game player use the same voice and pitch",selected==input.S("playerVoice")&&
                    playerSetting.Pitch==input.I("playerPitch",99),selected+" · "+playerSetting.Pitch+" semitones");
            }else Check("Loaded voice campaign",false,"The game did not report its save identity.");
            Check("Native interface sound",input["unrelatedPreserved"]?.GetValue<bool>()==true,"The chat icon kept its native sound.");
            var clips=input["speech"]?.AsArray().OfType<JsonObject>().ToArray()??[];
            bool Sound(int i,string voice,int pitch,string words)=>clips.Length>i&&clips[i].S("voice")==voice&&clips[i].I("pitch",99)==pitch&&
                Math.Abs((clips[i]["channelPitch"]?.GetValue<double>()??0)-Math.Pow(2,pitch/12.0))<.002&&
                clips[i].S("text")==words&&clips[i].I("positionMs")>0&&clips[i].I("captionSteps")>=1;
            Check("Player voice on the game audio channel",Sound(0,input.S("playerVoice"),input.I("playerPitch"),"Good morning."),input.S("playerVoice"));
            Check("Narrator speaks the italic action",Sound(1,"narrator",0,"I lift the sack.")&&input["actionItalic"]?.GetValue<bool>()==true,"I lift the sack.");
            Check("Resident voice on the game audio channel",Sound(2,input.S("residentVoice"),input.I("residentPitch"),"The mill is open."),input.S("residentVoice"));
            Check("Audible signal at each speaker’s FMOD fader",clips.Length==3&&clips.All(c=>(c["mixPeak"]?.GetValue<double>()??0)>.0001),
                string.Join(", ",clips.Select(c=>c.S("voice")+" "+c.S("mixPeak"))));
            Check("Captions follow playback in the character bubbles",input["playerPartial"]?.GetValue<bool>()==true&&
                input["npcPartial"]?.GetValue<bool>()==true&&input["speakerAnchor"]?.GetValue<bool>()==true&&
                input["speechComplete"]?.GetValue<bool>()==true,"Read text in both live bubble components while the game audio channel advanced.");
            Check("Repeated presentation speaks the reply once",clips.Length==3&&clips.Select(x=>x.S("waveSha256")).Distinct().Count()==3,"Two spoken lines and one narrated action.");
            Check("Complete text when speech is unreachable",input["audioFallback"]?.GetValue<bool>()==true,"The native bubble retained speech and italic action after the audio request failed.");
        }
        if(p.Mode=="world"){
            var target=input["target"];
            Check("Loaded campaign",target?["world"]!=null,"Active NPCs: "+input.I("npcCount",0));
            if(target!=null){
                try{
                    var hit=await dialogue.Resolve(target);var actor=await journal.Actor(hit.S("campaign"),hit.S("actor"));
                    Check("Native NPC matches its saved profile",actor.S("name")==input.S("npc"),actor.S("name")+" · "+actor.S("town"));
                    if(input.ContainsKey("isolated")){
                        var baseline=native.Describe(actor)["topicFit"];
                        Check("Saved conversation topics agree with the game",baseline!=null&&JsonNode.DeepEquals(baseline,input["baselineTopics"]),
                            input["baselineTopics"]?["closer"] is JsonArray names?string.Join(", ",names.Select(x=>x?.ToString())):"Native topic data unavailable.");
                        Check("Live mood and topic readings",NativeProfile.LiveTopics(input["liveTopics"])!=null,"Read from the resident’s live mood and native conversation topic list.");
                    }
                }catch(UserError e){Check("Native NPC matches its saved profile",false,e.Message);}
            }else Check("Native NPC matches its saved profile",false,"No loaded NPC with a save identity was available.");
            if(input.ContainsKey("isolated")){
                Check("Isolated native save paths",input["isolated"]?.GetValue<bool>()==true && input.I("testSlot")==0,"Disposable native slot 0 and separate test preferences.");
                Check("In-game conversation target",input["resolvedInPanel"]?.GetValue<bool>()==true,"Resolved the resident from the native panel without sending a player turn.");
                var records=input["exercised"] as JsonArray??[];var owner=target.S("owner");var listener=input.S("listenerOwner");
                Check("Social callback attributes both participants",owner!=""&&listener!=""&&listener!=owner
                    &&records.Any(r=>r.S("owner")==owner&&r.S("kind")=="social"&&r.S("chosenAction")=="Small Talk")
                    &&records.Any(r=>r.S("owner")==listener&&r.S("kind")=="social"&&r.S("chosenAction")==""),
                    "Exercised the game’s Small Talk response in the disposable world.");
                Check("Hired-work callback records the item and amount",records.Any(r=>r.S("owner")==owner&&r.S("kind")=="work"&&r.S("description").Contains("2 Flour")),
                    "Exercised the game’s hired-work collection in the disposable world.");
            }
        }
        if(p.Mode=="interface"){
            Check("Resident’s native overhead bubble",input["nativeStyle"]?.GetValue<bool>()==true&&input["onScreen"]?.GetValue<bool>()==true,
                input.S("speaker")+" · "+input.S("sprite")+" · "+input.S("font"));
            bool Yes(string key)=>input[key]?.GetValue<bool>()==true;
            Check("Focused editor above the player",Yes("playerAnchor")&&Yes("focused")&&Yes("acceptedInput"),"The player's own world anchor and keyboard field received the draft.");
            Check("Player and NPC action lettering",Yes("draftItalic")&&Yes("acceptedItalic")&&Yes("streamItalic")&&Yes("replyItalic"),"Read the actual TMP styles during drafting, after acceptance, mid-stream and after the reply.");
            Check("Streamed speech on screen",input.S("reply")=="*I nod.* The mill is open."&&input.S("displayed")=="I nod. The mill is open."&&input.I("deltas")==2&&Yes("done"),input.S("displayed"));
            if(p.Run!=""){
                if(input.S("probeError")!="")Check("Interface probe reached its reply",false,input.S("probeError"));
                Check("Enter accepts the typed draft",Yes("windowEnter"),"Sent Return to the isolated game window through Windows.");
                Check("Rolling periods above the selected resident",Yes("waitCycle")&&Yes("waitAnchored")&&Yes("waitSelectedOnly")&&Yes("waitCapture"),input.S("waitStages"));
                Check("Resident waits while the world advances",Yes("waitStationary")&&Yes("waitReleased"),"Checked the resident's position and native navigation agent during the delay and after the reply.");
                Check("Closing or retrying clears the wait",Yes("waitCancelled")&&Yes("waitRetry"),"Closed an outstanding request, reopened Talk, and recovered the draft after a rejected request.");
                Check("Selected reply replaces the waiting dots",Yes("waitVoiceReply"),Yes("waitVoiceQueued")?"The periods kept rolling during a delayed local voice failure, then the actual reply appeared.":"The unvoiced reply appeared immediately.");
                Check("Growing, wrapping player bubble",Yes("grows")&&Yes("wrapped")&&Yes("editAfterWrap")&&Yes("literalTags")&&Yes("editorEdges"),"Measured width and height, a 36-character hard wrap, caret deletion, literal tags and four screen corners.");
                Check("Extended draft editing",Yes("longDraftCaret")&&Yes("middleEdit")&&Yes("shortDraftVisible"),"The caret remains visible in a long draft; shortening it brings every fitting line back into view. Mid-action edits preserve the raw position.");
                Check("Player clock pause and restoration",Yes("clockPaused")&&Yes("resumeSpeed")&&Yes("nextDraftPaused")&&Yes("closeRestoresClock"),"Measured the scaled simulation and campaign calendar across typing, submission, the next draft and closing.");
                Check("Player anchor and draft capture",Yes("playerTracks")&&Yes("draftCapture"),"Player motion and the visible writing bubble were checked in the copied world.");
                Check("Native context prompts",Yes("contextSuppressed")&&Yes("contextRestored"),"The player’s stock interaction prompts yield to the speech bubble and return on closing.");
                Check("Campaign clock advances after Enter",Yes("clockAdvanced"),"Sampled the native day clock again after submission.");
                Check("Native Talk and Escape",Yes("talkKey")&&Yes("escapeRestored"),"Invoked the resident’s native Talk option, sent Escape and restored half-speed gameplay.");
                Check("Independent native pause",Yes("otherPausePreserved")&&Yes("alreadyPaused"),"The closing editor preserved an existing native pause and a pause requested while typing.");
                Check("Speech follows the resident",input["tracksSpeaker"]?.GetValue<bool>()==true,"Moved and restored the native overhead anchor in the test copy.");
                Check("Long replies and screen edges",input.I("pages")>1&&input.I("nextPage")==2&&input.I("backPage")==1&&input["edgeFit"]?.GetValue<bool>()==true,input.I("pages")+" pages, previous and next controls, four screen corners.");
                Check("Player draft leaves the resident's reply visible",Yes("separateBubbles"),"Long draft and paged resident response both fit on screen with separate bounds.");
                Check("Native reference capture",input["referenceCapture"]?.GetValue<bool>()==true,"The game’s own bark on the same resident with matching words.");
            }
            Check("Game capture",input["capture"]?.GetValue<bool>()==true,"Latest interface image in the local Echoes Diagnostics folder.");
        }
        if(p.Mode=="provider"){
            var trial=input["trial"];var answer=p.LiveAnswer;
            Check("One live model call",p.ProviderCalls==1&&answer!=null,answer.S("provider")+" · "+answer.S("model"));
            Check("Cloud topic",answer.S("topic")=="Leisure"&&trial.S("topic")=="Leisure",trial.S("topic")+" · "+trial.S("resident"));
            Check("Native player scoring",trial?["applied"]?.GetValue<bool>()==true&&trial["score"]!=null,
                "Friendship "+trial.S("before")+" → "+trial.S("after")+". "+trial.S("branch")+" reaction.");
            Check("Selected reply above the resident",p.Run!=""&&trial.S("reply")!=""&&trial.S("reply")==answer.S(trial.S("branch"))&&trial?["visible"]?.GetValue<bool>()==true&&input["capture"]?.GetValue<bool>()==true,trial.S("reply"));
        }
        if(p.Mode=="relationships"){
            var trials=input["trials"] as JsonArray??[];
            Check("Different saved residents",trials.Select(t=>t.S("actor")).Distinct().Count()>=3,input.S("probeError"));
            foreach(var t in trials)Check(t.S("resident")+" · "+t.S("expected"),t?["passed"]?.GetValue<bool>()==true,
                t.S("topic")+" · friendship "+t.S("before")+" → "+t.S("after")+" · "+t.S("scoreCalls")+" score calls.");
            Check("Copied game reload",input["reloaded"]?.GetValue<bool>()==true,input.S("probeError"));
            Check("Recognition, public standing and visible clothing",input["playerImpressions"]?["passed"]?.GetValue<bool>()==true,input.S("probeError"));
            Check("Six player impression conversations",input["impressionDialogue"] is JsonArray impressions && impressions.Count==6 &&
                impressions.All(t=>t?["passed"]?.GetValue<bool>()==true),input.S("probeError"));
            Check("Two personal story turns and resident switch",input["story"] is JsonArray story&&story.Count==2&&
                story.All(s=>s?["passed"]?.GetValue<bool>()==true)&&input["freshConversation"]?.GetValue<bool>()==true,input.S("probeError"));
        }
        if(p.Mode is "socialbalance" or "relationships"){
            var sessions=input["balance"] as JsonArray??[];
            Check("Native friendship, desire, return cards and recovery",sessions.Count==4&&sessions.All(s=>s?["matched"]?.GetValue<bool>()==true&&s["recoveredTo"]!=null),input.S("probeError"));
            var cards=input["cardBalance"] as JsonArray??[];
            Check("Rumors, Cheer Up and Complain match native score and desire",cards.Count==3&&cards.All(s=>s?["matched"]?.GetValue<bool>()==true),input.S("probeError"));
            Check("Willing again after native recovery",input["afterRest"]?["passed"]?.GetValue<bool>()==true,input.S("probeError"));
            Check("Graded positive desire cost",input["desireScale"]?["passed"]?.GetValue<bool>()==true,input.S("probeError"));
        }
        if(p.Mode=="interactions"){
            foreach(var item in input["cases"] as JsonArray??[])Check(item.S("name"),item?["passed"]?.GetValue<bool>()==true,item.S("detail"));
            Check("Completed copied-save interactions",input["interactionsComplete"]?.GetValue<bool>()==true,input.S("probeError"));
        }
        if(p.Mode=="quests"){
            Check("Talk recovery and native Speak through a backend outage",input["outage"]?["passed"]?.GetValue<bool>()==true,input.S("probeError"));
            Check("Native quest choices, terms, callbacks and reload",input["verifiedQuests"]?.GetValue<bool>()==true,input.S("probeError"));
        }
        var result=JsonSerializer.SerializeToNode(new{passed=pass,checks},Database.Json)!;
        if(p.Mode=="voices"){
            result["reactions"]=input["voiceRows"]?.DeepClone();result["speech"]=input["speech"]?.DeepClone();result["error"]=input["probeError"]?.DeepClone();
            foreach(string key in new[]{"playerPartial","npcPartial","speechComplete","speakerAnchor","actionItalic","playerCaptions","npcCaptions","playerCapture","residentCapture","audioFallback"})
                result[key]=input[key]?.DeepClone();
        }
        if(p.Mode=="interactions"){foreach(var key in new[]{"cases","conversation","restoredHead","liveAfterReload","postLoadEvents","giftReference","giftFollowUp","events","hooks","probeError","turns","path","passingGestures","quickMenuPages","quickReference","quickCapture","openers","walkAway","guardWarning","pairedCapture","passingCapture"})result[key]=input[key]?.DeepClone();}
        if(p.Mode=="quests"){result["quests"]=input["quests"]?.DeepClone();result["outage"]=input["outage"]?.DeepClone();result["error"]=input["probeError"]?.DeepClone();result["reload"]=input["questReload"]?.DeepClone();}
        if(p.Mode=="provider"){result["trial"]=input["trial"]?.DeepClone();result["error"]=input["probeError"]?.DeepClone();}
        if(p.Mode=="relationships"){result["trials"]=input["trials"]?.DeepClone();result["rewind"]=input["rewind"]?.DeepClone();result["story"]=input["story"]?.DeepClone();result["freshConversation"]=input["freshConversation"]?.DeepClone();result["playerImpressions"]=input["playerImpressions"]?.DeepClone();result["impressionDialogue"]=input["impressionDialogue"]?.DeepClone();}
        if(p.Mode is "relationships" or "socialbalance"){result["balance"]=input["balance"]?.DeepClone();result["cardBalance"]=input["cardBalance"]?.DeepClone();result["afterRest"]=input["afterRest"]?.DeepClone();result["desireScale"]=input["desireScale"]?.DeepClone();result["error"]=input["probeError"]?.DeepClone();}
        lock(gate){if(State(p)=="running")p.Result=result;}
    }
}
