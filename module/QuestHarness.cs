using System;
using System.Collections;
using System.IO;
using System.Linq;
using HarmonyLib;
using InfinityPBR.Modules;
using Newtonsoft.Json.Linq;
using TMPro;
using UnityEngine;
using _Mirthwood.Scripts.Demo.Chat;
using _Mirthwood.Scripts.Demo.Combat;
using _Mirthwood.Scripts.Demo.GameModules;
using _Mirthwood.Scripts.Demo.ScriptableObjects;
using _Mirthwood.Scripts.Runtime.Demo.Menus.Views;

namespace Mirthwood.Echoes
{
    internal sealed partial class NativeConversation
    {
        internal IEnumerator RunQuests(JObject report)
        {
            if(!InCopy()||IsolatedGameRun.Active.TalkAddress==null)yield break;
            try{yield return CheckedProbe(report,QuestCases(report));}
            finally{QuestPanel.StockPreview=false;QuestPanel.Close();}
        }
        static DialogueView ChooseSpeak(NPCController npc)
        {
            if(!InCopy())throw new InvalidOperationException("Missing isolated save.");
            var player=PlayerCharacterController.Instance.GetComponent<PlayerChat>();var other=npc.GetComponent<NPCChatController>();
            Plugin.Selected.SetValue(player,other);player.chatTarget=other;
            var choice=Choices(player).Single(o=>o.Name=="Speak");choice.Callback(1);
            return UnityEngine.Object.FindObjectOfType<DialogueView>()??throw new InvalidOperationException("Speak did not open the native portrait.");
        }
        static void RequireQuest(bool success,string message){if(!success)throw new InvalidOperationException(message);}
        IEnumerator QuestCases(JObject report)
        {
            using(var clock=Post(Address+"module/heartbeat",new{version=ModIdentity.Version,world=Plugin.World()})){
                yield return clock.SendWebRequest();RequireQuest(clock.responseCode==200,"No isolated campaign clock.");
            }
            var owners=ComparisonResidents();RequireQuest(owners.Length>=3,"Three indexed residents are needed.");
            yield return CheckBackendOutage(owners,report);
            var generator=MirthwoodPlayer.Instance.QuestGenerator;var trials=new JArray();report["quests"]=trials;
            var types=new[]{QuestType.helping,QuestType.hunting,QuestType.blade,QuestType.freshCatch};
            string[][] messages={
                new[]{"How much does the job pay?","I might be able to help. Let me think.","I’ll do it if you pay me double.","Yes, I’ll do the work on your original terms."},
                new[]{"Could you give me twice the usual pay?","When do you need it done?","Then give me another week to finish it instead."},
                new[]{"You can count on me. I'll take the job as offered."},
                new[]{"No thank you. I won't take this job."}
            };
            string[][] intents={new[]{"question","unclear","counter","accept"},new[]{"counter","question","counter"},new[]{"accept"},new[]{"decline"}};
            var accepted=new System.Collections.Generic.List<string>();
            for(int i=0;i<types.Length;i++){
                var npc=FindResident(owners[i%owners.Length]);var brain=npc.GetComponent<NPCChatController>();var ql=MirthwoodPlayer.Instance.QuestList;
                if(i==2){
                    var templates=(QuestTemplate)AccessTools.Field(typeof(QuestGenerator),"questTemplate").GetValue(generator);
                    types[i]=new[]{QuestType.blade,QuestType.bounty,QuestType.wolf,QuestType.pest}.First(type=>
                        ql.Get(templates.quests.First(t=>t.questType==type).baseQuest)==null&&!generator.GeneratedQuestTypeExists(type));
                }
                var generated=i==2?generator.GeneratePestControlQuest(types[i],npc):generator.GeneratePantryQuest(types[i],npc);
                var quest=generated.quest;var start=generated.dialogueStart;var lines=new JArray();
                var trial=new JObject{["resident"]=npc.CharacterInfo.name,["type"]=types[i].ToString(),["turns"]=lines};trials.Add(trial);
                var active=brain.GetDialogueData();
                RequireQuest(quest&&start&&ReferenceEquals(active,start)&&ql.Get(quest)==null,"Native offer unavailable for "+types[i]+
                    ": active="+active?.name+", generated="+start?.name+", listed="+brain.ConditionalDialogues.Any(d=>ReferenceEquals(d.dialogueData,start))+
                    ", in journal="+(quest&&ql.Get(quest)!=null)+", blocked="+brain.ConditionalDialoguesBlocked);
                trial["nativeOffer"]=start.Dialogue;trial["key"]=quest.Uid();
                var nativeChoices=start.Choices.Select(s=>s.choiceText).ToArray();trial["nativeChoices"]=new JArray(nativeChoices);
                RequireQuest(nativeChoices.SequenceEqual(new[]{"Accept","Decline"}),"Missing native binary choices.");
                if(i==0){
                    QuestPanel.StockPreview=true;var stock=ChooseSpeak(npc);
                    yield return Await(()=>stock.choiceButtons[1].isActiveAndEnabled,"Native portrait did not finish revealing the offer.",25);
                    yield return new WaitForSecondsRealtime(.4f);
                    RequireQuest(QuestPanel.Current==null&&stock.choiceButtons[1].isActiveAndEnabled,"Stock choices unavailable.");
                    yield return Capture("quest-offer-native.png",ok=>RequireQuest(ok,"Stock image missing."));
                    stock.HideDialogue();QuestPanel.StockPreview=false;yield return null;
                }
                var view=ChooseSpeak(npc);yield return null;view.Continue();
                yield return Await(()=>QuestPanel.Current?.Ready==true,"Interactive offer did not open for "+npc.CharacterInfo.name,60);
                var panel=QuestPanel.Current;
                RequireQuest(panel.Last.Value<string>("intent")=="offer"&&ql.Get(quest)==null,"Offer opening changed the quest.");
                lines.Add(new JObject{["request"]=panel.LastRequest.DeepClone(),["result"]=panel.Last.DeepClone()});
                if(i==0){
                    panel.DetailsButton.onClick.Invoke();yield return null;
                    trial["detailsAccessible"]=panel.DetailsButton.GetComponentInChildren<TextMeshProUGUI>().text=="Back to reply"&&panel.NativeTerms==start.Dialogue;
                    panel.DetailsButton.onClick.Invoke();yield return new WaitForSecondsRealtime(.35f);
                    panel.Portrait.ForceMeshUpdate();
                    RequireQuest(panel.Portrait.text.Contains(panel.NativeTerms)&&panel.Portrait.textInfo.characterCount>20&&
                        panel.Portrait.textInfo.characterInfo.Any(ch=>ch.isVisible),"The portrait did not render its original offer and invitation.");
                    yield return Capture("quest-offer-echoes.png",ok=>RequireQuest(ok,"Interactive image missing."));
                    RequireQuest((bool)trial["detailsAccessible"],"Native terms were lost.");
                }
                if(i==3){
                    // Leave while a real acceptance request is in flight. An abandoned screen cannot click a later offer.
                    panel.Editor.text="I accept the job.";panel.Editor.onSubmit.Invoke(panel.Editor.text);
                    trial["abandoned"]=panel.LastRequest.DeepClone();view.HideDialogue();yield return null;
                    view=ChooseSpeak(npc);yield return null;view.Continue();
                    yield return Await(()=>QuestPanel.Current?.Ready==true,"Failed to reopen the same unaccepted quest.");
                    panel=QuestPanel.Current;RequireQuest(ql.Get(quest)==null,"Stale acceptance started the quest.");
                    lines.Add(new JObject{["request"]=panel.LastRequest.DeepClone(),["result"]=panel.Last.DeepClone()});
                }
                float friendship=Friendship(npc);int markers=quest.questSteps[0].questLocations?.Count??0;
                for(int j=0;j<messages[i].Length;j++){
                    string words=messages[i][j];var previous=(string)panel.LastRequest["id"];
                    panel.Editor.text=words;panel.Editor.onSubmit.Invoke(panel.Editor.text);panel.Editor.onSubmit.Invoke(panel.Editor.text);
                    yield return Await(()=>QuestPanel.Current==null||panel.Ready&&(string)panel.LastRequest["id"]!=previous,"Quest turn timed out.",85);
                    var result=panel.Last;lines.Add(new JObject{["request"]=panel.LastRequest.DeepClone(),["result"]=result.DeepClone()});
                    string action=j+1<messages[i].Length?"continue":i==0||i==2?"accept":"decline";
                    RequireQuest((string)result["intent"]==intents[i][j]&&(string)result["action"]==action,"Wrong reaction to '"+words+"': "+result);
                    if(action=="continue")RequireQuest(QuestPanel.Current==panel&&ql.Get(quest)==null&&Friendship(npc)==friendship,"An open discussion changed game state.");
                }
                bool given=i==0||i==2;
                trial["journalCount"]=ql.CountQuestsInProgress(quest);
                trial["offerRemoved"]=!brain.ConditionalDialogues.Any(x=>ReferenceEquals(x.dialogueData,start));
                trial["friendshipDelta"]=Friendship(npc)-friendship;
                trial["markersBefore"]=markers;trial["markersAfter"]=quest.questSteps[0].questLocations?.Count??0;
                RequireQuest(QuestPanel.Current==null&&!view.isActiveAndEnabled,"The resolved portrait stayed open.");
                RequireQuest(given?(int)trial["journalCount"]==1:(bool)trial["offerRemoved"]&&ql.Get(quest)==null,"Native outcome was not confirmed.");
                if(i==2)RequireQuest((int)trial["markersAfter"]>0,"Pest-control callback did not set up its map marker.");
                if(given)accepted.Add(quest.Uid());trial["passed"]=true;
                yield return new WaitForSecondsRealtime(.5f);
            }
            string root=IsolatedGameRun.Active.Root;var snapshot=Path.Combine(root,"quest-reload");
            try{
                CaptureSlot(snapshot);yield return LoadSnapshot(snapshot,owners[0]);
                var list=MirthwoodPlayer.Instance.QuestList;
                var counts=new JArray(accepted.Select(key=>list.CountQuestsInProgress(key)));
                report["questReload"]=counts;RequireQuest(counts.All(x=>(int)x==1),"Quest journal changed on reload.");
            }finally{RemoveSnapshot(root,snapshot);}
            report["verifiedQuests"]=true;
        }

