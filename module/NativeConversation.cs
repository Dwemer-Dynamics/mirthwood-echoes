using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Networking;
using UnityEngine.UI;
using _Mirthwood.Scripts.Demo;
using _Mirthwood.Scripts.Demo.Chat;
using _Mirthwood.Scripts.Demo.Combat;
using _Mirthwood.Scripts.Demo.ScriptableObjects;
using _Mirthwood.Scripts.Demo.Tests;

namespace Mirthwood.Echoes
{
    internal sealed class DialogueStream : DownloadHandlerScript
    {
        readonly MemoryStream pending = new MemoryStream();
        readonly Action<string> show;
        public string Reply = "", Error = "";
        public int Deltas;
        public bool Done;
        public DialogueStream(Action<string> show) : base(new byte[4096]) { this.show = show; }
        protected override bool ReceiveData(byte[] data,int size)
        {
            if(data==null || size==0)return true;
            for(int i=0;i<size;i++){
                if(data[i]==10){ReadLine();continue;}
                if(pending.Length>=32768){Error="The server sent too much text.";return false;}
                pending.WriteByte(data[i]);
            }
            return true;
        }
        protected override void CompleteContent(){if(pending.Length>0)ReadLine();}
        void ReadLine()
        {
            var text=Encoding.UTF8.GetString(pending.ToArray()).Trim();pending.SetLength(0);
            if(text=="")return;
            try{
                var msg=JObject.Parse(text);string type=(string)msg["type"];
                if(type=="delta"){string part=(string)msg["text"]??"";Reply+=part;Deltas++;show(Reply);}
                else if(type=="done")Done=true;
                else Error=(string)msg["error"]??(string)msg["message"]??"The response could not be read.";
            }catch(JsonException){Error="The server returned an unreadable line.";}
        }
    }

    internal sealed partial class NativeConversation
    {
        static string Address=>IsolatedGameRun.Active?.TalkAddress??"http://127.0.0.1:17841/api/";
        enum Phase { Draft, Waiting, Reading }
        readonly Plugin host;
        ConversationSpeech speech;
        GameObject root,previousSelection;
        OverheadSpeech playerBubble,npcBubble;
        Phase phase;
        GameState.State previousState;
        bool controlsGame,busy,ready,resolving,conversationOver;
        bool cursorVisible;CursorLockMode cursorLock;
        int generation,phaseFrame;
        float nextChatRefresh,positiveRun;
        int waitingDots;
        float waitStarted;
        string campaign,actor,person="",accepted,pendingFinish;
        UnityWebRequest active;
        Canvas[] contextPrompts=Array.Empty<Canvas>();
        NPCController resident;
        object targetIdentity;
        TalkExchange exchange;
        JObject freshCue;
        string fixtureUrl;
        JObject fixtureReport;
        public bool Visible=>root!=null;
        public bool Ready=>Visible&&ready;
        public NativeConversation(Plugin host){this.host=host;}

