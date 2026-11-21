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
using _Mirthwood.Scripts.Demo.Tests;
using _Mirthwood.Scripts.Demo.ScriptableObjects;
using _Mirthwood.Scripts.Demo.Stories;
using _Mirthwood.Scripts.Demo.GameModules;
using _Mirthwood.Scripts.Runtime.Demo.InputReading;
using Lowscope.Saving;

namespace Mirthwood.Echoes
{
    internal sealed partial class NativeConversation
    {
        internal IEnumerator RunGroupHarness(JObject report)
        {
            if(!InCopy()||IsolatedGameRun.Active.PrivateDiagnostics!=true)yield break;
            yield return CheckedProbe(report,GroupCases(report));
        }

        IEnumerator GroupCases(JObject report)
        {
            yield return new WaitForSecondsRealtime(3);
            var player=PlayerCharacterController.Instance;
            var controls=InputReader.Instance.GetGameInput().asset;
            var bindings=controls.actionMaps.SelectMany(map=>map.actions).SelectMany(action=>
                action.bindings.Select(binding=>new{action=action.name,map=action.actionMap.name,path=binding.effectivePath})).ToArray();
            var gBindings=bindings.Where(b=>string.Equals(b.path,"<Keyboard>/g",StringComparison.OrdinalIgnoreCase)).ToArray();
            Require(!bindings.Any(b=>string.Equals(b.path,"<Keyboard>/t",StringComparison.OrdinalIgnoreCase)),"T is already bound in this game's controls.");
            report["gBindings"]=new JArray(gBindings.Select(b=>b.map+"/"+b.action));
            var residents=UnityEngine.Object.FindObjectsOfType<NPCController>().Where(n=>n.isActiveAndEnabled&&n.CharacterInfo!=null&&Plugin.Owner(n)!="")
                .ToArray();
            var two=new[]{residents.Single(n=>n.CharacterInfo.name=="Averil"),residents.Single(n=>n.CharacterInfo.name=="Drystan")};
            var late=residents.First(n=>n.CharacterInfo.name=="Graham");
            var lateStart=late.transform.position;
            var previous=two.Select(n=>n.transform.position).ToArray();
            var playerStart=player.transform.position;
            var seen=new JArray();NativeCues.Trace=seen;
            var stages=new JArray();report["setupStages"]=stages;groupProbe=s=>stages.Add(s);
            try{
                yield return DismissGroupTestCards();
                GameState.SetCurrentGeneralState(GameState.State.Gameplay);GameState.SetCurrentState(1,GameState.State.Gameplay);
                GameState.AllowInput=true;GameState.UnPause();
                NavMeshHit center=default,left=default,right=default;bool found=false;
                var anchors=new[]{new Vector3(-145,.08f,-76),previous[0],previous[1],playerStart};
                foreach(var pivot in anchors.SelectMany(anchor=>Enumerable.Range(-4,9).SelectMany(x=>Enumerable.Range(-4,9)
                    .Select(z=>anchor+new Vector3(x*2,0,z*2))))){
                    if(!NavMesh.SamplePosition(pivot,out center,2f,NavMesh.AllAreas))continue;
                    if(!NavMesh.FindClosestEdge(center.position,out var edge,NavMesh.AllAreas)||edge.distance<3.8f||
                        residents.Any(n=>!two.Contains(n)&&(n.transform.position-center.position).sqrMagnitude<30f))continue;
                    if(NavMesh.SamplePosition(center.position+Vector3.left*3.2f,out left,.45f,NavMesh.AllAreas)&&
                        NavMesh.SamplePosition(center.position+Vector3.right*3.2f,out right,.45f,NavMesh.AllAreas)&&
                        GroupGathering.ClearSight(center.position,left.position)&&GroupGathering.ClearSight(center.position,right.position)&&
                        GroupGathering.ClearSight(center.position,center.position+Vector3.forward*3f)&&
                        !NavMesh.Raycast(center.position,left.position,out _,NavMesh.AllAreas)&&!NavMesh.Raycast(center.position,right.position,out _,NavMesh.AllAreas)){found=true;break;}
                }
                Require(found,"No open meeting place in the copied world.");
                yield return player.CoTeleport(center.position,0,0,false,false);
                yield return DismissGroupTestCards();
                GameState.SetCurrentGeneralState(GameState.State.Gameplay);GameState.SetCurrentState(1,GameState.State.Gameplay);
                GameState.AllowInput=true;GameState.UnPause();
                yield return null;
                stages.Add("Staging at "+center.position+", player "+player.transform.position);
                for(int i=0;i<2;i++){
                    var n=two[i];StageGuest(n,i==0?left.position:right.position);
                    n.CharacterInfo.personality.values[5]=1f;
                }
                Physics.SyncTransforms();
                Require(GroupMayStart(),"Isolated gameplay is not ready for Group Talk. State="+GameState.GetCurrentState(1)+
                    " pause="+GameState.IsPaused+" input="+GameState.AllowInput+" combat="+player.DataContainer.data.InCombat);
                report["setup"]=new JArray(two.Select(n=>new JObject{["name"]=n.CharacterInfo.name,["distance"]=Vector3.Distance(n.transform.position,player.transform.position),
                    ["chat"]=n.GetComponent<NPCChatController>().chatEnabled,["busy"]=n.GetComponent<NPCChatController>().GetIsBusy(),
                    ["scripted"]=n.GetComponent<NPCChatController>().GetDialogueData()!=null}));
                var chats=two.Select(n=>n.GetComponent<NPCChatController>()).ToArray();
                chats[0].chatEnabled=chats[1].chatEnabled=false;
                Require(!StartGroup()&&!GroupActive,"Group Talk started with zero available residents.");
                chats[0].chatEnabled=true;Require(!StartGroup()&&!GroupActive,"Group Talk started with only one resident.");
                chats[1].chatEnabled=true;
                var screen=new GameObject("Group Talk test partition");
                try{
                    screen.transform.position=(player.transform.position+two[0].transform.position)*.5f+Vector3.up*.8f;
                    screen.AddComponent<BoxCollider>().size=new Vector3(.3f,2,1.4f);Physics.SyncTransforms();
                    Require(!StartGroup()&&!GroupActive,"A resident behind a partition joined the group.");
                }finally{screen.SetActive(false);UnityEngine.Object.Destroy(screen);}
                GameState.SetCurrentState(1,GameState.State.UI,false);Require(!GroupMayStart(),"Group Talk opened in a menu.");
                GameState.SetCurrentState(1,GameState.State.Gameplay,false);
                Require(StartGroup(),"Could not start the approach cancellation check.");
                var approaching=gathering.Guests.ToArray();
                Close();
                Require(!GroupActive&&approaching.All(g=>!GroupGathering.Holds(g.Chat)&&g.Agent.isStopped==g.WasStopped),
                    "Cancelling the approach did not release native movement.");
                Require(StartGroup(),"Could not gather for the Escape check.");
                yield return Await(()=>groupReady,"The residents did not arrive for the Escape check.",15);
                playerBubble.Input.text="An unfinished thought";
                yield return GroupEscape();
                Require(GroupActive&&!groupTyping&&groupSequence==0&&!GameState.IsPaused,
                    "Escape submitted the draft, closed the group or left the clock paused.");
                yield return GroupEscape();
                Require(!GroupActive&&!Visible&&!GameState.IsPaused&&GameState.GetCurrentState(1)==GameState.State.Gameplay,
                    "Escape from the circle also opened a game menu or left its controls captured.");
                report["escape"]=true;
                Require(IsolatedGameRun.PostGroupKey(true),"The copied game could not receive the group key.");
                yield return new WaitForSecondsRealtime(.18f);IsolatedGameRun.PostGroupKey(false);
                float gatherUntil=Time.unscaledTime+14;
                while(!groupReady&&Time.unscaledTime<gatherUntil)yield return null;
                Require(groupReady,"The group key did not open the editor. "+stages.ToString(Newtonsoft.Json.Formatting.None)+"; "+
                    (gathering==null?"released":string.Join(", ",gathering.Guests.Select(g=>g.Npc.CharacterInfo.name+" at "+g.Npc.transform.position+" toward "+g.Spot+" "+g.Actor))));
                var seated=Participants();Require(two.All(n=>seated.Any(g=>g.Npc==n)),"Both staged residents must have arrived.");
                var distances=seated.Select(g=>Vector3.Distance(player.transform.position,g.Npc.transform.position)).ToArray();
                Require(distances.All(d=>d>=1.1f&&d<=2.6f)&&Vector3.Distance(two[0].transform.position,two[1].transform.position)>1f,"A resident crowded the player or another resident.");
                report["spacing"]=JArray.FromObject(distances);report["gathered"]=true;report["campaign"]=groupCampaign;report["groupSession"]=gathering.Session;
                report["guests"]=new JArray(seated.Select(g=>new JObject{["name"]=g.Npc.CharacterInfo.name,["actor"]=g.Actor}));
                var first=seated.Single(g=>g.Npc==two[0]);var other=seated.Single(g=>g.Npc==two[1]);
                string ownSession=gathering.Session;
                playerBubble.Input.text="wai";playerBubble.Input.caretPosition=3;
                Require(IsolatedGameRun.PostGroupKey(true),"Could not type the group key in the draft.");
                yield return new WaitForSecondsRealtime(.12f);IsolatedGameRun.PostGroupKey(false);
                yield return new WaitForSecondsRealtime(.15f);
                Require(groupTyping&&groupSequence==0&&gathering.Session==ownSession&&playerBubble.Input.text=="wait",
                    "Typing t changed the group or failed to enter text: "+playerBubble.Input.text);
                report["typedT"]=true;
                report["pair"]=ComparePairScores(first,other,player);
                string desireFile=Path.Combine(IsolatedGameRun.Active.Root,"starting-desire.txt");
                string desire=File.Exists(desireFile)?File.ReadAllText(desireFile).Trim():null;
                if(desire=="eager")foreach(var g in seated)g.Npc.CharacterInfo.personality.values[5]=1f;
                nextGroupReply=0;
                first.Npc.AddToHiringItemList("Flour",2);
                string opening="Averil, what did you collect for work today? Could you tell Drystan too?";
                playerBubble.Input.text=opening;float started=Time.unscaledTime;
                Require(IsolatedGameRun.PostReturn(),"Could not post the player's Enter.");
                float until=Time.unscaledTime+92;
                while(GroupActive&&groupSequence<2&&(groupTyping||busy||phase!=Phase.Reading)&&Time.unscaledTime<until)yield return null;
                Require(groupSequence>=2,"Group reply failed. Sequence "+groupSequence+", phase "+phase+", waiting "+busy+", typing "+groupTyping+", queued "+NativeCues.Pending+
                    ". "+stages.ToString(Newtonsoft.Json.Formatting.None));
                report["firstMs"]=Mathf.RoundToInt((Time.unscaledTime-started)*1000);
                bool captured=false;yield return Capture("latest-group-talk.png",ok=>captured=ok);report["capture"]=captured;
                if(groupTo=="player"&&nextGroupReply==0){IsolatedGameRun.PostListenKey();yield return null;}
                yield return Await(()=>groupSequence>=3||!GroupActive,"The second resident never answered.",95);
                Require(GroupActive&&groupSequence>=3,"The group ended before the second resident spoke.");
                NavMeshHit lateSpot;
                Require(NavMesh.SamplePosition(center.position+Vector3.forward*3f,out lateSpot,.65f,NavMesh.AllAreas),"No third approach in the clearing.");
                StageGuest(late,lateSpot.position);yield return null;
                Require(IsolatedGameRun.PostGroupKey(true),"Could not invite a late arrival.");
                yield return new WaitForSecondsRealtime(.15f);IsolatedGameRun.PostGroupKey(false);
                yield return Await(()=>GroupActive&&Participants().Any(g=>g.Npc==late),"The third resident did not join.",14);
                var lateGuest=Participants().Single(g=>g.Npc==late);
                ((JArray)report["guests"]).Add(new JObject{["name"]=late.CharacterInfo.name,["actor"]=lateGuest.Actor});
                int joinedAfter=groupSequence;
                yield return GroupQuestion("Drystan, who told you about the flour?",other.Actor);
                if(desire=="eager"){
                    late.CharacterInfo.personality.values[5]=-1f;
                    yield return GroupQuestion("Graham, have you had much time to yourself today?",lateGuest.Actor);
                    Require(!groupParting,"One resident taking leave ended a gathering of three.");
                }else StageGuest(late,lateStart);
                int departedAfter=groupSequence;
                yield return Await(()=>GroupActive&&!gathering.Guests.Contains(lateGuest),"The departing resident remained in the audience.",20);
                report["attendance"]=new JObject{["late"]=lateGuest.Actor,["joinedAfter"]=joinedAfter,["departedAfter"]=departedAfter};
                if(desire=="eager"||desire=="tired"){
                    foreach(var g in Participants())g.Npc.CharacterInfo.personality.values[5]=desire=="eager"?1f:-1f;
                    report["startingDesire"]=desire;
                }
                if(File.Exists(Path.Combine(IsolatedGameRun.Active.Root,"resident.txt"))){
                    for(int turn=0;turn<8&&GroupActive&&!groupParting;turn++)
                        yield return GroupQuestion(null,null,turn,seen,(JArray)report["guests"]);
                }
                if(!groupParting)yield return GroupQuestion("I should head home. Thank you both, and good night.",null);
                Require(groupParting,"The group missed the spoken goodbye.");
                report["totalMs"]=Mathf.RoundToInt((Time.unscaledTime-started)*1000);
                if(desire=="tired"){
                    yield return new WaitForSecondsRealtime(8.5f);
                    Require(GroupActive&&groupParting&&!groupTyping&&npcBubble.Visible,"The native farewell closed before the player finished reading.");
                    report["heldFarewell"]=true;
                }
                yield return Await(()=>NativeCues.Pending==0,"The delivered group lines did not reach the server.",18);
                report["groupLines"]=new JArray(seen.Where(e=>(string)e["kind"]=="group").Select(e=>e.DeepClone()));
                int pages=npcBubble.Pages;
                for(int page=0;GroupActive&&page<pages;page++){
                    yield return null;Require(IsolatedGameRun.PostReturn(),"Could not advance the spoken farewell.");
                    yield return new WaitForSecondsRealtime(.15f);
                }
                yield return Await(()=>!GroupActive,"Enter did not end the group after the goodbye.",4);
                report["released"]=!GroupActive&&!Visible&&!GameState.IsPaused&&seated.All(g=>!GroupGathering.Holds(g.Chat));
                string scoreKey="mirthwood.echoes.group.score."+ownSession;
                string savedScore=SaveMaster.GetString(scoreKey),owner=Plugin.Owner(first.Npc);
                Require(!string.IsNullOrEmpty(savedScore),"The real conversation has no saved social receipt.");
                var snapshot=Path.Combine(IsolatedGameRun.Active.Root,"group-farewell");
                try{
                    CaptureSlot(snapshot);yield return LoadSnapshot(snapshot,owner);
                    Require(!GroupActive&&SaveMaster.GetString(scoreKey)==savedScore,"Reload lost the directed social receipt or reopened the circle.");
                    report["reload"]=true;
                }finally{RemoveSnapshot(IsolatedGameRun.Active.Root,snapshot);}
            }finally{
                Close();NativeCues.Trace=null;groupProbe=null;
                for(int i=0;i<two.Length;i++)if(two[i])two[i].GetComponent<NavMeshAgent>()?.Warp(previous[i]);
                if(late&&late.GetComponent<NavMeshAgent>().isOnNavMesh)late.GetComponent<NavMeshAgent>().Warp(lateStart);
                if(player)player.transform.position=playerStart;
            }
        }

