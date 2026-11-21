using System;
using System.Collections;
using System.IO;
using System.Linq;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.AI;
using _Mirthwood.Scripts.Demo;
using _Mirthwood.Scripts.Demo.Chat;
using _Mirthwood.Scripts.Demo.Combat;

namespace Mirthwood.Echoes
{
    internal sealed partial class NativeConversation
    {
        internal IEnumerator RunResidentDialogue(JObject report,string mode)
        {
            if(!InCopy()||IsolatedGameRun.Active.PrivateDiagnostics!=true)yield break;
            yield return CheckedProbe(report,ResidentCases(report,mode));
        }

        // These hooks are installed only during the private replay of recorded native cards.
        static NPCChatController replaySpeaker;
        static string replayLeisureCue;
        static bool PickReplayCard(NPCChatController __instance,ChatController other)
        {
            if(replayLeisureCue==null||__instance!=replaySpeaker||!(other is PlayerChat))return true;
            __instance.SendInteraction(__instance.dnaData.chatActions.Single(card=>card.name=="Leisure"));
            return false;
        }
        static void SetReplayCue(NPCController resident,JObject __result)
        {
            if(replayLeisureCue!=null&&replaySpeaker.Character==resident&&__result?["Leisure"]!=null)
                __result["Leisure"]=replayLeisureCue;
        }

        IEnumerator ReadResidentPages(OverheadSpeech bubble,string line,JObject row,string filename,bool opener)
        {
            string words=new BubbleText(line,false).Plain;
            var pages=new JArray();row["visiblePages"]=pages;
            int count=bubble.Pages;
            for(int page=1;page<=count;page++){
                yield return new WaitForEndOfFrame();
                if(!bubble.Visible||bubble.Page!=page)throw new InvalidOperationException("The resident's next page was not displayed.");
                pages.Add(bubble.ReadPageInk());
                if(filename!=null){
                    bool captured=false;
                    yield return Capture(filename+(page==1?"":"-page-"+page)+".png",ok=>captured=ok);
                    if(!captured)throw new InvalidOperationException("Could not capture the resident's visible page.");
                }
                if(page==count)break;
                if(!opener)Page(1);
                else if(page==1)AdvancePassingLine();
                else{
                    passing.Expires=Time.unscaledTime;
                    yield return Await(()=>passing!=null&&bubble.Page==page+1,"Averil's passing remark ended before the next page.");
                }
            }
            row["capture"]=filename!=null;
            if(string.Concat(pages.Values<string>())!=string.Concat(words.Where(c=>!char.IsWhiteSpace(c))))
                throw new InvalidOperationException("Some of the resident's words were missing from the visible pages.");
        }

