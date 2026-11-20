using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using UnityEngine;
using _Mirthwood.Scripts.Demo.Chat;
using _Mirthwood.Scripts.Demo.Combat;
using _Mirthwood.Scripts.Demo.Menus;
using _Mirthwood.Scripts.Demo.ScriptableObjects;
using _Mirthwood.Scripts.Demo.Tests;
using _Mirthwood.Scripts.Demo.Weather;

namespace Mirthwood.Echoes
{
    internal sealed partial class NativeConversation
    {
        static NPCChatController compared;
        static FieldInfo receiverOwner, recoveryOwner;
        static int decisionSeed, appetiteDebits;
        static bool holdRecovery, receivedCard;
        static string returnedCard;
        static bool stockRound;
        internal static bool UsingStockChat=>IsolatedGameRun.Active!=null&&stockRound;

        static void ChooseStockTalk(PlayerChat player,NPCChatController brain)
        {
            Plugin.Selected.SetValue(player,brain);player.chatTarget=brain;
            brain.isChatReady=true;brain.resetChatActiveTime=DayNightCycle.Instance.Now+1;
            Choices(player).Single(o=>o.Name=="Talk").Callback(1);
        }
        static IEnumerator SelectStockCard(PlayerChat player,NPCChatController brain,DnaData.Dna card)
        {
            var menu=ChatMenuController.Instance;
            var animating=AccessTools.Field(typeof(ChatMenuController),"_isAnimating");
            var initialized=AccessTools.Field(typeof(ChatMenuController),"_playerChat");
            bool ReadyForCard()=>menu!=null&&ReferenceEquals(initialized.GetValue(menu),player)&&menu.topLevelMenu.activeInHierarchy&&!(bool)animating.GetValue(menu);
            float deadline=Time.realtimeSinceStartup+15;
            while(!ReadyForCard()&&Time.realtimeSinceStartup<deadline)yield return null;
            if(!ReadyForCard())throw new InvalidOperationException(menu==null?"The stock card menu closed.":string.Format(
                "Stock topic menu unavailable for {0} (current player {1}, top deck {2}, animating {3}, time scale {4}).",
                brain.Character.CharacterInfo.name,ReferenceEquals(initialized.GetValue(menu),player),menu.topLevelMenu.activeInHierarchy,animating.GetValue(menu),Time.timeScale));
            var categories=(DnaData.Dna.DnaCategories[])Enum.GetValues(typeof(DnaData.Dna.DnaCategories));
            player.chatTarget=brain;
            menu.deckCards[Array.IndexOf(categories,card.category)-1].Card.onClick.Invoke();
            yield return Await(()=>menu.subLevelMenu.activeInHierarchy&&!(bool)animating.GetValue(menu),"Stock Chat deck did not open.",10);
            var choice=menu.subChoices.Single(c=>c.Card.gameObject.activeInHierarchy&&c.Label.text==card.DnaNameLocalized);
            if(!choice.Card.interactable)throw new InvalidOperationException("The comparison requested a locked card.");
            choice.Card.onClick.Invoke();
        }