        IEnumerator DiscussWorldDuringOffer(AwarenessPlaytest play, NPCController npc, string words)
        {
            var player = MirthwoodPlayer.Instance;
            var chat = npc.GetComponent<NPCChatController>();
            yield return AwarenessPlaytest.Approach(npc.transform.position);
            var generated = player.QuestGenerator.GeneratePantryQuest(QuestType.helping,npc);
            var quest = generated.quest;
            var start = generated.dialogueStart;
            string terms = start.Dialogue;
            float friendship = Friendship(npc);
            RequireQuest(ReferenceEquals(chat.GetDialogueData(),start) && player.QuestList.Get(quest)==null,
                "The native game did not offer this generated quest.");
            var menu = ShowQuickMenu(npc);
            var labels = QuickLabels(menu);
            play.Record("quest-before",0,npc,new {terms,words,key=quest.Uid(),labels,friendship,
                nativeChoices=start.Choices.Select(s=>s.choiceText).ToArray()});
            SelectQuickAction(menu,"Speak");
            yield return null;
            var view = UnityEngine.Object.FindObjectOfType<DialogueView>();
            RequireQuest(view && view.isActiveAndEnabled,"Native Speak did not open the quest portrait.");
            view.Continue();
            yield return Await(()=>QuestPanel.Current?.Ready==true,"The offer did not reach the quest editor.",85);
            var panel = QuestPanel.Current;
            play.Record("quest-offer",0,npc,new {request=panel.LastRequest,result=panel.Last,nativeTerms=panel.NativeTerms});
            var objectives=(JArray)panel.LastRequest["offer"]["objectives"];
            var items=objectives.Values<string>().Where(x=>x.StartsWith("Gather ",StringComparison.Ordinal)).Select(x=>x.Substring(7)).ToArray();
            string opening = (string)panel.LastRequest["id"];
            float started = Time.realtimeSinceStartup;
            panel.Editor.text=words;
            panel.Editor.onSubmit.Invoke(words);
            yield return Await(()=>QuestPanel.Current!=panel || panel.Ready && (string)panel.LastRequest["id"]!=opening,
                "The incidental world question did not return.",85);
            RequireQuest(QuestPanel.Current==panel && (string)panel.Last["action"]=="continue" &&
                (string)panel.Last["intent"]=="question" && panel.NativeTerms==terms && start.Dialogue==terms &&
                ReferenceEquals(chat.GetDialogueData(),start) && player.QuestList.Get(quest)==null && Friendship(npc)==friendship,
                "An incidental question changed the native offer, eligibility or effects.");
            string reply = (string)panel.Last["reply"];
            string rendered = null;
            yield return Await(()=>{
                panel.Portrait.ForceMeshUpdate();
                rendered=panel.Portrait.GetParsedText().Trim();
                return rendered==new BubbleText(reply,false).Plain.Trim();
            },"The portrait did not display the incidental answer.",24);
            play.Record("quest-portrait",0,npc,new {request=panel.LastRequest,result=panel.Last,rendered,terms,
                elapsedMs=Mathf.RoundToInt((Time.realtimeSinceStartup-started)*1000)});
            yield return play.Screenshot("quest-world-answer");
            string earlier=(string)panel.LastRequest["id"];
            panel.Editor.text="I'll attend the Grand Joust.";
            panel.Editor.onSubmit.Invoke(panel.Editor.text);
            yield return Await(()=>QuestPanel.Current!=panel || panel.Ready && (string)panel.LastRequest["id"]!=earlier,
                "No answer to the player's intention to attend the joust.",85);
            string eventWords=(string)panel.Last?["reply"];
            string onScreen=null;
            if(QuestPanel.Current==panel && eventWords!=null){
                yield return Await(()=>{
                    panel.Portrait.ForceMeshUpdate();
                    onScreen=panel.Portrait.GetParsedText().Trim();
                    return onScreen==new BubbleText(eventWords,false).Plain.Trim();
                },"No visible answer to the attendance statement.",24);
            }
            play.Record("quest-attendance-intention",0,npc,new {request=panel.LastRequest,result=panel.Last,
                rendered=onScreen,journal=player.QuestList.CountQuestsInProgress(quest),originalOffer=ReferenceEquals(chat.GetDialogueData(),start)});
            RequireQuest(QuestPanel.Current==panel && (string)panel.Last["action"]=="continue" && player.QuestList.Get(quest)==null &&
                ReferenceEquals(chat.GetDialogueData(),start) && panel.NativeTerms==terms && Friendship(npc)==friendship,
                "Agreeing to attend the joust resolved or altered the unrelated job.");
            if(play.RepeatUnrelatedOffer){
                RequireQuest(items.Length>0,"The generated job has no named gathering objective.");
                string proposal="I will gather "+items[0]+" molds if you pay more.";
                for(int i=0;i<2;i++){
                    string prior=(string)panel.LastRequest["id"];
                    panel.Editor.text=proposal;panel.Editor.onSubmit.Invoke(proposal);
                    yield return Await(()=>QuestPanel.Current!=panel || panel.Ready && (string)panel.LastRequest["id"]!=prior,
                        "The unrelated proposal did not receive an answer.",85);
                    string visible=null;
                    if(QuestPanel.Current==panel) yield return Await(()=>{
                        panel.Portrait.ForceMeshUpdate();visible=panel.Portrait.GetParsedText().Trim();
                        return visible==new BubbleText((string)panel.Last["reply"],false).Plain.Trim();
                    },"The quest portrait did not show the reply to the unrelated offer.",24);
                    play.Record("quest-unrelated-proposal",i,npc,new {request=panel.LastRequest,result=panel.Last,rendered=visible,
                        journal=player.QuestList.CountQuestsInProgress(quest),originalOffer=ReferenceEquals(chat.GetDialogueData(),start),terms,friendship=Friendship(npc)});
                    RequireQuest(QuestPanel.Current==panel && (string)panel.Last["action"]=="continue" &&
                        (bool?)panel.Last["warned"]!=true && player.QuestList.Get(quest)==null &&
                        ReferenceEquals(chat.GetDialogueData(),start) && panel.NativeTerms==terms && Friendship(npc)==friendship,
                        "A proposal about a different item altered or warned against the original job.");
                }
            }
            if(play.QuestDecision.Contains("{items}"))RequireQuest(items.Length>0,"Cannot identify the goods in the native offer.");
            panel.Editor.text=play.QuestDecision.Replace("{items}",string.Join(" and ",items));
            panel.Editor.onSubmit.Invoke(panel.Editor.text);
            yield return Await(()=>QuestPanel.Current==null,"The native decision did not close the offer.",85);
            int count=player.QuestList.CountQuestsInProgress(quest);
            play.Record("quest-outcome",0,npc,new {count,key=quest.Uid(),request=panel.LastRequest,result=panel.Last,
                friendshipDelta=Friendship(npc)-friendship,terms=start.Dialogue,
                removed=!chat.ConditionalDialogues.Any(x=>ReferenceEquals(x.dialogueData,start))});
            bool correct=play.DeclineQuest ? count==0 && (string)panel.Last["action"]=="decline" &&
                !chat.ConditionalDialogues.Any(x=>ReferenceEquals(x.dialogueData,start)) : count==1 && (string)panel.Last["action"]=="accept";
            RequireQuest(correct,"The native journal and the player's explicit decision differ.");
        }
    }
}
