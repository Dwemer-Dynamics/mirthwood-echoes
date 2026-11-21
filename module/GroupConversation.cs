using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using Newtonsoft.Json.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI.Extensions;
using _Mirthwood.Scripts.Demo;
using _Mirthwood.Scripts.Demo.Chat;
using _Mirthwood.Scripts.Demo.Combat;
using _Mirthwood.Scripts.Demo.Menus;
using _Mirthwood.Scripts.Demo.Stories;

namespace Mirthwood.Echoes
{
    internal sealed partial class NativeConversation
    {
        GroupGathering gathering;
        GroupGathering.Guest departingGuest;
        readonly Dictionary<string,int> groupTurns=new Dictionary<string,int>();
        readonly Dictionary<string,float> groupRuns=new Dictionary<string,float>();
        readonly Dictionary<string,string> groupCues=new Dictionary<string,string>();
        string groupCampaign,groupLastId,groupLastSpeaker,groupTo;
        string invitedAnswer;
        int groupSequence,groupRevision,groupReplies;
        float nextGroupReply;
        bool groupReady,groupTyping,groupParting;
        Action<string> groupProbe;
        static readonly System.Reflection.FieldInfo StoryCard=HarmonyLib.AccessTools.Field(typeof(HUDMenuController),"eventCardView");
        static bool StoryCardOpen=>Menu<HUDMenuController>.Instance is HUDMenuController hud&&
            StoryCard?.GetValue(hud) is EventCardView card&&card.isActiveAndEnabled;
        internal bool GroupActive=>gathering!=null;

        internal void GroupKeys(KeyboardShortcut key)
        {
            if(gathering==null){
                bool down=Input.GetKeyDown(key.MainKey)&&key.Modifiers.All(Input.GetKey);
                if(groupProbe!=null&&down)groupProbe("Key received, visible="+Visible+", gameplay="+GroupMayStart());
                if(down&&!Visible&&GroupMayStart())StartGroup();
                return;
            }
            var player=PlayerCharacterController.Instance;
            if(StoryCardOpen){
                controlsGame=false;Close();
                GameState.SetCurrentState(1,GameState.State.UI,false);GameState.Pause();return;
            }
            if(!player||player.DataContainer.data.InCombat||NativeCues.Session!=gathering.PlaySession||
                (player.transform.position-gathering.Origin).sqrMagnitude>25f){Close();return;}
            if(Input.GetKeyDown(KeyCode.Escape)){
                if(groupTyping&&!string.IsNullOrWhiteSpace(playerBubble.Input.text)){
                    groupTyping=false;DraftPause.End();GameState.SetCurrentState(1,GameState.State.Gameplay,false);
                    playerBubble.Show("");playerBubble.SetVisible(false);phase=Phase.Reading;phaseFrame=Time.frameCount;
                    npcBubble.Note("Enter to speak   •   Tab to listen   •   Esc to leave");
                }else Close();
                return;
            }
            var state=GameState.GetCurrentState(1);
            if(state!=GameState.State.Gameplay&&!(groupTyping&&state==GameState.State.UI)){
                bool paused=GameState.IsPaused;controlsGame=false;Close();
                if(paused&&!GameState.IsPaused)GameState.Pause();
                return;
            }
            gathering.Tick();
            foreach(var g in gathering.Guests.ToArray())if(g.Actor==null&&!g.Resolving)host.StartCoroutine(ResolveGuest(g,gathering));
            var seated=Participants();
            if(gathering.Guests.Count<2||groupReady&&seated.Length<2){
                bool stillGathering=!groupReady;
                groupProbe?.Invoke("Gathering ended with "+gathering.Guests.Count+" candidates and "+seated.Length+" seated.");
                Close();
                if(stillGathering)host.StartCoroutine(GroupNotice("Too few residents could join the conversation."));
                return;
            }
            if(!groupReady){
                if(seated.Length<2)return;
                if(!Build(seated[0].Npc)){Close();return;}
                groupReady=groupTyping=true;ready=true;playerBubble.Note("Enter to speak   •   Esc to leave");
                return;
            }
            if(!seated.Any(g=>g.Npc==resident)){
                waitingDots=0;
                SelectSpeaker(seated[0]);
            }
            if(departingGuest!=null&&seated.Contains(departingGuest)&&seated.Length==2){
                groupParting=true;departingGuest=null;npcBubble.Note("Enter to leave");
            }
            if(groupTyping){
                if(Input.GetKeyDown(KeyCode.Tab)&&string.IsNullOrWhiteSpace(playerBubble.Input.text)){
                    groupTyping=false;DraftPause.End();GameState.SetCurrentState(1,GameState.State.Gameplay,false);
                    playerBubble.Show("");playerBubble.SetVisible(false);BeginGroupReply();
                }
                return;
            }
            if(Time.frameCount==phaseFrame)return;
            if(groupParting){
                if(Input.GetKeyDown(KeyCode.Return)||Input.GetKeyDown(KeyCode.KeypadEnter))Close();
                return;
            }
            if(key.IsDown()){gathering.Invite();return;}
            if(Input.GetKeyDown(KeyCode.Return)||Input.GetKeyDown(KeyCode.KeypadEnter)){WriteToGroup();return;}
            bool listen=Input.GetKeyDown(KeyCode.Tab);
            if(!busy&&(listen||groupReplies<2&&nextGroupReply>0&&Time.unscaledTime>=nextGroupReply&&speech?.Busy!=true)){
                if(listen&&groupReplies>=2)groupReplies=0;
                nextGroupReply=0;BeginGroupReply();
            }
        }