        // The installed game runs both sides. Only the copied-save comparison controls random draws
        // at the native decision and holds passive five-second recovery during matched sequences.
        static void SeedNativeDecision(object __instance, out UnityEngine.Random.State __state)
        {
            __state=UnityEngine.Random.state;
            if(compared!=null && ReferenceEquals(receiverOwner.GetValue(__instance),compared))
                UnityEngine.Random.InitState(decisionSeed);
        }
        static void RestoreDecision(object __instance, UnityEngine.Random.State __state)
        {
            if(compared!=null && ReferenceEquals(receiverOwner.GetValue(__instance),compared))UnityEngine.Random.state=__state;
        }
        static bool HoldPassiveTick(object __instance,ref bool __result)
        {
            if(!holdRecovery || compared==null || !ReferenceEquals(recoveryOwner.GetValue(__instance),compared.Character))return true;
            __result=true;return false;
        }
        static void ObserveReturn(PlayerChat __instance, DnaData.Dna action, ChatController other)
        {
            if(compared==null || other!=compared || __instance!=PlayerCharacterController.Instance.GetComponent<PlayerChat>())return;
            receivedCard=true;returnedCard=action?.name;
        }
        static void CountDebit(NPCChatController __instance){if(__instance==compared)appetiteDebits++;}
        static bool KeepSelectedPair(PlayerChat __instance)=>!holdRecovery||compared==null||__instance!=PlayerCharacterController.Instance.GetComponent<PlayerChat>();
        static float Appetite(NPCController npc)=>npc.CharacterInfo.personality.values[5];
        static TalkResult PairedCard(NPCController npc,int sample,string topic,float previousRun=0,int costPercent=100)
        {
            var outbox=new TalkExchange(new string('b',32),(sample+1).ToString("x32"));
            var context=InteractionHooks.Cards(npc).Any(card=>card.name==topic)?NativeCues.Binding(null,npc):null;
            string id=(string)outbox.Accept("Paired "+topic,previousRun,context)["id"];
            var result=outbox.Apply(id,topic,npc,costPercent);
            var replay=outbox.Apply(id,topic,npc,costPercent==100?25:100);
            float accepted=outbox.Acknowledge(id);
            if(result.Reaction!=replay.Reaction||!JToken.DeepEquals(result.Handoff,replay.Handoff)||
                !Mathf.Approximately(result.PositiveRun,replay.PositiveRun)||!Mathf.Approximately(result.PositiveRun,accepted))
                throw new InvalidOperationException("Replaying changed the native reaction or return card.");
            return result;
        }

        static IEnumerator TalkAfterRest(NPCController npc,float measuredRecovery,JObject proof)
        {
            // Start within one observed idle tick of this seeded willingness decision in the copy.
            decisionSeed=29;sampleSeed=35;
            var prior=UnityEngine.Random.state;UnityEngine.Random.InitState(decisionSeed);
            float threshold=UnityEngine.Random.value*.5f-.5f;UnityEngine.Random.state=prior;
            holdRecovery=true;
            float before=threshold-measuredRecovery*.5f;npc.CharacterInfo.personality.values[5]=before;
            appetiteDebits=observedScores=0;
            var first=PairedCard(npc,5,"Leisure");
            bool declined=(string)first.Handoff["kind"]=="ended"&&appetiteDebits==0&&observedScores==1;
            holdRecovery=false;
            yield return Await(()=>Appetite(npc)>threshold,"Native desire did not recover past the same willingness decision.",18);
            float rested=Appetite(npc);appetiteDebits=observedScores=0;
            var next=PairedCard(npc,5,"Leisure");
            proof["afterRest"]=JObject.FromObject(new{resident=npc.CharacterInfo.name,threshold,before,rested,
                first=first.Handoff,second=next.Handoff,desireAfterAnswer=Appetite(npc),debits=appetiteDebits,
                passed=declined&&(string)next.Handoff["kind"]=="followup"&&appetiteDebits==1&&observedScores==1&&Appetite(npc)<rested});
        }