        static void StageGuest(NPCController npc,Vector3 position)
        {
            var chat=npc.GetComponent<NPCChatController>();chat.SetBusy(false);chat.chatTarget=null;
            AccessTools.PropertySetter(typeof(ChatController),"IsChatting").Invoke(chat,new object[]{false});
            var agent=npc.GetComponent<NavMeshAgent>();var controller=npc.GetComponent<CharacterController>();
            bool enabled=controller&&controller.enabled;
            if(enabled)controller.enabled=false;
            try{
                Require(agent&&agent.isActiveAndEnabled&&agent.Warp(position),"Could not stage "+npc.CharacterInfo.name);
                npc.transform.position=position;agent.ResetPath();
            }finally{if(enabled)controller.enabled=true;}
            Physics.SyncTransforms();
        }

        static IEnumerator DismissGroupTestCards()
        {
            yield return new WaitForSecondsRealtime(.6f);
            for(int i=0;i<10&&StoryCardOpen;i++){
                UnityEngine.Object.FindObjectOfType<EventCardView>()?.HideEventCard();
                yield return new WaitForSecondsRealtime(1.2f);
            }
            Require(!StoryCardOpen,"A native story card still owns the screen.");
        }

        static IEnumerator GroupEscape()
        {
            Require(IsolatedGameRun.PostTalkOrEscape(false,true),"Could not press Escape in the copied game.");
            yield return new WaitForSecondsRealtime(.15f);
            Require(IsolatedGameRun.PostTalkOrEscape(false,false),"Could not release Escape in the copied game.");
            yield return new WaitForSecondsRealtime(.2f);
        }