        static GameObject SpeechSurface(string name,bool interactive)
        {
            var surface=new GameObject(name,typeof(RectTransform),typeof(Canvas),typeof(CanvasScaler));
            if(interactive)surface.AddComponent<GraphicRaycaster>();
            UnityEngine.Object.DontDestroyOnLoad(surface);
            var canvas=surface.GetComponent<Canvas>();canvas.renderMode=RenderMode.ScreenSpaceOverlay;canvas.sortingOrder=32000;
            var scaler=surface.GetComponent<CanvasScaler>();scaler.uiScaleMode=CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution=new Vector2(1280,720);scaler.matchWidthOrHeight=.5f;
            Canvas.ForceUpdateCanvases();
            return surface;
        }
        bool Build(NPCController npc)
        {
            var player=PlayerCharacterController.Instance;if(player==null)return false;
            generation++;person=npc.CharacterInfo.name;busy=false;ready=false;resolving=false;conversationOver=false;positiveRun=0;resident=npc;accepted=pendingFinish=null;
            root=SpeechSurface("Mirthwood Echoes • Conversation",true);
            var surface=(RectTransform)root.transform;npcBubble=OverheadSpeech.Create(npc,surface);playerBubble=OverheadSpeech.Create(npc,surface,player);
            if(npcBubble==null||playerBubble==null){Close();return false;}
            speech=new ConversationSpeech(host);
            playerBubble.CreateEditor(Submit);
            previousSelection=EventSystem.current?.currentSelectedGameObject;previousState=GameState.GetCurrentState(1);controlsGame=true;
            cursorVisible=Cursor.visible;cursorLock=Cursor.lockState;GameState.SetCurrentState(1,GameState.State.UI,false);
            // The native context-menu fade normally runs on a scaled timer, which stops as soon as typing pauses time.
            var menu=player.GetComponent<PlayerChat>()?.ContextMenuUI;
            contextPrompts=menu==null?Array.Empty<Canvas>():menu.GetComponentsInChildren<Canvas>(true).Where(c=>c.enabled).ToArray();
            foreach(var prompt in contextPrompts)prompt.enabled=false;
            Cursor.lockState=CursorLockMode.None;Cursor.visible=true;Edit("");return true;
        }
        void Edit(string draft,string note="")
        {
            speech?.Stop(true);
            if(waitingDots!=0){waitingDots=0;npcBubble.SetVisible(false);}
            phase=Phase.Draft;phaseFrame=Time.frameCount;DraftPause.Begin();npcBubble.Note(npcBubble.Visible?PageHint():"");
            playerBubble.Edit(draft);playerBubble.Note(note);
        }
        void WaitForReply(string words,string note="")
        {
            accepted=words;phase=Phase.Waiting;phaseFrame=Time.frameCount;
            if(waitingDots==0){
                waitStarted=Time.unscaledTime;waitingDots=1;
                npcBubble.Note("");npcBubble.Show(".");
            }
            DraftPause.End();playerBubble.Show(words);playerBubble.Note(note);
        }
        internal bool WaitingFor(ChatController speaker) =>
            waitingDots!=0 && Visible && ReferenceEquals(speaker.Character,resident);