        IEnumerator BalanceCases(JObject report,string[] owners)
        {
            if(!InCopy())yield break;
            var hooks=new Harmony("mirthwood.echoes.harness.social");
            var signature=new[]{typeof(DnaData.Dna),typeof(ChatController),typeof(bool)};
            var decision=AccessTools.EnumeratorMoveNext(AccessTools.Method(typeof(NPCChatController),"ReceiveChat",signature));
            var passive=AccessTools.EnumeratorMoveNext(AccessTools.Method(typeof(NPCController),"UpdateSocial"));
            receiverOwner=AccessTools.Field(decision.DeclaringType,"<>4__this");
            recoveryOwner=AccessTools.Field(passive.DeclaringType,"<>4__this");
            var sessions=new JArray();report["balance"]=sessions;
            var run=IsolatedGameRun.Active;
            string baseline=System.IO.Path.Combine(run.Root,"BeforeSocialBalance");
            try{
                hooks.Patch(AccessTools.Method(typeof(DnaTesting),"ScoreAction"),prefix:new HarmonyMethod(typeof(NativeConversation),nameof(CountScore)),
                    postfix:new HarmonyMethod(typeof(NativeConversation),nameof(RestoreRandom)));
                hooks.Patch(decision,new HarmonyMethod(typeof(NativeConversation),nameof(SeedNativeDecision)),new HarmonyMethod(typeof(NativeConversation),nameof(RestoreDecision)));
                hooks.Patch(passive,new HarmonyMethod(typeof(NativeConversation),nameof(HoldPassiveTick)));
                // A distant resident selected by the test must not be replaced by a collider nearer the spawn point.
                hooks.Patch(AccessTools.Method(typeof(PlayerChat),"SelectClosestNPC"),prefix:new HarmonyMethod(typeof(NativeConversation),nameof(KeepSelectedPair)));
                hooks.Patch(AccessTools.Method(typeof(PlayerChat),"ReceiveChat",signature),postfix:new HarmonyMethod(typeof(NativeConversation),nameof(ObserveReturn)));
                hooks.Patch(AccessTools.Method(typeof(NPCChatController),"DecreaseSocialNeed"),postfix:new HarmonyMethod(typeof(NativeConversation),nameof(CountDebit)));
                if(owners.Length!=3)throw new InvalidOperationException("Three saved residents are needed for the comparison.");
                CaptureSlot(baseline);
                var otherCards=new[]{"Rumors","Cheer Up","Complain"};var cardChecks=new JArray();report["cardBalance"]=cardChecks;
                for(int i=0;i<4+otherCards.Length;i++){
                    compared=null;holdRecovery=false;
                    bool checkCard=i>=4;
                    if(i>0&&i<=4)yield return LoadSnapshot(baseline,owners[i%3]);
                    var player=PlayerCharacterController.Instance.GetComponent<PlayerChat>();
                    var npc=FindResident(owners[i%3]);var brain=npc.GetComponent<NPCChatController>();
                    player.transform.position=npc.transform.position+Vector3.right*.65f;
                    yield return new WaitForSecondsRealtime(.65f);
                    var info=npc.CharacterInfo;var relation=info.relationships[player.Character.CharacterInfo.id];
                    var originalMood=info.mood;var originalDna=(float[])info.personality.values.Clone();
                    var initialFriend=relation.friendship;var angles=new[]{"Leisure","Small Talk","Deep","Dumb","Intellectual"};
                    var session=new JObject{["resident"]=info.name,["startingDesire"]=checkCard?.7f:new[]{.65f,-.85f,-.25f,-.85f}[i]};(checkCard?cardChecks:sessions).Add(session);
                    compared=brain;holdRecovery=true;
                    void RestoreCase(float desire){
                        info.mood=originalMood;info.personality.values=(float[])originalDna.Clone();info.personality.values[5]=desire;
                        relation.friendship=initialFriend;info.UpdateCombinedDna();
                    }
                    for(int pass=0;pass<2;pass++){
                        stockRound=pass==0;
                        var trace=new JArray();session[pass==0?"stock":"echoes"]=trace;
                        RestoreCase((float)session["startingDesire"]);
                        Plugin.Selected.SetValue(player,brain);player.chatTarget=brain;
                        player.PrepareForChat(brain);brain.PrepareForChat(player);
                        if(pass==0){
                            ChooseStockTalk(player,brain);
                            yield return Await(()=>ChatMenuController.IsOpen(),"The stock Talk option did not open.");
                        }
                        for(int j=0;j<(checkCard?1:10);j++){
                            string topic=checkCard?otherCards[i-4]:(i==0||i==3)?"Leisure":angles[j%angles.Length];var card=brain.dnaData.chatActions.Single(a=>a.name==topic);
                            decisionSeed=29+j;sampleSeed=i==0||i==3||j%2==0?35:32;observedScores=0;appetiteDebits=0;receivedCard=false;returnedCard=null;
                            float desire=Appetite(npc),before=Friendship(npc);string handoff,cue=null;
                            if(pass==0){
                                // Exercise the stock buttons on entry. For subsequent cards use the same
                                // PlayerChat command as those buttons, independent of presentation transitions.
                                if((i==0||checkCard)&&j==0)yield return SelectStockCard(player,brain,card);
                                else{player.chatTarget=brain;player.SendInteraction(card);}
                                yield return Await(()=>receivedCard,"Stock card did not return for "+info.name,18);
                                handoff=returnedCard==null?"ended":"followup";
                                if(i==1&&handoff=="ended"&&ChatMenuController.IsOpen()){
                                    var label=(TMPro.TextMeshProUGUI)AccessTools.Field(typeof(ChatMenuController),"conversationText").GetValue(ChatMenuController.Instance);
                                    session["stockEndingText"]=label.text;
                                    yield return Capture("latest-stock-ending.png",ok=>session["stockEndingCapture"]=ok);
                                }
                            }else{
                                var result=PairedCard(npc,i,topic);
                                handoff=(string)result.Handoff["kind"];cue=(string)result.Handoff["cue"];
                                if((string)result.Handoff["action"]!=returnedCard || string.IsNullOrWhiteSpace(cue))throw new InvalidOperationException("The Echoes cue did not reflect the card.");
                            }
                            var row=JObject.FromObject(new{topic,before,after=Friendship(npc),desireBefore=desire,desireAfter=Appetite(npc),handoff,
                                card=returnedCard,debits=appetiteDebits,scores=observedScores,mood=info.mood.values.ToArray()});trace.Add(row);
                            if(pass==1)((JArray)session["cues"]).Add(cue);
                            if(handoff=="ended")break;
                            if(pass==0)yield return new WaitForSeconds(3.25f);
                        }
                        if(pass==0){
                            // Stock handles a null card by backing out of its menu after the native display delay.
                            yield return new WaitForSeconds(3.5f);
                            session["stockMenuClosed"]=!ChatMenuController.IsOpen();
                            if(ChatMenuController.IsOpen()){
                                player.ExitChat();
                                yield return Await(()=>!ChatMenuController.IsOpen(),"Could not leave the native card menu.",8);
                            }
                            Plugin.Selected.SetValue(player,brain);player.chatTarget=brain;
                            session["talkOfferedAfterEnding"]=Choices(player).Any(o=>o.Name=="Talk");
                            session["cues"]=new JArray();
                        }
                    }
                    var stock=(JArray)session["stock"];var echoes=(JArray)session["echoes"];
                    session["matched"]=JToken.DeepEquals(stock,echoes)&&stock.Count>0&&(checkCard||(string)stock.Last["handoff"]=="ended")&&
                        stock.All(t=>(int)t["scores"]==1 && (int)t["debits"]==((string)t["handoff"]=="ended"?0:1));
                    if(!(bool)session["matched"])throw new InvalidOperationException("Stock and Echoes differed for "+info.name);
                    holdRecovery=false;
                    if(checkCard){RestoreCase(originalDna[5]);continue;}
                    float spent=Appetite(npc);
                    yield return Await(()=>Appetite(npc)>spent,"The resident's native social desire did not recover.",18);
                    session["recoveredFrom"]=spent;session["recoveredTo"]=Appetite(npc);
                    if(i==0){
                        holdRecovery=true;
                        MeasureDesireScale(npc,RestoreCase,stock,(float)session["startingDesire"],report);
                        holdRecovery=false;
                    }
                    if(i==3)yield return TalkAfterRest(npc,Appetite(npc)-spent,report);
                    info.mood=originalMood;info.personality.values=originalDna;relation.friendship=initialFriend;info.UpdateCombinedDna();
                }
            }finally{stockRound=false;holdRecovery=false;compared=null;hooks.UnpatchSelf();RemoveSnapshot(run.Root,baseline);}
        }
    }
}