        IEnumerator ResidentCases(JObject report,string mode)
        {
            yield return new WaitForSecondsRealtime(3);
            bool topicFlow=mode=="dialogueflow",interactive=mode=="roleplay";
            string folder=IsolatedGameRun.Active.Root;
            string name=interactive?File.ReadAllText(Path.Combine(folder,"resident.txt")).Trim():"Averil";
            var player=PlayerCharacterController.Instance;
            var npcs=UnityEngine.Object.FindObjectsOfType<NPCController>();
            var npc=npcs.SingleOrDefault(n=>n.isActiveAndEnabled&&n.CharacterInfo?.name==name&&Plugin.Owner(n)!="");
            if(npc==null)throw new InvalidOperationException("Resident unavailable. Loaded: "+string.Join(", ",npcs.Where(n=>n.isActiveAndEnabled&&Plugin.Owner(n)!="").Select(n=>n.CharacterInfo?.name)));
            var brain=npc.GetComponent<NPCChatController>();
            var chat=player.GetComponent<PlayerChat>();
            var origin=npc.transform.position;
            var agent=npc.GetComponent<NavMeshAgent>();
            void PlaceResident(Vector3 position)
            {
                if(!agent||!agent.isActiveAndEnabled||!agent.isOnNavMesh||!agent.Warp(position))npc.transform.position=position;
            }
            var turns=new JArray();var openers=new JArray();
            report["resident"]=npc.CharacterInfo.name;report["turns"]=turns;report["openers"]=openers;
            var replay=topicFlow?new Harmony(Plugin.Id+".topic-flow-test"):null;
            float originalDesire=npc.CharacterInfo.personality.values[5];
            string startingDesire=Path.Combine(folder,"starting-desire.txt");
            bool setDesire=interactive&&File.Exists(startingDesire);
            try{
                if(setDesire){
                    string condition=File.ReadAllText(startingDesire).Trim();
                    if(condition!="tired"&&condition!="eager")throw new InvalidDataException("Unknown initial social desire.");
                    npc.CharacterInfo.personality.values[5]=condition=="eager"?1f:-1f;
                    report["startingDesire"]=condition;
                }
                PlaceResident(player.transform.position+Vector3.right*.8f);
                if(replay!=null){
                    replaySpeaker=brain;
                    replay.Patch(AccessTools.Method(typeof(NPCChatController),"PickResponseChatAction"),prefix:new HarmonyMethod(typeof(NativeConversation),nameof(PickReplayCard)));
                    replay.Patch(AccessTools.Method(typeof(NativeSocialTurn),nameof(NativeSocialTurn.ReturnCues)),postfix:new HarmonyMethod(typeof(NativeConversation),nameof(SetReplayCue)));
                }
                string[] messages=topicFlow?new[]{
                    "Evening, Averil. How do you like to spend a quiet evening?",
                    "What is it about chess that keeps you at the board?",
                    "I enjoy a game myself. What else would you like to try on a free afternoon?",
                    "What would you hope to find inside one of those ruins?",
                    "Speaking of tomorrow, do you think the fields could use a little rain?",
                    "Back to chess for a moment. If your opponent left a rook unguarded, would you take it at once?"
                }:new[]{
                    "I miss my brother. We used to mend fishing nets together. What would you do with his old net?",
                    "It's the quiet by the water that I miss most. Could we sit with that a moment?",
                    "Let's talk about something lighter. Tell me a joke about the tavern cook."
                };
                for(int i=0;interactive||i<messages.Length;i++){
                    string words=interactive?null:messages[i];
                    if(topicFlow){
                        switch(turns.Count){
                            case 0: case 1: case 4: replayLeisureCue="Averil talks about playing chess";break;
                            case 2: replayLeisureCue="Averil talks about exploring old ruins";break;
                            default: replayLeisureCue=null;break;
                        }
                        if(replayLeisureCue!=null)npc.CharacterInfo.personality.values[5]=1f;
                    }
                    bool opening=!Visible;
                    if(opening){
                        PlaceResident(player.transform.position+Vector3.right*.8f);
                        var world=Plugin.World();
                        using(var check=Post(Address+"module/resolve",new{world,owner=Plugin.Owner(npc),nativeId=npc.CharacterInfo.id})){
                            yield return check.SendWebRequest();
                            if(check.responseCode!=200)throw new InvalidOperationException("Could not resolve "+name+": "+Error(check,"HTTP "+check.responseCode));
                        }
                        GameState.SetCurrentGeneralState(GameState.State.Gameplay);GameState.SetCurrentState(1,GameState.State.Gameplay);
                        GameState.AllowInput=true;GameState.UnPause();
                        ChooseTalkInCopy(npc);
                        if(!Visible)throw new InvalidOperationException("Native Talk did not open: chatEnabled="+brain.chatEnabled+
                            ", scripted Speak="+(brain.GetDialogueData()!=null)+", game state="+_Mirthwood.Scripts.Demo.GameState.GetCurrentState(1)+
                            ", camera="+Camera.main?.name+", in range="+InReplyRange(npc));
                    }
                    yield return Await(()=>Ready&&phase==Phase.Draft,"Ordinary Talk could not resolve its pending exchange.",15);
                    if(interactive){
                        string draft=Path.Combine(folder,"draft-"+i+".txt");
                        File.WriteAllText(Path.Combine(folder,"ready-"+i),name);
                        yield return Await(()=>File.Exists(draft),"No input for the copied-save playtest.",240);
                        words=File.ReadAllText(draft);
                        if(words.Length==0){Close();break;}
                    }
                    string c=campaign,a=actor;var box=exchange;float start=Time.realtimeSinceStartup;
                    playerBubble.Input.text=words;
                    if(interactive){
                        yield return new WaitForEndOfFrame();
                        if(!IsolatedGameRun.PostReturn())throw new IOException("Could not send Enter to the game window.");
                        yield return Await(()=>box.Pending()!=null,"Enter did not submit this draft.",6);
                    }else playerBubble.Input.onSubmit.Invoke(words);
                    var intent=box.Pending();string id=(string)intent["id"];
                    yield return Await(()=>phase==Phase.Reading||(!busy&&phase==Phase.Draft),"No answer to "+words);
                    if(phase!=Phase.Reading)throw new InvalidOperationException("The resident returned to editing before speaking: "+words);
                    int firstMs=Mathf.RoundToInt(1000*(Time.realtimeSinceStartup-start));
                    yield return Await(()=>!busy&&Visible&&waitingDots==0,"The final receipt did not arrive.");
                    int finishMs=Mathf.RoundToInt(1000*(Time.realtimeSinceStartup-start));
                    using(var req=Post(Address+"dialogue/prepare",new{id,campaign=c,actor=a,text=words,context=intent["context"]})){
                        yield return req.SendWebRequest();var packet=JObject.Parse(req.downloadHandler.text);
                        string reply=(string)packet["reply"];
                        bool matched=(string)packet["state"]=="complete"&&reply!=null&&box.Pending()==null&&
                            npcBubble.ReadRenderedText()==new BubbleText(reply,false).Plain.Trim();
                        var row=JObject.FromObject(new{id,campaign=c,actor=a,player=words,reply,topic=packet["topic"],handoff=packet["handoff"],
                            branch=packet["reaction"],context=intent["context"],matched,firstMs,finishMs,opened=opening,
                            playerLeaves=(bool?)packet["playerLeaves"]==true,closed=conversationOver});
                        if(replayLeisureCue!=null){
                            row["replayedCue"]=replayLeisureCue;
                            if((string)row["handoff"]?["cue"]!=replayLeisureCue)
                                throw new InvalidOperationException("The recorded Leisure card did not reach this turn.");
                        }
                        turns.Add(row);
                        string filename=interactive?(i==0||conversationOver?"latest-roleplay-turn-"+turns.Count:null):"latest-averil-turn-"+turns.Count;
                        yield return ReadResidentPages(npcBubble,reply,row,filename,false);
                        if(!matched)throw new InvalidOperationException("The on-screen speech differs from the recovered turn.");
                        if(interactive){
                            string output=Path.Combine(folder,"reply-"+i+".json");
                            File.WriteAllText(output+".tmp",row.ToString());File.Move(output+".tmp",output);
                        }
                    }
                    bool ended=conversationOver;
                    if(interactive){
                        yield return new WaitForEndOfFrame();
                        if(!IsolatedGameRun.PostReturn())throw new IOException("Could not accept the resident's reply.");
                        yield return Await(()=>ended?!Visible:Visible&&phase==Phase.Draft,"Enter did not reach the expected state after the reply.",6);
                        if(ended)break;
                    }else Advance();
                }
                Close();
                if(topicFlow||interactive)yield break;
                foreach(string card in new[]{"Funny Joke","Flirt"}){
                    var native=brain.dnaData.chatActions.Single(x=>string.Equals(x.name.Replace(" ",""),card.Replace(" ",""),StringComparison.OrdinalIgnoreCase));
                    PlaceResident(player.transform.position+Vector3.right*.8f);
                    brain.PrepareForChat(chat);chat.PrepareForChat(brain);brain.SendInteraction(native);
                    float until=Time.realtimeSinceStartup+80;
                    while(passing?.Heard!=true&&Time.realtimeSinceStartup<until){
                        if(!InReplyRange(npc))PlaceResident(player.transform.position+Vector3.right*.8f);
                        yield return null;
                    }
                    if(passing?.Speaker!=npc||passing.Kind!="address"||passing.EventId==null)
                        throw new InvalidOperationException("Averil's native "+card+" did not produce a spoken opener.");
                    var remark=passing;string line=remark.Line,id=remark.EventId;
                    bool matched=remark.Bubble.ReadRenderedText()==new BubbleText(line,false).Plain.Trim();
                    var row=JObject.FromObject(new{card,id,line,matched});openers.Add(row);
                    yield return ReadResidentPages(remark.Bubble,line,row,"latest-averil-opener-"+openers.Count,true);
                    AdvancePassingLine();
                    yield return Await(()=>Ready,"The native opener could not be answered.");
                    row["campaign"]=campaign;row["actor"]=actor;
                    row["matched"]=matched&&npcBubble.ReadRenderedText()==new BubbleText(line,false).Plain.Trim()&&
                        (string)freshCue?["event"]==id&&(string)freshCue?["remark"]?["text"]==line;
                    Close();
                }
                yield return Await(()=>NativeCues.Pending==0,"The heard openers did not reach the journal.",20);
                var witness=npcs.First(n=>n&&n!=npc&&n.isActiveAndEnabled&&Plugin.Owner(n)!=""&&n.GetComponent<NPCChatController>()!=null);
                var other=witness.GetComponent<NPCChatController>();
                var small=brain.dnaData.chatActions.First(x=>x.name=="Small Talk");
                int ticket=addressRequest;
                var previousTrace=NativeCues.Trace;var events=new JArray();
                try{
                    NativeCues.Trace=events;
                    brain.PrepareForChat(other);other.PrepareForChat(brain);brain.SendInteraction(small);
                    yield return Await(()=>events.Any(e=>(string)e["kind"]=="social"&&e["people"] is JArray pair&&
                        pair.Any(p=>(string)p["owner"]==Plugin.Owner(npc))&&pair.Any(p=>(string)p["owner"]==Plugin.Owner(witness))),
                        "The two residents did not exchange a native card.",15);
                    report["backgroundNative"]=addressRequest==ticket&&passing==null;
                }finally{NativeCues.Trace=previousTrace;}
            }finally{
                if(replay!=null){replayLeisureCue=null;replaySpeaker=null;replay.UnpatchSelf();}
                if(replay!=null||setDesire)npc.CharacterInfo.personality.values[5]=originalDesire;
                Close();PlaceResident(origin);
            }
        }
    }
}
