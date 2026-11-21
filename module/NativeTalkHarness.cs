using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HarmonyLib;
using Lowscope.Saving;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;
using _Mirthwood.Scripts.Demo;
using _Mirthwood.Scripts.Demo.Chat;
using _Mirthwood.Scripts.Demo.Combat;
using _Mirthwood.Scripts.Demo.Items;
using _Mirthwood.Scripts.Demo.Menus;
using _Mirthwood.Scripts.Demo.ScriptableObjects;
using _Mirthwood.Scripts.Demo.Tests;
using _Mirthwood.Scripts.Demo.Weather;

namespace Mirthwood.Echoes
{
    internal sealed partial class NativeConversation
    {
        bool interruptBeforeFinish;
        float testFirstVisible;
        string testFirstBubble;
        static int observedScores,sampleSeed;
        static string watchedOwner;
        static int liveDebits;
        static float lastLiveCost;
        static void BeforeLiveDebit(NPCChatController __instance,out float __state)
        {
            __state=__instance.chatTarget is PlayerChat && watchedOwner!=null && Plugin.Owner(__instance.Character)==watchedOwner
                ?__instance.Character.CharacterInfo.personality.values[5]:float.NaN;
        }
        static void CountLiveDebit(NPCChatController __instance,float __state)
        {
            if(float.IsNaN(__state))return;
            liveDebits++;lastLiveCost=__state-__instance.Character.CharacterInfo.personality.values[5];
        }
        static string scoredTopic;
        static void CountScore(DnaData.Dna action,bool isPlayer,out UnityEngine.Random.State __state)
        {
            __state=UnityEngine.Random.state;
            if(isPlayer){observedScores++;scoredTopic=action.name;UnityEngine.Random.InitState(sampleSeed);}
        }
        static void RestoreRandom(bool isPlayer,UnityEngine.Random.State __state){if(isPlayer)UnityEngine.Random.state=__state;}
        static bool InCopy()=>IsolatedGameRun.Active?.PathsMatch()==true;
        static List<IContextMenu.Option> Choices(PlayerChat chat)=>(List<IContextMenu.Option>)AccessTools.Method(typeof(PlayerChat),"GenerateOptions").Invoke(chat,null);
        void ChooseTalkInCopy(NPCController npc)
        {
            if(!InCopy()||npc==null)return;
            var chat=PlayerCharacterController.Instance.GetComponent<PlayerChat>();var other=npc.GetComponent<NPCChatController>();
            Plugin.Selected.SetValue(chat,other);chat.chatTarget=other;
            var option=Choices(chat).Single(o=>o.Name=="Talk");
            option.Callback(2);if(Visible)throw new InvalidOperationException("Talk accepted the wrong player.");
            option.Callback(1);
        }
        public IEnumerator RunRelationships(JObject report,bool balanceOnly=false)
        {
            if(!InCopy()||!balanceOnly&&IsolatedGameRun.Active.TalkAddress==null)yield break;
            yield return CheckedProbe(report,balanceOnly?BalanceCases(report,ComparisonResidents()):RelationshipCases(report));
        }
        IEnumerator CheckedProbe(JObject report,IEnumerator cases)
        {
            var stack=new Stack<IEnumerator>();stack.Push(cases);
            try{
                while(stack.Count>0){
                    var item=stack.Peek();bool more;
                    try{more=item.MoveNext();}
                    catch(Exception e){report["probeError"]=e.GetBaseException().ToString();break;}
                    if(!more){(stack.Pop() as IDisposable)?.Dispose();continue;}
                    if(item.Current is IEnumerator child && !(child is CustomYieldInstruction)){stack.Push(child);continue;}
                    yield return item.Current;
                }
            }finally{
                while(stack.Count>0)(stack.Pop() as IDisposable)?.Dispose();
                interruptBeforeFinish=false;if(Visible)Close();
            }
        }
        static IEnumerator Await(Func<bool> predicate,string description,float seconds=90)
        {
            float until=Time.realtimeSinceStartup+seconds;
            while(!predicate()&&Time.realtimeSinceStartup<until)yield return null;
            if(!predicate())throw new TimeoutException(description);
        }
        static NPCController FindResident(string owner)=>UnityEngine.Object.FindObjectsOfType<NPCController>().FirstOrDefault(n=>n!=null&&n.isActiveAndEnabled&&Plugin.Owner(n)==owner);
        static float Friendship(NPCController npc)=>npc.CharacterInfo.relationships[PlayerCharacterController.Instance.CharacterInfo.id].friendship;
        const string SnapshotMarker="mirthwood.echoes.harness.snapshot";
        static readonly string[] SlotFiles={"Slot0.savegame","Slot0.savegame.b","Slot0.metadata","Slot0.metadata.b"};
        static void CopySlot(string from,string to)
        {
            Directory.CreateDirectory(to);
            foreach(string file in SlotFiles){
                var source=Path.Combine(from,file);var destination=Path.Combine(to,file);
                if(!File.Exists(source)){if(File.Exists(destination))File.Delete(destination);continue;}
                File.Copy(source,destination,true);File.SetLastWriteTimeUtc(destination,File.GetLastWriteTimeUtc(source));
            }
        }
        static void CaptureSlot(string snapshot)
        {
            SaveMaster.SetString(SnapshotMarker,Path.GetFileName(snapshot));GameState.SaveGame();
            CopySlot(Path.Combine(IsolatedGameRun.Active.Root,"SaveData"),snapshot);
        }
        static void RemoveSnapshot(string root,string snapshot)
        {
            if(Path.GetDirectoryName(snapshot)!=root||!Directory.Exists(snapshot))return;
            foreach(string file in SlotFiles){var target=Path.Combine(snapshot,file);if(File.Exists(target))File.Delete(target);}
            Directory.Delete(snapshot,false);
        }
        static IEnumerator LoadSnapshot(string snapshot,string owner)
        {
            var run=IsolatedGameRun.Active;
            if(!InCopy()||Path.GetDirectoryName(snapshot)!=run.Root)throw new InvalidDataException("Unexpected harness snapshot.");
            var previous=FindResident(owner);
            GameState.QuitGameplay();SceneManager.LoadScene(0);
            yield return Await(()=>UnityEngine.Object.FindObjectOfType<TitleScreenController>()!=null,"The title screen did not load.",50);
            yield return new WaitForSecondsRealtime(1.5f);
            if(SaveMaster.IsSlotLoaded())SaveMaster.ClearSlot(false,false);
            CopySlot(snapshot,Path.Combine(run.Root,"SaveData"));
            UnityEngine.Object.FindObjectOfType<TitleScreenController>().Continue();
            yield return Await(()=>{var npc=FindResident(owner);return npc!=null&&npc!=previous&&npc.CharacterInfo!=null&&
                PlayerCharacterController.Instance?.CharacterInfo!=null&&GameState.GetCurrentState(1)==GameState.State.Gameplay;},
                "Continue did not create the saved resident.",65);
            yield return new WaitForSecondsRealtime(1);
            if(SaveMaster.GetString(SnapshotMarker)!=Path.GetFileName(snapshot))throw new IOException("Continue loaded the wrong save generation.");
        }
        static string[] ComparisonResidents()=>UnityEngine.Object.FindObjectsOfType<NPCController>()
                .Where(n=>n.isActiveAndEnabled&&Plugin.Owner(n)!=""&&n.CharacterInfo!=null&&OverheadSpeech.Board(n)!=null&&
                    n.GetComponent<NPCChatController>() is NPCChatController brain&&brain.chatEnabled&&brain.dnaData!=null&&brain.GetDialogueData()==null&&!n.CharacterInfo.relationships.IsPlayersSpouse())
                .OrderBy(n=>(n.transform.position-PlayerCharacterController.Instance.transform.position).sqrMagnitude)
                .Select(Plugin.Owner).Distinct().Take(3).ToArray();
        IEnumerator RelationshipCases(JObject report)
        {
            var trials=new JArray();report["trials"]=trials;
            var run=IsolatedGameRun.Active;
            using(var heartbeat=Post(Address+"module/heartbeat",new{version=ModIdentity.Version,world=Plugin.World()})){
                yield return heartbeat.SendWebRequest();if(heartbeat.responseCode!=200)throw new IOException("No isolated campaign clock.");
            }
            var candidates=ComparisonResidents();
            if(candidates.Length!=3)throw new InvalidDataException("Three named residents are required.");
            string[] words={"Rain again. When work is done, would you rather go fishing or play chess?",
                "I will bring flour to the mill tomorrow. What causes the moon to change shape through the month?",
                "I have two equally important questions. Which games do you play for fun? Is it ever right to break a promise to save a life?",
                "*I brush rain from my sleeve.* Hmm.",
                "Would you sell me this loaf of bread? How much are you asking for it?",
                "*I look down at my boots.*",
                "Which moral value do I privately hold most sacred? I have never told anyone."};
            string[] expected={"Leisure","Intellectual","Small Talk","Small Talk","None","None","Deep"};
            var scoreMethod=AccessTools.Method(typeof(DnaTesting),"ScoreAction");
            var observer=new Harmony("mirthwood.echoes.harness.relationships");
            observer.Patch(scoreMethod,prefix:new HarmonyMethod(typeof(NativeConversation),nameof(CountScore)),
                postfix:new HarmonyMethod(typeof(NativeConversation),nameof(RestoreRandom)));
            observer.Patch(AccessTools.Method(typeof(NPCChatController),"DecreaseSocialNeed"),prefix:new HarmonyMethod(typeof(NativeConversation),nameof(BeforeLiveDebit)),
                postfix:new HarmonyMethod(typeof(NativeConversation),nameof(CountLiveDebit)){after=new[]{Plugin.Id}});
            // Only files in the validated harness root are touched, including the snapshot used to prove rewind behavior.
            var rewind=Path.Combine(run.Root,"BeforeTalk");var latest=Path.Combine(run.Root,"AcceptedTalk");
            try{
                CaptureSlot(rewind);
                var impressions=new JArray();report["impressionDialogue"]=impressions;
                yield return ImpressionDialogueCases(rewind,candidates[0],impressions,"anonymousArmor","introducedInfamous","introducedObscure","knownParagon","vanityArmor","underlayers");
                for(int i=0;i<words.Length;i++){
                    var owner=candidates[i%3];var npc=FindResident(owner);
                    watchedOwner=owner;liveDebits=0;
                    if(i==0||i==1||i==6)npc.CharacterInfo.personality.values[5]=i==0?.9f:-.9f;
                    var row=new JObject{["resident"]=npc.CharacterInfo.name,["expected"]=expected[i]};trials.Add(row);
                    var chat=PlayerCharacterController.Instance.GetComponent<PlayerChat>();var brain=npc.GetComponent<NPCChatController>();
                    Plugin.Selected.SetValue(chat,brain);brain.isChatReady=false;
                    var firstOptions=Choices(chat);row["directTalk"]=firstOptions.Count(o=>o.Name=="Talk")==1&&firstOptions.First().Name=="Talk";
                    ChooseTalkInCopy(npc);yield return Await(()=>Ready,"Could not resolve "+npc.CharacterInfo.name+".",12);
                    row["targeted"]=resident==npc&&GameState.IsPaused;
                    string c=campaign,a=actor;var outbox=exchange;
                    float before=Friendship(npc),started=Time.realtimeSinceStartup;observedScores=0;scoredTopic=null;interruptBeforeFinish=true;
                    row["desireBefore"]=Appetite(npc);
                    sampleSeed=i==6?StorySeed(npc,.85f):i==0?35:32;row["nativeSeed"]=sampleSeed;
                    playerBubble.Input.text=words[i];playerBubble.Input.onSubmit.Invoke(words[i]);
                    var pending=outbox.Pending();if(pending==null)throw new InvalidDataException("The accepted exchange was not persisted.");
                    string id=(string)pending["id"];
                    string path=Path.Combine(run.Root,"PendingTalk",c+"-"+a+".json");var firstRequest=File.ReadAllBytes(path);
                    row["context"]=pending["context"]?.DeepClone();row["id"]=id;row["campaign"]=c;row["actor"]=a;row["text"]=words[i];row["before"]=before;
                    if(i==0){
                        yield return new WaitForSecondsRealtime(.8f);if(Visible)Close();
                        ChooseTalkInCopy(npc);
                    }
                    yield return Await(()=>!Visible,"The model or native scorer did not finish for "+npc.CharacterInfo.name+".");
                    var scored=outbox.Pending();
                    if((string)scored?["stage"]!="scored")throw new InvalidDataException("Missing local outcome: "+(string)scored?["stage"]);
                    float after=Friendship(npc);string branch=(string)scored["reaction"];row["after"]=after;row["branch"]=branch;
                    var handoff=scored["handoff"] as JObject;row["handoff"]=handoff?.DeepClone();row["desireAfter"]=Appetite(npc);
                    row["positiveRun"]=(float?)scored["positiveRun"]??0;
                    row["firstVisibleMs"]=Mathf.RoundToInt(1000*(testFirstVisible-started));
                    using(var request=Post(Address+"dialogue/prepare",new{id,campaign=c,actor=a,text=words[i],context=pending["context"]})){
                        yield return request.SendWebRequest();var packet=JObject.Parse(request.downloadHandler.text);
                        string ordinary=(string)packet["choices"][branch],ending=(string)packet["endingChoices"][branch];
                        var card=(string)handoff?["action"];
                        string continuing=(card==null?null:(string)packet["cardChoices"]?[card]?[branch])??ordinary;
                        string spoken=(string)handoff?["kind"]=="ended"?ending:continuing;
                        row["continuingReply"]=continuing;row["endingReply"]=ending;row["spoken"]=spoken;
                        row["firstBubbleMatched"]=testFirstBubble==new BubbleText(spoken,false).Plain;
                    }
                    // Simulate a process exit between the native write and its final local disk receipt.
                    var ambiguous=JObject.Parse(File.ReadAllText(path));ambiguous["stage"]="applying";ambiguous.Remove("reaction");ambiguous.Remove("handoff");ambiguous.Remove("positiveRun");File.WriteAllText(path,ambiguous.ToString());
                    // Recovery after the native mutation, while PostgreSQL still holds the prepared reply.
                    ChooseTalkInCopy(npc);yield return Await(()=>Visible&&!busy&&phase==Phase.Reading,"Missing recovered reply.");
                    string reply=npcBubble.VisibleText;row["reply"]=reply;row["recovered"]=Friendship(npc)==after&&outbox.Pending()==null&&Near(positiveRun,(float)row["positiveRun"]);
                    using(var request=Post(Address+"dialogue/prepare",new{id,campaign=c,actor=a,text=words[i],context=pending["context"]})){
                        yield return request.SendWebRequest();var packet=JObject.Parse(request.downloadHandler.text);
                        row["topic"]=(string)packet["topic"];row["cached"]=(string)packet["state"]=="complete"&&(string)packet["reaction"]==branch&&
                            (string)packet["reply"]==(string)row["spoken"]&&testFirstBubble==reply&&JToken.DeepEquals(handoff,packet["handoff"]);
                    }
                    if((string)handoff?["kind"]=="ended"){
                        row["exitHintFits"]=npcBubble.ChecksEdges();
                        if(i==1)yield return Capture("latest-social-ending.png",ok=>row["endingCapture"]=ok);
                        if(!conversationOver||!IsolatedGameRun.PostReturn())throw new IOException("The ended conversation accepted another turn.");
                        yield return Await(()=>!Visible,"Enter did not leave the ended conversation.",5);
                        row["endInputClosed"]=outbox.Pending()==null;
                    }
                    Close();CaptureSlot(latest);
                    // Deliberately alter the in-memory value to prove we actually reloaded the saved relationship.
                    npc.CharacterInfo.relationships[PlayerCharacterController.Instance.CharacterInfo.id].friendship=after+7;
                    yield return LoadSnapshot(latest,owner);
                    npc=FindResident(owner);
                    row["friendAfterLoad"]=Friendship(npc);
                    var saved=outbox.SavedResult(id);
                    row["savedReceipt"]=saved?.Reaction==branch&&JToken.DeepEquals(saved.Handoff,handoff)&&Near(saved.PositiveRun,(float)row["positiveRun"]);
                    row["reloaded"]=Mathf.Approximately(Friendship(npc),after)&&(bool)row["savedReceipt"];
                    File.WriteAllBytes(path,firstRequest);ChooseTalkInCopy(npc);
                    yield return Await(()=>Visible&&!busy&&phase==Phase.Reading,"Reply did not replay after a native reload.");
                    row["sameReply"]=reply==npcBubble.VisibleText&&conversationOver==((string)handoff?["kind"]=="ended");
                    if(i==words.Length-1){
                        var interrupted=outbox.Accept("Harness interruption before a confirmed native result.");var stamp=(string)interrupted["id"];
                        interrupted["stage"]="applying";File.WriteAllText(path,interrupted.ToString());
                        var unknown=outbox.Apply(stamp,"Leisure",npc);
                        row["unknownSkipped"]=unknown.Reaction=="unknown"&&(string)unknown.Handoff["kind"]=="unknown"&&outbox.Apply(stamp,"Leisure",npc).Reaction=="unknown";
                        outbox.Acknowledge(stamp);
                        var legacy=outbox.Accept("Legacy receipt.");string legacyId=(string)legacy["id"];
                        SaveMaster.SetString("mirthwood.echoes.talk."+a,legacyId+"|cool");
                        var recovered=outbox.Apply(legacyId,"Small Talk",npc);
                        row["legacySkipped"]=recovered.Reaction=="cool"&&recovered.Handoff==null;outbox.Acknowledge(legacyId);
                    }
                    row["scoreCalls"]=observedScores;row["nativeAction"]=scoredTopic;row["afterReplay"]=Friendship(npc);
                    row["desireDebits"]=liveDebits;
                    row["passed"]=(bool)row["directTalk"]&&(bool)row["targeted"]&&(bool)row["recovered"]&&(bool)row["cached"]&&
                        (bool)row["reloaded"]&&(bool)row["sameReply"]&&(string)row["topic"]==expected[i]&&Friendship(npc)==after&&
                        observedScores==(expected[i]=="None"?0:1)&&scoredTopic==(expected[i]=="None"?null:expected[i])&&
                        liveDebits==((string)handoff?["kind"]=="followup"?1:0)&&
                        ((string)handoff?["kind"]!="ended"||((bool)row["endInputClosed"]&&(bool)row["exitHintFits"]))&&
                        (bool)row["firstBubbleMatched"]&&(i!=1||(string)handoff?["kind"]=="ended")&&
                        (i!=6||(branch=="warm"&&(string)handoff?["kind"]=="ended"))&&
                        (i!=words.Length-1||((bool)row["unknownSkipped"]&&(bool)row["legacySkipped"]));
                    Close();
                    if(i==words.Length-1){
                        yield return CheckPlayerImpressions(npc,outbox,report);
                        yield return LoadSnapshot(rewind,candidates[0]);
                        var first=(JObject)trials[0];var restored=FindResident(candidates[0]);
                        string original=(string)first["id"];var old=new TalkExchange((string)first["campaign"],(string)first["actor"]);
                        bool older=!old.SavedMarker().StartsWith(original,StringComparison.Ordinal)&&Mathf.Approximately(Friendship(restored),(float)first["before"]);
                        var file=Path.Combine(run.Root,"PendingTalk",(string)first["campaign"]+"-"+(string)first["actor"]+".json");
                        File.WriteAllText(file,new JObject{["id"]=original,["stage"]="new",["text"]=words[0],["context"]=first["context"]?.DeepClone()}.ToString());
                        observedScores=0;ChooseTalkInCopy(restored);yield return Await(()=>Ready&&!busy&&phase==Phase.Draft,"Earlier save did not discard the other timeline’s receipt.");
                        report["rewind"]=JObject.FromObject(new{earlierSave=older,snapshot=SaveMaster.GetString(SnapshotMarker),
                            friendship=Friendship(restored),expected=(float)first["before"],scoreCalls=observedScores,abandonedReceiptCleared=old.Pending()==null});
                        report["reloaded"]=older&&observedScores==0&&Mathf.Approximately(Friendship(restored),(float)first["before"])&&old.Pending()==null;
                    }
                }
                if(Visible)Close();
                yield return LiveStory(report,candidates);
                watchedOwner=null;
            }finally{watchedOwner=null;observer.UnpatchSelf();RemoveSnapshot(run.Root,rewind);RemoveSnapshot(run.Root,latest);}
            yield return BalanceCases(report,candidates);
        }
        IEnumerator LiveStory(JObject report,string[] owners)
        {
            var npc=FindResident(owners[0]);watchedOwner=owners[0];npc.CharacterInfo.personality.values[5]=.8f;
            ChooseTalkInCopy(npc);yield return Await(()=>Ready,"Could not open a personal conversation.");
            var story=new JArray();report["story"]=story;
            var words=new[]{"I remember leaving home and wondering what makes a life worthwhile. What gives your own life meaning?",
                "Thinking about your own story, why does mercy matter to you when someone has caused real hurt?"};
            for(int i=0;i<words.Length;i++){
                var outbox=exchange;float prior=positiveRun;
                observedScores=liveDebits=0;lastLiveCost=0;sampleSeed=StorySeed(npc,i==0?.48f:.85f);interruptBeforeFinish=i==1;
                playerBubble.Input.text=words[i];playerBubble.Input.onSubmit.Invoke(words[i]);
                var intent=outbox.Pending();string id=(string)intent["id"];
                if(!Near((float)intent["previousRun"],prior))throw new InvalidOperationException("The personal conversation lost its running interest.");
                if(i==1){
                    yield return Await(()=>!Visible,"The second personal reply did not reach its receipt.");
                    var scored=outbox.Pending();
                    if(!Near((float)scored["previousRun"],prior)||(string)scored["stage"]!="scored")throw new IOException("Missing personal reply receipt.");
                    ChooseTalkInCopy(npc);
                }
                yield return Await(()=>Visible&&!busy&&phase==Phase.Reading,"The personal conversation did not complete.");
                using(var req=Post(Address+"dialogue/prepare",new{id,campaign,actor,text=words[i],context=intent["context"]})){
                    yield return req.SendWebRequest();var packet=JObject.Parse(req.downloadHandler.text);
                    var row=new JObject{["id"]=id,["context"]=intent["context"]?.DeepClone(),["campaign"]=campaign,["actor"]=actor,["text"]=words[i],["resident"]=npc.CharacterInfo.name,
                        ["topic"]=packet["topic"],["branch"]=packet["reaction"],["reply"]=packet["reply"],["handoff"]=packet["handoff"],
                        ["previousRun"]=prior,["positiveRun"]=positiveRun,["cost"]=lastLiveCost,["scoreCalls"]=observedScores,["debits"]=liveDebits};
                    story.Add(row);
                    row["passed"]=(string)row["topic"]=="Deep"&&(string)row["branch"]=="warm"&&!conversationOver&&
                        observedScores==1&&liveDebits==1&&lastLiveCost>0&&positiveRun>0&&(i==0?prior==0:prior>0&&positiveRun>=prior)&&
                        Near(outbox.SavedResult(id).PositiveRun,positiveRun);
                    if(!(bool)row["passed"])throw new InvalidOperationException("Personal conversation did not receive the expected native response on turn "+(i+1)+".");
                }
                if(i==0)Advance();
            }
            Close();ChooseTalkInCopy(FindResident(owners[1]));yield return Await(()=>Ready,"Could not switch residents after the story.");
            report["freshConversation"]=positiveRun==0;Close();
        }
        static int StorySeed(NPCController npc,float targetScore)
        {
            var info=npc.CharacterInfo;var player=PlayerCharacterController.Instance.CharacterInfo;
            var card=npc.GetComponent<NPCChatController>().dnaData.chatActions.Single(a=>a.name=="Deep");
            float affinity=(info.personality+player.personality*.25f).Normalized().DotDistance(card)+
                info.mood.DotDistance(card)*(card.negativeMoodComparison?-.5f:.5f);
            var prior=UnityEngine.Random.state;int seed=0;float best=float.MaxValue;
            try{
                for(int i=0;i<96;i++){
                    UnityEngine.Random.InitState(i);
                    float difference=Mathf.Abs(affinity+UnityEngine.Random.Range(-.5f,.5f)-targetScore);
                    if(difference<best){best=difference;seed=i;}
                }
            }finally{UnityEngine.Random.state=prior;}
            return seed;
        }
    }
}