        void AnimateWaitingDots()
        {
            if(waitingDots==0)return;
            int count=1+Mathf.FloorToInt((Time.unscaledTime-waitStarted)/.4f)%3;
            if(count==waitingDots)return;
            waitingDots=count;npcBubble.Show(new string('.',count));
        }
        void ShowResidentReply(string value,bool partial=false)
        {
            if(string.IsNullOrWhiteSpace(value)||(partial&&value=="*"))return;
            if(waitingDots!=0||phase!=Phase.Reading)phaseFrame=Time.frameCount;
            waitingDots=0;phase=Phase.Reading;
            playerBubble.SetVisible(false);npcBubble.Show(value,partial);ReadingHint();
        }
        string PageHint()=>npcBubble.Pages>1?"PgUp / PgDn  "+npcBubble.Page+" / "+npcBubble.Pages:"";
        void Page(int direction){npcBubble.TurnPage(direction);if(phase==Phase.Draft)npcBubble.Note(PageHint());else ReadingHint();}
        void ReadingHint()
        {
            if(waitingDots!=0)return;
            string pages=npcBubble.Pages>1?PageHint()+"    ":"";
            npcBubble.Note(pages+(busy?"":pendingFinish!=null?"Enter to save this reply":conversationOver?"Enter to leave":"Enter to reply"));
        }
        void Advance()
        {
            if(pendingFinish!=null){string original=pendingFinish;pendingFinish=null;host.StartCoroutine(Speak(original,generation));return;}
            if(conversationOver)Close();else Edit("");
        }
        public void HandleKeys()
        {
            if(Input.GetKeyDown(KeyCode.Escape)){Close();return;}
            if(Input.GetKeyDown(KeyCode.F8)){
                if(phase==Phase.Reading&&!busy&&waitingDots==0)Advance();else Close();return;
            }
            if(phase==Phase.Reading&&!busy&&waitingDots==0&&Time.frameCount!=phaseFrame&&(Input.GetKeyDown(KeyCode.Return)||Input.GetKeyDown(KeyCode.KeypadEnter))){Advance();return;}
            if((phase==Phase.Reading||phase==Phase.Draft)&&npcBubble.Visible&&npcBubble.Pages>1){
                if(Input.GetKeyDown(KeyCode.PageDown)||Input.mouseScrollDelta.y<0)Page(1);
                if(Input.GetKeyDown(KeyCode.PageUp)||Input.mouseScrollDelta.y>0)Page(-1);
            }
        }
        public void Follow()
        {
            if(!Visible)return;
            DraftPause.Maintain();
            if(resident==null||!resident.isActiveAndEnabled||!playerBubble.Follow()||!npcBubble.Follow()){Close();return;}
            AnimateWaitingDots();
            if(npcBubble.Visible){
                var menu=PlayerCharacterController.Instance?.GetComponent<PlayerChat>()?.ContextMenuUI;
                if(npcBubble.QuickMenuBounds(menu) is Rect occupied)npcBubble.Avoid(occupied);
                if(playerBubble.Visible)playerBubble.Avoid(npcBubble.Footprint);
            }
            // Keep the native pair facing each other during audible playback.
            if(waitingDots==0&&phase==Phase.Reading&&speech?.Busy==true&&targetIdentity!=null&&Time.time>=nextChatRefresh){
                var player=PlayerCharacterController.Instance?.GetComponent<PlayerChat>();
                var brain=resident.GetComponent<NPCChatController>();
                if(brain!=null&&player!=null&&brain.chatTarget==player)FaceForTalk(brain,player);
            }
        }
        void FaceForTalk(NPCChatController brain,PlayerChat player)
        {
            brain.PrepareForChat(player);player.PrepareForChat(brain);nextChatRefresh=Time.time+4;
        }
        internal static JObject TopicFit(NPCController npc,bool includeMood)
        {
            var brain=npc==null?null:npc.GetComponent<NPCChatController>();
            var info=npc?.CharacterInfo;
            if(brain?.dnaData?.chatActions==null || info?.personality?.values==null || info.personality.values.Length<5)return null;
            var topics=brain.dnaData.chatActions.Where(x=>x!=null && (x.name=="Small Talk"||x.name=="Leisure"||x.name=="Intellectual"||x.name=="Deep"||x.name=="Dumb")).ToArray();
            if(topics.Length!=5)return null;
            var dna=info.personality;
            if(includeMood && info.mood?.values?.Length>=5)dna=(dna+info.mood*.5f).Normalized();
            var closer=brain.dnaData.FindClosestDna(dna,topics,.0001f,2);
            var farther=brain.dnaData.FindClosestDna(dna*-1f,topics,.0001f,2);
            return JObject.FromObject(new{closer=closer.Select(x=>x.name),farther=farther.Select(x=>x.name)});
        }
        public void Open(NPCController npc,object target)
        {
            if(npc?.CharacterInfo==null||Visible||GameState.GetCurrentState(1)!=GameState.State.Gameplay)return;
            string opener=null;
            if(passing?.Speaker==npc&&passing.Heard&&passing.Bubble!=null&&InReplyRange(npc)){
                opener=passing.Line;
                if(passing.EventId!=null)NativeCues.Recent(npc,passing.EventId,passing.Kind,passing.Line);
            }
            DismissPassingLine();
            if(!Build(npc))return;
            if(opener!=null){npcBubble.Show(opener);Follow();}
            var brain=npc.GetComponent<NPCChatController>();var player=PlayerCharacterController.Instance.GetComponent<PlayerChat>();
            if(brain!=null&&player!=null)FaceForTalk(brain,player);
            freshCue=NativeCues.Take(npc);targetIdentity=target;host.StartCoroutine(Resolve(target,generation));
        }
        internal static UnityWebRequest Post(string url,object payload)
        {
            var r=new UnityWebRequest(url,"POST");r.uploadHandler=new UploadHandlerRaw(Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(payload)));
            r.downloadHandler=new DownloadHandlerBuffer();r.SetRequestHeader("Content-Type","application/json");return r;
        }
        IEnumerator Resolve(object target,int epoch)
        {
            resolving=true;
            using(var r=Post(Address+"module/resolve",target)){
                r.timeout=8;active=r;yield return r.SendWebRequest();if(epoch!=generation||!Visible)yield break;active=null;resolving=false;
                if(r.responseCode!=200){
                    string note=Error(r,"Echoes could not reach this character. Esc to leave.");
                    if(accepted==null)playerBubble.Note(note);else{var draft=accepted;accepted=null;Edit(draft,note);}
                    yield break;
                }
                try{
                    var hit=JObject.Parse(r.downloadHandler.text);campaign=(string)hit["campaign"];actor=(string)hit["actor"];
                    exchange=new TalkExchange(campaign,actor);
                    var pending=exchange.Pending();
                    if(pending!=null)WaitForReply((string)pending["text"],"Recovering your exchange…");
                    ready=true;
                }catch(Exception){
                    const string note="Echoes could not open the pending exchange. Esc to leave.";
                    if(accepted==null)playerBubble.Note(note);else Retry(accepted,note);
                    yield break;
                }
            }
            if(accepted!=null)host.StartCoroutine(Speak(accepted,epoch));
        }
        internal static string Error(UnityWebRequest r,string fallback)
        {
            try{return (string)JObject.Parse(r.downloadHandler.text)["error"]??fallback;}catch{return fallback;}
        }
        void Submit(string words)
        {
            if(!Visible||busy||phase!=Phase.Draft||(IsolatedGameRun.Active!=null&&fixtureUrl==null&&IsolatedGameRun.Active.TalkAddress==null))return;
            if(string.IsNullOrWhiteSpace(words)){host.StartCoroutine(Refocus(generation));return;}
            WaitForReply(words.Trim());
            if(fixtureReport!=null)fixtureReport["acceptedItalic"]=playerBubble.Styled("lift",true)&&!playerBubble.VisibleText.Contains("*");
            if(ready)host.StartCoroutine(Speak(accepted,generation));
            else if(!resolving)host.StartCoroutine(Resolve(targetIdentity,generation));
        }
        IEnumerator Refocus(int epoch)
        {
            yield return null;
            if(epoch==generation&&Visible&&phase==Phase.Draft)playerBubble.Input.ActivateInputField();
        }
        IEnumerator Speak(string message,int epoch)
        {
            busy=true;bool preview=fixtureUrl!=null;
            if(preview&&campaign=="isolated-provider"){
                yield return LiveTalkTest(message,epoch);yield break;
            }
            if(!preview){yield return NativeTurn(message,epoch);yield break;}
            var stream=new DialogueStream(value=>{
                if(epoch!=generation||!Visible)return;
                ShowResidentReply(value,true);
                if(preview&&value=="*I nod")fixtureReport["streamItalic"]=npcBubble.Styled("nod",true);
            });
            using(var r=new UnityWebRequest(fixtureUrl,"POST")){
                r.uploadHandler=new UploadHandlerRaw(Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(new{id=Guid.NewGuid(),campaign,actor,text=message,liveTopicFit=TopicFit(resident,true)})));
                r.SetRequestHeader("Content-Type","application/json");r.downloadHandler=stream;r.timeout=preview?10:90;active=r;
                yield return r.SendWebRequest();if(epoch!=generation||!Visible)yield break;active=null;busy=false;accepted=null;
                if(preview){fixtureReport["reply"]=stream.Reply;fixtureReport["done"]=stream.Done;fixtureReport["deltas"]=stream.Deltas;fixtureReport["acceptedInput"]=r.responseCode==200&&stream.Done;}
                if(r.responseCode==200&&stream.Done&&stream.Error==""){
                    ShowResidentReply(stream.Reply);
                }else Edit(message,stream.Error!=""?stream.Error:"Reply interrupted. Enter to retry. Esc to leave.");
            }
        }
        IEnumerator LiveTalkTest(string message,int epoch)
        {
            // The provider comparison and its single score write belong exclusively to the disposable world.
            if(IsolatedGameRun.Active?.PathsMatch()!=true)yield break;
            var npc=resident;
            using(var r=Post(fixtureUrl,new {resident=person,text=message,topicFit=TopicFit(npc,true)})){
                active=r;r.timeout=85;float started=Time.realtimeSinceStartup;
                yield return r.SendWebRequest();
                if(epoch!=generation || !Visible)yield break;
                active=null;busy=false;accepted=null;
                if(r.responseCode!=200){fixtureReport["probeError"]=Error(r,"The live provider test failed.");yield break;}
                try{
                    if(npc!=resident || npc==null || IsolatedGameRun.Active?.PathsMatch()!=true)yield break;
                    var answer=JObject.Parse(r.downloadHandler.text);string topic=(string)answer["topic"];
                    var info=npc.CharacterInfo;var player=PlayerCharacterController.Instance.CharacterInfo;
                    var relation=info.relationships[player.id];
                    float before=relation.friendship,score=0;
                    var actions=npc.GetComponent<NPCChatController>().dnaData.chatActions;
                    var action=actions.SingleOrDefault(a=>a!=null&&a.category==DnaData.Dna.DnaCategories.Chat&&a.name==topic);
                    if(action==null&&topic!="None")throw new InvalidDataException("Unknown native Talk topic.");
                    foreach(var key in new[]{"warm","neutral","cool"})if(string.IsNullOrWhiteSpace((string)answer[key]))throw new InvalidDataException("Missing reaction line.");
                    if(action!=null)DnaTesting.ScoreAction(action,info,player,out score,true);
                    string branch=score>0?"warm":score<0?"cool":"neutral";
                    string reply=(string)answer[branch];
                    ShowResidentReply(reply);
                    fixtureReport["trial"]=JObject.FromObject(new {resident=person,player=message,topic,score,before,after=relation.friendship,
                        branch,reply,applied=action!=null,provider=(string)answer["provider"],model=(string)answer["model"],
                        visible=npcBubble.Visible&&npcBubble.AnchoredTo(npc.transform),elapsedSeconds=Time.realtimeSinceStartup-started});
                }catch(Exception e){fixtureReport["probeError"]=e.GetBaseException().Message;}
            }
        }
        static IEnumerator Capture(string filename,Action<bool> result)
        {
            yield return new WaitForEndOfFrame();
            var dir=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"MirthwoodEchoes","Diagnostics");
            Directory.CreateDirectory(dir);var path=Path.Combine(dir,"interface-"+Guid.NewGuid().ToString("N")+".png");
            ScreenCapture.CaptureScreenshot(path);
            float until=Time.realtimeSinceStartup+6;
            while(Time.realtimeSinceStartup<until&&(!File.Exists(path)||new FileInfo(path).Length<256))yield return null;
            bool took=File.Exists(path)&&new FileInfo(path).Length>=256;
            if(took){var last=Path.Combine(dir,filename);if(File.Exists(last))File.Replace(path,last,null);else File.Move(path,last);}
            result(took);
        }
        public IEnumerator Preview(string url,JObject report,bool live=false)
        {
            if(Visible||GameState.GetCurrentState(1)!=GameState.State.Gameplay)yield break;
            bool isolated=IsolatedGameRun.Active?.PathsMatch()==true;
            var player=PlayerCharacterController.Instance;
            var nearest=player==null?null:UnityEngine.Object.FindObjectsOfType<NPCController>()
                .Where(n=>n.CharacterInfo!=null&&n.isActiveAndEnabled&&OverheadSpeech.Board(n)!=null)
                .Where(n=>!isolated||live||CanProbeReplyWait(n))
                .OrderBy(n=>(n.transform.position-player.transform.position).sqrMagnitude).FirstOrDefault();
            if(nearest==null)yield break;
            var start=player.transform.position;
            bool approach=isolated&&!live&&!InReplyRange(nearest);
            try{
                if(approach){
                    player.transform.position=nearest.transform.position+Vector3.left*1.5f;
                    yield return new WaitForSecondsRealtime(.8f);
                }
                var preview=PreviewResident(url,report,live,isolated,player,nearest);
                if(isolated)yield return CheckedProbe(report,preview);
                else while(preview.MoveNext())yield return preview.Current;
            }finally{if(approach&&player)player.transform.position=start;}
        }
        IEnumerator PreviewResident(string url,JObject report,bool live,bool isolated,PlayerCharacterController player,NPCController nearest)
        {
            if(live){
                if(!isolated||!IsolatedGameRun.Active.PathsMatch()||!Build(nearest))yield break;
                int liveEpoch=generation;
                fixtureUrl=url;fixtureReport=report;campaign="isolated-provider";actor=person;ready=true;
                try{
                    yield return null;yield return null;
                    playerBubble.Input.text="When work is done, would you rather fish or play chess?";
                    playerBubble.Input.MoveTextEnd(false);
                    if(!IsolatedGameRun.PostReturn()){report["probeError"]="Return did not reach the isolated game window.";yield break;}
                    float liveDeadline=Time.realtimeSinceStartup+88;
                    while(liveEpoch==generation && report["trial"]==null && report["probeError"]==null && Time.realtimeSinceStartup<liveDeadline)yield return null;
                    if(report["trial"]!=null)yield return Capture("latest-provider-talk.png",ok=>report["capture"]=ok);
                }finally{if(liveEpoch==generation)Close();}
                yield break;
            }
            if(isolated){
                OverheadSpeech.Board(nearest).ShowOverheadText("The mill is open.",null);
                yield return new WaitForSecondsRealtime(1.3f);
                yield return Capture("latest-native-overhead.png",ok=>report["referenceCapture"]=ok);
            }
            float originalSpeed=Time.timeScale;
            if(!Build(nearest))yield break;
            int epoch=generation;report["speaker"]=person;fixtureUrl=url;fixtureReport=report;campaign="fixture";actor="resident";ready=true;
            var input=playerBubble.Input;
            yield return null;yield return null;
            report["playerAnchor"]=playerBubble.AnchoredTo(player.transform)&&playerBubble.Visible&&!npcBubble.Visible;
            report["focused"]=input.isFocused;
            if(isolated)report["contextSuppressed"]=contextPrompts.Length>0&&contextPrompts.All(c=>!c.enabled);
            if(isolated){
                report["playerTracks"]=playerBubble.ChecksTracking();
                var clock=_Mirthwood.Scripts.Demo.Weather.DayNightCycle.Instance;float day=clock.Now;float scaled=Time.time;
                yield return new WaitForSecondsRealtime(.7f);
                report["clockPaused"]=GameState.IsPaused&&Time.timeScale==0&&day==clock.Now&&scaled==Time.time;
                input.text="Hi";var small=playerBubble.Size;
                input.text="The flour is ready by the mill";var wide=playerBubble.Size;
                input.text=string.Join(" ",Enumerable.Repeat("The sacks are ready by the mill.",5));
                report["grows"]=wide.x>small.x&&playerBubble.Size.y>wide.y;
                input.MoveTextEnd(false);input.ProcessEvent(new Event{type=EventType.KeyDown,character='!'});input.ForceLabelUpdate();
                yield return null;yield return new WaitForEndOfFrame();
                report["longDraftCaret"]=playerBubble.DraftEndInView();
                report["editorEdges"]=playerBubble.ChecksEdges()&&playerBubble.FitsScreen();
                var narrow=new string('i',37);input.text=narrow;input.ForceLabelUpdate();input.textComponent.ForceMeshUpdate(false,true);
                var chars=input.textComponent.textInfo.characterInfo;
                report["wrapped"]=input.text==narrow&&input.textComponent.GetParsedText().StartsWith(new string('i',36)+"\ni")&&chars[36].stringLength==0&&chars[37].index==36;
                input.stringPosition=37;input.ProcessEvent(Event.KeyboardEvent("backspace"));report["editAfterWrap"]=input.text==new string('i',36);
                input.text="<b>plain</b> *bows*";input.ForceLabelUpdate();report["literalTags"]=input.textComponent.GetParsedText().Contains("<b>plain</b>")&&playerBubble.Styled("plain",false);
                input.text="Hello *bows*. Morning.";input.stringPosition=9;input.ProcessEvent(new Event{type=EventType.KeyDown,character='X'});
                report["middleEdit"]=input.text=="Hello *boXws*. Morning.";
            }
            const string line="*I lift my hat.* I’ll collect the flour. Is the mill open?";
            input.text=line;input.stringPosition=line.Length;input.ForceLabelUpdate();
            report["draftItalic"]=playerBubble.Styled("lift",true)&&playerBubble.Styled("collect",false);
            yield return null;yield return new WaitForEndOfFrame();report["shortDraftVisible"]=playerBubble.EntireDraftInView()&&playerBubble.CaretAligned;
            if(isolated)yield return Capture("latest-player-draft.png",ok=>report["draftCapture"]=ok);
            if(isolated&&IsolatedGameRun.PostReturn()){
                float timeout=Time.realtimeSinceStartup+2;
                while(phase==Phase.Draft&&Time.realtimeSinceStartup<timeout)yield return null;
                report["windowEnter"]=phase!=Phase.Draft;
            }
            if(phase==Phase.Draft)input.onSubmit.Invoke(input.text);
            if(isolated)yield return ObserveReplyWait(nearest,report,epoch);
            float until=Time.realtimeSinceStartup+13;
            while(epoch==generation&&Visible&&busy&&Time.realtimeSinceStartup<until)yield return null;
            if(epoch!=generation||!Visible){report["probeError"]="Talk closed before its reply could be inspected.";yield break;}
            report["resumeSpeed"]=!GameState.IsPaused&&Mathf.Approximately(Time.timeScale,originalSpeed);
            report["font"]=npcBubble.FontName;report["sprite"]=npcBubble.SpriteName;report["nativeStyle"]=npcBubble.UsesNativeStyle&&playerBubble.UsesNativeStyle;
            report["onScreen"]=npcBubble.FitsScreen();report["displayed"]=npcBubble.VisibleText;
            report["replyItalic"]=npcBubble.Styled("nod",true)&&npcBubble.Styled("mill",false);
            yield return Capture("latest-interface.png",ok=>report["capture"]=ok);
            if(isolated&&phase==Phase.Reading){
                float before=_Mirthwood.Scripts.Demo.Weather.DayNightCycle.Instance.Now;
                yield return new WaitForSecondsRealtime(.65f);
                report["clockAdvanced"]=_Mirthwood.Scripts.Demo.Weather.DayNightCycle.Instance.Now>before;
                report["tracksSpeaker"]=npcBubble.ChecksTracking();
                npcBubble.Show(string.Join(" ",Enumerable.Repeat("The mill wheel turns beside the willow while we wait for the rain to pass.",14)));
                report["pages"]=npcBubble.Pages;Page(1);report["nextPage"]=npcBubble.Page;Page(-1);report["backPage"]=npcBubble.Page;
                report["edgeFit"]=npcBubble.ChecksEdges()&&npcBubble.FitsScreen();
                Edit("");report["nextDraftPaused"]=GameState.IsPaused&&Time.timeScale==0;
                playerBubble.Input.text=string.Join(" ",Enumerable.Repeat("I remember the mill wheel and the rain.",10));
                Follow();
                report["separateBubbles"]=npcBubble.Visible&&playerBubble.Editing&&playerBubble.FitsScreen()&&npcBubble.FitsScreen()&&!playerBubble.Footprint.Overlaps(npcBubble.Footprint);
            }
            var prompts=contextPrompts;
            if(epoch==generation)Close();
            if(isolated)report["contextRestored"]=prompts.All(c=>c!=null&&c.enabled);
            report["closeRestoresClock"]=!GameState.IsPaused&&Mathf.Approximately(Time.timeScale,originalSpeed);
            if(isolated&&!Visible){
                yield return CheckWaitCancellation(nearest,url,line,report);
                bool oldInput=GameState.AllowInput;var origin=player.transform.position;
                bool moved=(nearest.transform.position-origin).sqrMagnitude>23;
                try{
                    if(moved)player.transform.position=nearest.transform.position+Vector3.left*1.5f;
                    GameState.SetTimeScale(.5f);ChooseTalkInCopy(nearest);
                    float wait=Time.realtimeSinceStartup+2;
                    while(!Visible&&Time.realtimeSinceStartup<wait)yield return null;
                    report["talkKey"]=Visible&&phase==Phase.Draft&&GameState.IsPaused;
                    IsolatedGameRun.PostTalkOrEscape(false,true);yield return new WaitForSecondsRealtime(.12f);
                    IsolatedGameRun.PostTalkOrEscape(false,false);yield return null;
                    report["escapeRestored"]=!Visible&&!GameState.IsPaused&&Mathf.Approximately(Time.timeScale,.5f);
                    if(Visible)Close();
                    if(Build(nearest)){GameState.Pause();Close();report["otherPausePreserved"]=GameState.IsPaused&&Time.timeScale==0;}
                    if(Build(nearest)){Close();report["alreadyPaused"]=GameState.IsPaused&&Time.timeScale==0;}
                }finally{
                    if(Visible)Close();
                    GameState.UnPause(oldInput);GameState.SetTimeScale(originalSpeed);
                    if(moved)player.transform.position=origin;
                }
            }
        }
        public void Close()
        {
            waitingDots=0;
            DismissPassingLine();
            if(!Visible)return;generation++;if(active!=null){active.Abort();active=null;}
            speech?.Stop();speech=null;
            freshCue=null;NativeCues.Clear(resident);InteractionHooks.DismissGiftRemark(resident);
            DraftPause.End();playerBubble?.Dispose();npcBubble?.Dispose();
            foreach(var prompt in contextPrompts)if(prompt!=null)prompt.enabled=true;
            contextPrompts=Array.Empty<Canvas>();
            if(controlsGame){
                if(GameState.GetCurrentState(1)==GameState.State.UI)GameState.SetCurrentState(1,previousState,false);
                Cursor.lockState=cursorLock;Cursor.visible=cursorVisible;EventSystem.current?.SetSelectedGameObject(previousSelection);
            }
            playerBubble=npcBubble=null;controlsGame=ready=busy=resolving=false;resident=null;exchange=null;accepted=null;targetIdentity=null;fixtureUrl=null;fixtureReport=null;UnityEngine.Object.Destroy(root);root=null;
        }
    }
}
