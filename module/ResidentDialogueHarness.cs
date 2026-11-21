using System;
using System.Collections;
using System.Linq;
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
        internal IEnumerator RunResidentDialogue(JObject report)
        {
            if(!InCopy()||IsolatedGameRun.Active.PrivateDiagnostics!=true)yield break;
            yield return CheckedProbe(report,AverilCases(report));
        }

        IEnumerator AverilCases(JObject report)
        {
            yield return new WaitForSecondsRealtime(3);
            var player=PlayerCharacterController.Instance;
            var npcs=UnityEngine.Object.FindObjectsOfType<NPCController>();
            var npc=npcs.Single(n=>n.isActiveAndEnabled&&n.CharacterInfo?.name=="Averil"&&Plugin.Owner(n)!="");
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
            try{
                PlaceResident(player.transform.position+Vector3.right*.8f);
                string[] messages={
                    "I miss my brother. We used to mend fishing nets together. What would you do with his old net?",
                    "It's the quiet by the water that I miss most. Could we sit with that a moment?",
                    "Let's talk about something lighter. Tell me a joke about the tavern cook."
                };
                foreach(string words in messages){
                    bool opening=!Visible;
                    if(opening){
                        PlaceResident(player.transform.position+Vector3.right*.8f);
                        var world=Plugin.World();
                        using(var check=Post(Address+"module/resolve",new{world,owner=Plugin.Owner(npc),nativeId=npc.CharacterInfo.id})){
                            yield return check.SendWebRequest();
                            if(check.responseCode!=200)throw new InvalidOperationException("Could not resolve Averil: "+Error(check,"HTTP "+check.responseCode));
                        }
                        GameState.SetCurrentGeneralState(GameState.State.Gameplay);GameState.SetCurrentState(1,GameState.State.Gameplay);
                        GameState.AllowInput=true;GameState.UnPause();
                        ChooseTalkInCopy(npc);
                        if(!Visible)throw new InvalidOperationException("Native Talk did not open: chatEnabled="+brain.chatEnabled+
                            ", scripted Speak="+(brain.GetDialogueData()!=null)+", game state="+_Mirthwood.Scripts.Demo.GameState.GetCurrentState(1)+
                            ", camera="+Camera.main?.name+", in range="+InReplyRange(npc));
                    }
                    yield return Await(()=>Ready&&phase==Phase.Draft,"Averil's ordinary Talk could not resolve its pending exchange.",15);
                    string c=campaign,a=actor;var box=exchange;float start=Time.realtimeSinceStartup;
                    playerBubble.Input.text=words;playerBubble.Input.onSubmit.Invoke(words);
                    var intent=box.Pending();string id=(string)intent["id"];
                    yield return Await(()=>phase==Phase.Reading||(!busy&&phase==Phase.Draft),"No answer to "+words);
                    if(phase!=Phase.Reading)throw new InvalidOperationException("Averil returned to editing before speaking: "+words);
                    int firstMs=Mathf.RoundToInt(1000*(Time.realtimeSinceStartup-start));
                    yield return Await(()=>!busy&&Visible&&waitingDots==0,"The final receipt did not arrive.");
                    int finishMs=Mathf.RoundToInt(1000*(Time.realtimeSinceStartup-start));
                    using(var req=Post(Address+"dialogue/prepare",new{id,campaign=c,actor=a,text=words,context=intent["context"]})){
                        yield return req.SendWebRequest();var packet=JObject.Parse(req.downloadHandler.text);
                        string reply=(string)packet["reply"];
                        bool matched=(string)packet["state"]=="complete"&&reply!=null&&box.Pending()==null&&
                            npcBubble.ReadRenderedText()==new BubbleText(reply,false).Plain.Trim();
                        var row=JObject.FromObject(new{id,campaign=c,actor=a,player=words,reply,topic=packet["topic"],handoff=packet["handoff"],
                            branch=packet["reaction"],context=intent["context"],matched,firstMs,finishMs,opened=opening});
                        turns.Add(row);
                        yield return Capture("latest-averil-turn-"+turns.Count+".png",ok=>row["capture"]=ok);
                        if(!matched)throw new InvalidOperationException("Averil’s speech differs from the recovered turn.");
                    }
                    Advance();
                }
                Close();
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
                    yield return Capture("latest-averil-opener-"+openers.Count+".png",ok=>row["capture"]=ok);
                    ReplyToPassingLine();
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
                Close();PlaceResident(origin);
            }
        }
    }
}