        static JObject ComparePairScores(GroupGathering.Guest speaker,GroupGathering.Guest listener,PlayerCharacterController player)
        {
            var self=listener.Npc.CharacterInfo;var other=speaker.Npc.CharacterInfo;
            var original=self.relationships;var originalMood=self.mood*1f;
            var random=UnityEngine.Random.state;bool unique=listener.Unique;
            var spouseManager=MirthwoodPlayer.Instance.SpouseManager;
            var spouseField=AccessTools.Field(typeof(SpouseManager),"npcCharacter");
            var originalSpouse=spouseField.GetValue(spouseManager);
            var result=new JObject();string receipt=Guid.NewGuid().ToString();int seq=0;
            string reverse=Newtonsoft.Json.JsonConvert.SerializeObject(other.relationships.GetSaveData());
            try{
                self.relationships=new Relationships();listener.Unique=true;
                var bystander=self.relationships[player.CharacterInfo.id];bystander.friendship=13;
                Require(GroupSocialTurn.Apply(receipt,++seq,listener,speaker.Npc,"Leisure",100)==null&&
                    !self.relationships.GetSaveData().characterIDs.Contains(other.id),"An unknown relationship was created.");
                var bond=self.relationships[other.id];bond.friendship=22;
                string damaged=Guid.NewGuid().ToString();
                SaveMaster.SetString("mirthwood.echoes.group.score."+damaged,"{truncated");
                Require(GroupSocialTurn.Apply(damaged,1,listener,speaker.Npc,"Leisure",100)==null&&bond.friendship==22,
                    "A damaged score receipt changed the relationship.");
                listener.Unique=false;
                Require(GroupSocialTurn.Apply(receipt,++seq,listener,speaker.Npc,"Leisure",100)==null&&bond.friendship==22,"A duplicate identity was graded.");
                listener.Unique=true;
                Require(GroupSocialTurn.Apply(receipt,++seq,listener,speaker.Npc,"None",100)==null&&bond.friendship==22,"A physical gesture changed friendship.");
                result["unknownDuplicateActionOnly"]=true;
                foreach(bool spouse in new[]{false,true}){
                    if(spouse){
                        self.relationships.SetSpouse("Player");
                        spouseField.SetValue(spouseManager,null);
                        Require(GroupSocialTurn.Apply(receipt,++seq,listener,speaker.Npc,"Leisure",100)==null,"An unverified spouse was graded.");
                        spouseField.SetValue(spouseManager,listener.Npc);
                    }
                    foreach(var card in listener.Chat.dnaData.chatActions.Where(c=>c!=null&&new[]{"Small Talk","Leisure","Deep","Intellectual","Dumb"}.Contains(c.name))){
                        for(int seed=0;seed<12;seed++){
                            self.mood=originalMood*1f;bond.friendship=22;bond.happiness=50;bond.romance=5;UnityEngine.Random.InitState(seed);
                            DnaTesting.ScoreAction(card,self,other,out float grade,false);
                            string kind=spouse?"spouse":grade>0?"warm":grade<0?"cool":"neutral";
                            if(result[kind]!=null||spouse&&grade==0)continue;
                            float stock=bond.friendship,happiness=bond.happiness,romance=bond.romance;
                            self.mood=originalMood*1f;bond.friendship=22;bond.happiness=50;bond.romance=5;UnityEngine.Random.InitState(seed);
                            var applied=GroupSocialTurn.Apply(receipt,++seq,listener,speaker.Npc,card.name,100);
                            var replay=GroupSocialTurn.Apply(receipt,seq,listener,speaker.Npc,card.name,100);
                            Require(applied!=null&&replay?.Reaction==applied.Reaction&&Mathf.Approximately(bond.friendship,stock)&&
                                Mathf.Approximately(bond.happiness,happiness)&&Mathf.Approximately(bond.romance,romance)&&bystander.friendship==13,
                                "Directed "+kind+" scoring or replay differed from stock.");
                            result[kind]=new JObject{["card"]=card.name,["stock"]=stock,["group"]=bond.friendship,["happiness"]=happiness,["grade"]=grade};
                        }
                    }
                }
                Require(new[]{"warm","cool","neutral","spouse"}.All(key=>result[key]!=null)&&
                    Newtonsoft.Json.JsonConvert.SerializeObject(other.relationships.GetSaveData())==reverse,"Missing native score or changed reverse relationship.");
                return result;
            }finally{spouseField.SetValue(spouseManager,originalSpouse);self.relationships=original;self.mood=originalMood;listener.Unique=unique;UnityEngine.Random.state=random;}
        }