        static bool GroupMayStart()
        {
            var selected=EventSystem.current?.currentSelectedGameObject;
            return InteractionHooks.Compatible&&Plugin.World()!=null&&!StoryCardOpen&&!PlayerCharacterController.Instance.DataContainer.data.InCombat&&GameState.GetCurrentState(1)==GameState.State.Gameplay&&GameState.AllowInput&&
                !GameState.IsPaused&&!(selected&&selected.GetComponentInParent<TMP_InputField>()?.isFocused==true)&&
                (IsolatedGameRun.Active==null||IsolatedGameRun.Active.TalkAddress!=null);
        }
        internal bool StartGroup()
        {
            if(GroupActive||Visible||!GroupMayStart())return false;
            DismissPassingLine();
            gathering=new GroupGathering(PlayerCharacterController.Instance.transform.position,groupProbe);
            gathering.Invite();
            groupProbe?.Invoke("Invited "+string.Join(", ",gathering.Guests.Select(g=>g.Npc.CharacterInfo.name)));
            if(gathering.Guests.Count<2){
                gathering.Close();gathering=null;host.StartCoroutine(GroupNotice("Two available residents are needed nearby."));return false;
            }
            return true;
        }
        IEnumerator GroupNotice(string message)
        {
            var template=UnityEngine.Object.FindObjectsOfType<NPCController>().FirstOrDefault(n=>n&&OverheadSpeech.Board(n));
            var player=PlayerCharacterController.Instance;if(!template||!player)yield break;
            var notice=SpeechSurface("Mirthwood Echoes • Group Talk",false);
            var bubble=OverheadSpeech.Create(template,(RectTransform)notice.transform,player);
            try{
                if(bubble==null)yield break;
                bubble.Show(message);
                float end=Time.unscaledTime+2.5f;
                while(Time.unscaledTime<end&&!Visible&&!GroupActive&&bubble.Follow())yield return null;
            }finally{bubble?.Dispose();if(notice)UnityEngine.Object.Destroy(notice);}
        }
        IEnumerator ResolveGuest(GroupGathering.Guest g,GroupGathering group)
        {
            g.Resolving=true;
            var identity=new {world=Plugin.World(),owner=Plugin.Owner(g.Npc),nativeId=g.Npc.CharacterInfo.id};
            using(var request=Post(Address+"module/resolve",identity)){
                request.timeout=6;yield return request.SendWebRequest();
                if(group!=gathering||!group.Guests.Contains(g))yield break;
                if(request.responseCode==200){
                    JObject result=null;
                    try{result=JObject.Parse(request.downloadHandler.text);}catch(Newtonsoft.Json.JsonException){}
                    string c=(string)result?["campaign"],actor=(string)result?["actor"];
                    if(!string.IsNullOrEmpty(c)&&!string.IsNullOrEmpty(actor)&&(groupCampaign==null||c==groupCampaign)){
                        groupCampaign=c;g.Actor=actor;yield break;
                    }
                }
                groupProbe?.Invoke("Could not resolve "+g.Npc.name+" ("+request.responseCode+").");
                group.Release(g);
            }
        }
        GroupGathering.Guest[] Participants()=>gathering.Guests.Where(g=>g.Arrived&&g.Actor!=null).ToArray();
        void WriteToGroup()
        {
            FinishDeparture();
            groupProbe?.Invoke("Player composing after line "+groupSequence);
            if(active!=null){active.Abort();active=null;}
            busy=false;nextGroupReply=0;groupReplies=0;groupTyping=true;
            GameState.SetCurrentState(1,GameState.State.UI,false);Edit("");groupRevision++;
            playerBubble.Note("Enter to speak   •   Esc to leave");
        }
        void SubmitGroup(string words)
        {
            if(!groupReady||!groupTyping)return;
            if(string.IsNullOrWhiteSpace(words)){host.StartCoroutine(Refocus(generation));return;}
            var seated=Participants();
            var named=seated.Where(g=>System.Text.RegularExpressions.Regex.IsMatch(words,
                @"\b"+System.Text.RegularExpressions.Regex.Escape(g.Npc.CharacterInfo.name)+@"\b",System.Text.RegularExpressions.RegexOptions.IgnoreCase)).ToArray();
            if(named.Length>1)named=named.Where(g=>System.Text.RegularExpressions.Regex.IsMatch(words.TrimStart(),
                "^"+System.Text.RegularExpressions.Regex.Escape(g.Npc.CharacterInfo.name)+@"\s*[,!?]",System.Text.RegularExpressions.RegexOptions.IgnoreCase)).ToArray();
            string addressed=named.Length==1?named[0].Actor:"group";
            // A player answering a resident's question gives that resident priority even without repeating their name.
            if(groupTo!="player"||System.Text.RegularExpressions.Regex.IsMatch(words,@"\b(both|everyone|anyone|either of you|all of you)\b",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase))invitedAnswer=null;
            string id=RecordGroupLine("player",addressed,words.Trim(),null,null);
            if(id==null){playerBubble.Note("The conversation could not be saved. Enter to retry.");host.StartCoroutine(Refocus(generation));return;}
            groupTyping=false;phaseFrame=Time.frameCount;DraftPause.End();GameState.SetCurrentState(1,GameState.State.Gameplay,false);
            groupProbe?.Invoke("Submitted player line "+groupSequence);
            playerBubble.Note("");playerBubble.Show(words.Trim());PlayerLine(id,words.Trim());groupReplies=0;BeginGroupReply();
        }
        string RecordGroupLine(string speaker,string to,string words,string promise,JObject priorPromise,JToken proposal=null)
        {
            var heard=Participants();
            if(heard.Length<2||speaker!="player"&&!heard.Any(g=>g.Actor==speaker)||
                to!="player"&&to!="group"&&!heard.Any(g=>g.Actor==to))return null;
            var line=new JObject{["session"]=gathering.Session,["seq"]=groupSequence+1,["speaker"]=speaker,["to"]=to,
                ["words"]=words,["at"]=DateTime.UtcNow.ToString("O"),["audience"]=new JArray(heard.Select(g=>g.Actor))};
            if(promise!=null)line["promise"]=promise;
            if(proposal!=null)line["proposal"]=proposal;
            if(priorPromise!=null)line["priorPromise"]=priorPromise;
            string id=NativeCues.Record("group",new JArray(),line:line);
            if(id!=null){groupSequence++;groupLastId=id;groupLastSpeaker=speaker;groupTo=to;}
            return id;
        }
        void SelectSpeaker(GroupGathering.Guest g)
        {
            if(resident!=g.Npc){
                npcBubble?.Dispose();resident=g.Npc;person=resident.CharacterInfo.name;
                npcBubble=OverheadSpeech.Create(resident,(RectTransform)root.transform);
            }
            foreach(var listener in Participants())GroupGathering.Face(listener.Npc,listener==g?
                PlayerCharacterController.Instance.transform.position:g.Npc.transform.position);
        }
        void BeginGroupReply()
        {
            if(!GroupActive||groupTyping||busy)return;
            FinishDeparture();
            var seated=Participants();if(seated.Length<2){Close();return;}
            var nextGuest=seated.FirstOrDefault(g=>g.Actor==groupTo)??seated.FirstOrDefault(g=>groupLastSpeaker=="player"&&g.Actor==invitedAnswer)??seated.Where(g=>g.Actor!=groupLastSpeaker)
                .OrderBy(g=>groupTurns.TryGetValue(g.Actor,out int count)?count:0).First();
            SelectSpeaker(nextGuest);
            if(npcBubble==null){Close();return;}
            busy=true;phase=Phase.Waiting;phaseFrame=Time.frameCount;waitStarted=Time.unscaledTime;waitingDots=1;npcBubble.Show(".");
            npcBubble.Note("Enter to speak   •   Esc to leave");
            host.StartCoroutine(GetGroupReply(nextGuest,gathering,++groupRevision));
        }
        IEnumerator GetGroupReply(GroupGathering.Guest guest,GroupGathering group,int revision)
        {
            groupCues.TryGetValue(guest.Actor,out var cue);
            string head=NativeCues.Head;
            var payload=new {campaign=groupCampaign,actor=guest.Actor,audience=Participants().Select(g=>g.Actor).ToArray(),
                session=group.Session,head=string.IsNullOrEmpty(head)?null:head,cue};
            JObject answer=null;
            float timeout=Time.unscaledTime+95;
            while(group==gathering&&revision==groupRevision){
                using(var request=Post(Address+"dialogue/group",payload)){
                    active=request;request.timeout=85;yield return request.SendWebRequest();
                    if(group!=gathering||revision!=groupRevision)yield break;
                    active=null;
                    if(request.responseCode==425&&Time.unscaledTime<timeout){yield return new WaitForSecondsRealtime(.25f);continue;}
                    if(request.responseCode==200){
                        try{answer=JObject.Parse(request.downloadHandler.text);}catch(Newtonsoft.Json.JsonException){}
                    }
                    if(answer==null){GroupRetry(Error(request,"The speaker lost their train of thought."));yield break;}
                }
                break;
            }
            if(group!=gathering||revision!=groupRevision)yield break;
            var seated=Participants();
            if(!seated.Contains(guest)){GroupRetry("The speaker has left.");yield break;}
            foreach(var g in seated)g.Unique=(bool?)answer["residents"]?.FirstOrDefault(x=>(string)x["key"]==g.Actor)?["uniqueNative"]==true;
            string addressed=(string)answer["to"],words=(string)answer["words"];
            if(string.IsNullOrWhiteSpace(words)){GroupRetry("The resident had nothing to say.");yield break;}
            if(addressed!="group"&&addressed!="player"&&!seated.Any(g=>g.Actor==addressed)){GroupRetry("The person they were addressing has left.");yield break;}
            var player=PlayerCharacterController.Instance;
            TalkResult native=null;
            if(groupLastSpeaker=="player"&&(groupTo=="group"||groupTo==guest.Actor)&&(string)answer["previous"]==groupLastId){
                groupRuns.TryGetValue(guest.Actor,out float run);
                native=GroupSocialTurn.Apply(group.Session,groupSequence,guest,player,(string)answer["receivedTopic"],
                    (int?)answer["positiveDesireCostPercent"]??100,run);
                if(native!=null)groupRuns[guest.Actor]=native.PositiveRun;
            }
            bool playerParting=groupLastSpeaker=="player"&&(string)answer["previous"]==groupLastId&&(bool?)answer["playerParting"]==true;
            bool leaves=(string)native?.Handoff?["kind"]=="ended";
            if(leaves){words=(string)answer["farewell"];if(string.IsNullOrWhiteSpace(words))words="I'll see you later.";addressed="player";}
            JObject prior=answer["previousPromise"]!=null&&groupLastId==(string)answer["previous"]?
                new JObject{["source"]=groupLastId,["quote"]=(string)answer["previousPromise"]}:null;
            if(prior!=null&&answer["previousProposal"]!=null)prior["proposal"]=answer["previousProposal"];
            string id=RecordGroupLine(guest.Actor,addressed,words,leaves?null:(string)answer["promise"],prior,leaves?null:answer["proposal"]);
            if(id==null){GroupRetry("The conversation could not be saved.");yield break;}
            groupParting=playerParting||leaves&&seated.Length==2;
            groupCues.Remove(guest.Actor);
            if(native?.Handoff!=null)groupCues[guest.Actor]=(string)native.Handoff["cue"];
            invitedAnswer=addressed=="player"?guest.Actor:null;
            var recipient=seated.FirstOrDefault(g=>g.Actor==addressed);
            if(recipient!=null&&guest.Unique)GroupSocialTurn.Apply(group.Session,groupSequence,recipient,guest.Npc,(string)answer["ownTopic"],100);
            foreach(var g in seated)GroupGathering.Face(g.Npc,g==guest?(recipient!=null?recipient.Npc.transform.position:player.transform.position):guest.Npc.transform.position);
            groupTurns[guest.Actor]=groupTurns.TryGetValue(guest.Actor,out int count)?count+1:1;
            busy=false;waitingDots=0;phase=Phase.Reading;phaseFrame=Time.frameCount;groupReplies++;
            playerBubble.SetVisible(false);npcBubble.Show(words);
            string hint=groupParting?"Enter to leave":"Enter to speak   •   Tab to listen   •   Esc to leave";
            npcBubble.Note(hint);speech.Say(id,guest.Npc,words,(heard,partial)=>{
                if(group==gathering&&groupRevision==revision&&groupLastId==id&&resident==guest.Npc){npcBubble.ShowSpoken(words,heard,partial);npcBubble.Note(hint);}
            },false);
            bool yieldToPlayer=addressed=="player"&&(words.Contains("?")||(bool?)answer["yieldToPlayer"]==true);
            nextGroupReply=leaves||groupParting||yieldToPlayer?0:Time.unscaledTime+Mathf.Clamp(words.Length/16f,2.7f,8f);
            if(leaves&&!groupParting){
                departingGuest=guest;
                float end=Time.unscaledTime+Mathf.Clamp(words.Length/16f,2.7f,8f);
                while(group==gathering&&departingGuest==guest&&(Time.unscaledTime<end||speech?.Busy==true))yield return null;
                if(group==gathering&&departingGuest==guest)FinishDeparture();
            }
        }
        void FinishDeparture()
        {
            var guest=departingGuest;
            departingGuest=null;
            if(guest!=null&&gathering.Guests.Contains(guest))gathering.Release(guest);
        }
        void GroupRetry(string reason)
        {
            groupProbe?.Invoke(reason);
            busy=false;waitingDots=0;phase=Phase.Reading;phaseFrame=Time.frameCount;nextGroupReply=0;
            npcBubble.Show("…");npcBubble.Note(reason+"  Tab to retry   •   Enter to speak");
        }
        void ReleaseGroup()
        {
            if(gathering==null)return;
            groupRevision++;gathering.Close();gathering=null;departingGuest=null;
            groupReady=groupTyping=groupParting=false;groupCampaign=groupLastId=groupLastSpeaker=groupTo=invitedAnswer=null;
            groupReplies=groupSequence=0;nextGroupReply=0;groupRuns.Clear();groupTurns.Clear();groupCues.Clear();
        }
    }
}