        IEnumerator GroupQuestion(string words,string expectedSpeaker,int turn=-1,JArray heard=null,JArray guests=null)
        {
            int previous=groupSequence;
            yield return null;
            Require(IsolatedGameRun.PostReturn(),"Could not open the group draft.");
            yield return Await(()=>groupTyping,"Enter did not give the player the floor.",5);
            if(turn>=0){
                string folder=IsolatedGameRun.Active.Root;
                var recent=heard.Where(e=>(string)e["kind"]=="group").Select(e=>e["line"]).Reverse().Take(3).Reverse().Select(line=>{
                    string key=(string)line["speaker"];
                    string name=key=="player"?"Player":(string)guests.First(g=>(string)g["actor"]==key)["name"];
                    return name+": "+(string)line["words"];
                });
                string output=Path.Combine(folder,"reply-"+turn+".json"),draft=Path.Combine(folder,"draft-"+turn+".txt");
                File.WriteAllText(output+".tmp",new JObject{["reply"]=string.Join("\n",recent)}.ToString());File.Move(output+".tmp",output);
                File.WriteAllText(Path.Combine(folder,"ready-"+turn),"Group");
                yield return Await(()=>File.Exists(draft)||!GroupActive,"No player input for the copied group conversation.",180);
                Require(GroupActive&&groupTyping,"The group closed while the player was writing.");
                words=File.ReadAllText(draft);
            }
            playerBubble.Input.text=words;yield return null;
            Require(IsolatedGameRun.PostReturn(),"Could not submit the group draft.");
            yield return Await(()=>groupSequence>=previous+2||!GroupActive,"The group did not answer "+words,95);
            Require(GroupActive&&(expectedSpeaker==null||groupLastSpeaker==expectedSpeaker),"The named resident did not answer.");
        }
    }
}
