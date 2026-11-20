using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Lowscope.Saving;
using Newtonsoft.Json.Linq;
using UnityEngine;
using _Mirthwood.Scripts.Demo;
using _Mirthwood.Scripts.Demo.Chat;
using _Mirthwood.Scripts.Demo.Combat;
using _Mirthwood.Scripts.Demo.GameModules;
using _Mirthwood.Scripts.Demo.Items;
using _Mirthwood.Scripts.Demo.Menus;
using _Mirthwood.Scripts.Demo.ScriptableObjects;
using _Mirthwood.Scripts.Demo.Utils;
using _Mirthwood.Scripts.Runtime.Demo.Menus;

namespace Mirthwood.Echoes
{
    internal sealed partial class NativeConversation
    {
        internal IEnumerator RunInteractions(JObject report)
        {
            if(!InCopy()||IsolatedGameRun.Active.TalkAddress==null)yield break;
            var trace=new JArray();NativeCues.Trace=trace;report["cases"]=new JArray();report["turns"]=new JArray();
            report["hooks"]=new JArray(InteractionHooks.Installed);
            try{yield return CheckedProbe(report,InteractionCases(report));}
            finally{InteractionHooks.Enabled=true;report["events"]=trace;NativeCues.Trace=null;}
        }
        static void Case(JObject report,string name,Action act){
            var row=new JObject{["name"]=name};((JArray)report["cases"]).Add(row);
            try{act();row["passed"]=true;}catch(Exception e){row["passed"]=false;row["detail"]=e.GetBaseException().ToString();}
        }
        static void Require(bool ok,string text){if(!ok)throw new InvalidOperationException(text);}
        static ContextMenuUI ShowQuickMenu(NPCController npc)
        {
            var chat=PlayerCharacterController.Instance.GetComponent<PlayerChat>();
            var resident=npc.GetComponent<NPCChatController>();
            Plugin.Selected.SetValue(chat,resident);
            chat.chatTarget=resident;
            AccessTools.Method(typeof(PlayerChat),"OpenQuickChat").Invoke(chat,null);
            var menu=chat.ContextMenuUI;
            menu.ShowContextMenu(1);
            return menu;
        }
        static string[] QuickLabels(ContextMenuUI menu) =>
            ((IContextMenu.Option[])AccessTools.Field(typeof(ContextMenuUI),"options").GetValue(menu)).Select(o=>o.Name).ToArray();
        static void SelectQuickAction(ContextMenuUI menu,string name,int player=1)
        {
            var labels=QuickLabels(menu);
            int index=Array.IndexOf(labels,name);
            Require(index>=0&&index<4&&labels.Length<=4,"Unreachable "+name+" in "+string.Join(", ",labels));
            menu.SelectInteractOption(index,player);
        }
        static T ComponentInScene<T>() where T:Component=>Resources.FindObjectsOfTypeAll<T>().FirstOrDefault(x=>x.gameObject.scene.IsValid());
        static void GiveItemInCopy(NPCController npc,ItemDataBase item,bool supply)
        {
            var player=PlayerCharacterController.Instance;
            if(supply&&player.inventory.ItemCount(item)==0)Require(player.AddItem(item,1),"Could not stock "+item.name+".");
            SimpleMenu<StorageUIWindow>.Show(1);var window=StorageUIWindow.Instance;Require(window!=null,"Missing gift window.");
            window.SetGiftContainer(npc.GetComponent<NPCChatController>());
            AccessTools.Method(typeof(StorageUIWindow),"OnContainerItemSubmitted").Invoke(window,new object[]{new Item(item,1)});
            GameState.UnPause();GameState.SetCurrentState(1,GameState.State.Gameplay);
        }
        static int Recorded(string kind)=>NativeCues.Trace.Count(x=>(string)x["kind"]==kind);
        static string LastFact(string kind)=>(string)NativeCues.Trace.Last(x=>(string)x["kind"]==kind)["people"][0]["description"];
        IEnumerator ReadCue(JObject report,NPCController npc,string words,bool answerOpener=false)
        {
            if(!answerOpener)yield return Await(()=>NativeCues.Pending==0,"Observation delivery did not drain.",20);
            var brain=npc.GetComponent<NPCChatController>();
            GameState.SetCurrentGeneralState(GameState.State.Gameplay);GameState.SetCurrentState(1,GameState.State.Gameplay);GameState.AllowInput=true;GameState.UnPause();
            npc.transform.position=PlayerCharacterController.Instance.transform.position+Vector3.right;
            if(answerOpener){
                TickPassingLine();
                yield return Await(()=>passing?.Speaker==npc&&passing.Bubble!=null,"No passing line from "+npc.CharacterInfo.name+" (queued="+passing?.Line+", in range="+InReplyRange(npc)+", open="+Visible+").",6);
                Require(!Visible&&!GameState.IsPaused&&passing.Bubble.UsesNativeStyle&&passing.Bubble.AnchoredTo(npc.transform),"The opening line took over gameplay.");
                var openers=(JArray)(report["openers"]??(report["openers"]=new JArray()));
                openers.Add(new JObject{["npc"]=npc.CharacterInfo.name,["id"]=passing.Id,["kind"]=passing.Kind,["line"]=passing.Line});
                string spokenLine=passing.Line;
                var quick=ShowQuickMenu(npc);
                if(openers.Count==1)yield return new WaitForSeconds(.15f);
                TickPassingLine();
                Case(report,passing.Kind+" leaves the native quick buttons visible",()=>{
                    var occupied=passing.Bubble.QuickMenuBounds(quick);
                    Require(occupied.HasValue&&!passing.Bubble.Footprint.Overlaps(occupied.Value)&&passing.Bubble.FitsScreen(),"The quick buttons cover the opening line.");
                });
                if(openers.Count==1)yield return Capture("latest-passing-opener.png",ok=>report["passingCapture"]=ok);
                if(openers.Count==1){
                    Require(IsolatedGameRun.PostReturn(),"Could not send Enter to the copied game's window.");
                    yield return Await(()=>Visible,"The visible gift prompt did not accept Enter.",3);
                }else SelectQuickAction(ShowQuickMenu(npc),"Talk");
                playerBubble.Input.text="*I pause to think about what you just said.* I have something I'd like to ask you.";
                Follow();
                Case(report,openers.Count==1?"Gift reaction stays readable while typing":"Work receipt stays readable while typing",()=>{
                    Require(GameState.IsPaused&&npcBubble.Visible&&playerBubble.Editing,"Both speech bubbles must be visible during the draft.");
                    Require(npcBubble.VisibleText==new BubbleText(spokenLine,false).Plain,"The resident's visible words changed when replying.");
                    Require(!playerBubble.Footprint.Overlaps(npcBubble.Footprint)&&playerBubble.FitsScreen()&&npcBubble.FitsScreen(),"The two speech bubbles overlap or clip the screen.");
                });
                if(openers.Count==1)yield return Capture("latest-paired-speech.png",ok=>report["pairedCapture"]=ok);
            }else ChooseTalkInCopy(npc);
            yield return Await(()=>Ready,"Could not open Talk with "+npc.CharacterInfo.name+" (enabled="+brain.chatEnabled+", authored dialogue="+(brain.GetDialogueData()!=null)+", visible="+Visible+").",15);
            string who=actor,world=campaign;var book=exchange;float start=Time.realtimeSinceStartup;
            playerBubble.Input.text=words;playerBubble.Input.onSubmit.Invoke(words);
            var pending=book.Pending();
            Require(pending!=null,"No persisted Talk.");
            yield return Await(()=>phase==Phase.Reading||phase==Phase.Draft,"No spoken line for "+words,85);
            Require(phase==Phase.Reading,"The Talk request returned to editing for "+words);
            Require(Visible&&npcBubble!=null,"The conversation closed before its first line for "+words);
            string spoken=npcBubble.VisibleText;float visible=Time.realtimeSinceStartup-start;
            yield return Await(()=>!busy,"The final Talk receipt did not arrive.",20);
            var row=new JObject{["id"]=pending["id"],["text"]=words,["campaign"]=world,["actor"]=who,["context"]=pending["context"].DeepClone(),
                ["name"]=npc.CharacterInfo.name,["bubble"]=spoken,["firstMs"]=(int)(visible*1000),["totalMs"]=(int)((Time.realtimeSinceStartup-start)*1000)};
            var output=(JArray)report["turns"];
            if(output.Count==0)yield return Capture("latest-gift-followup.png",ok=>report["giftFollowUp"]=ok);
            output.Add(row);Close();
        }
        IEnumerator InteractionCases(JObject report)
        {
            var owners=ComparisonResidents();Require(owners.Length>=3,"Three saved residents were required.");
            var npc=FindResident(owners[0]);var next=FindResident(owners[1]);var player=PlayerCharacterController.Instance;
            string snapshot=Path.Combine(IsolatedGameRun.Active.Root,"CueMorning");
            CaptureSlot(snapshot);string morning=NativeCues.Head;
            var gifts=ItemGiftData.Instance.giftData[(int)npc.CharacterInfo.Class];
            var good=gifts.likeItemList[npc.CharacterInfo.favoriteItem];var bad=gifts.dislikeItemList[npc.CharacterInfo.leastFavoriteItem];
            try{
                Case(report,"Favorite gift matches native stock",()=>{
                    var relation=npc.CharacterInfo.relationships[player.CharacterInfo.id];
                    float old=relation.friendship,rom=relation.romance;
                    InteractionHooks.Enabled=false;
                    float stock;try{GiveItemInCopy(npc,good,true);stock=relation.friendship-old;}finally{InteractionHooks.Enabled=true;}
                    relation.friendship=old;relation.romance=rom;
                    int count=Recorded("gift");GiveItemInCopy(npc,good,true);
                    Require(Recorded("gift")==count+1&&LastFact("gift").Contains("strongly positive")&&Mathf.Approximately(relation.friendship-old,stock),"Gift observation or native friendship differs.");
                });
                Case(report,"Missing inventory item and shared decline reaction",()=>{
                    int count=Recorded("gift");int qty=player.inventory.ItemCount(good);if(qty>0)player.inventory.RemoveItem(good,qty);
                    string existing=passing?.Id;
                    GiveItemInCopy(npc,good,false);
                    npc.GetComponent<NPCChatController>().GiftReaction(-1,player.ChatController);
                    Require(Recorded("gift")==count&&passing?.Id==existing,"An untransferred item produced a gift or reply prompt.");
                });
                Case(report,"Unlisted gift gets one actual reaction",()=>{
                    var other=Resources.FindObjectsOfTypeAll<ItemData>().First(x=>x&&x!=good&&x!=bad&&!gifts.likeItemList.Contains(x)&&!gifts.dislikeItemList.Contains(x)&&player.inventory.CanAddItem(x,1));
                    int count=Recorded("gift");GiveItemInCopy(npc,other,true);
                    Require(Recorded("gift")==count+1&&LastFact("gift").Contains("reaction"),"Missing actual unlisted reaction.");
                });
                Case(report,"Disliked gift belongs to its recipient",()=>{
                    GiveItemInCopy(npc,bad,true);Require(LastFact("gift").Contains("strongly negative"),"Dislike lost.");
                    Require(NativeCues.Take(next)==null,"A bystander received the gift cue.");
                    npc.transform.position=player.transform.position+Vector3.right;
                    GiveItemInCopy(npc,bad,true);
                });
                yield return new WaitForSeconds(2.35f);
                yield return Capture("latest-gift-native.png",ok=>report["giftReference"]=ok);
                yield return ReadCue(report,npc,"How did you feel about the gift I just gave you?",true);
                Case(report,"Gift quotation came from the displayed recipient callback",()=>{
                    var bound=((JArray)report["turns"]).Last["context"];
                    Require((string)bound["remark"]?["kind"]=="gift"&&(string)bound["event"]==(string)NativeCues.Trace.Last(e=>(string)e["kind"]=="gift")["id"],"No matching displayed gift line.");
                });
                Case(report,"Five native hire purchases and actual gold",()=>{
                    SimpleMenu<HireUIWindow>.Show(1);var hire=HireUIWindow.Instance;Require(hire,"Missing hire menu.");
                    int funds=player.inventory.gold;player.AddGold(-funds);hire.Init(next);
                    var button=(UnityEngine.UI.Button)AccessTools.Field(typeof(HireUIWindow),"miningButton").GetValue(hire);Require(!button.interactable,"Unaffordable hire available.");
                    int cancelled=Recorded("hire");hire.OnBackPressed();Require(cancelled==Recorded("hire"),"Cancelled hire was charged.");player.AddGold(funds+5000);
                    foreach(var job in new[]{"Farmhand","Mining","Woodcutting","Hunting","Foraging"}){
                        AccessTools.Field(typeof(NPCController),"_isHired").SetValue(next,false);
                        SimpleMenu<HireUIWindow>.Show(1);hire=HireUIWindow.Instance;hire.Init(next);int price=(int)AccessTools.Field(typeof(HireUIWindow),"price").GetValue(hire);
                        int before=player.inventory.gold,count=Recorded("hire");AccessTools.Method(typeof(HireUIWindow),"On"+job+"Pressed").Invoke(hire,null);
                        Require(before-player.inventory.gold==price&&next.GetCurrentHiring().ToString()==job&&Recorded("hire")==count+1,"Mismatched "+job+" hire.");
                    }
                    GameState.UnPause();GameState.SetCurrentState(1,GameState.State.Gameplay);
                });
                Case(report,"Farmhand items in progress and collected",()=>{
                    next.transform.position=player.transform.position+Vector3.right;
                    next.SetHiringData(CharacterData.Character.HiringTypes.Farmhand);
                    var product=Resources.FindObjectsOfTypeAll<ItemData>().First(x=>x&&x.ItemNameLocalized=="Flour"&&MirthwoodPlayer.Instance.QuestGenerator.GetItemDataByName(x.name)==x);
                    next.AddToHiringItemList(product.name,2);AccessTools.Field(typeof(NPCController),"_readyToCollect").SetValue(next,true);
                    var flour=product;int before=player.inventory.ItemCount(flour),seen=Recorded("collection");
                    var relationships=next.CharacterInfo.relationships;
                    var giftTime=AccessTools.Field(typeof(Relationships),"_lastGiftTime");
                    var originalGiftTime=giftTime.GetValue(relationships);
                    var brain=next.GetComponent<NPCChatController>();
                    bool ready=brain.isChatReady;
                    try{
                        giftTime.SetValue(relationships,_Mirthwood.Scripts.Demo.Weather.DayNightCycle.Instance.Now-1);
                        brain.isChatReady=false;
                        var quick=ShowQuickMenu(next);
                        var first=QuickLabels(quick);
                        Require(first.SequenceEqual(new[]{"Talk","Greet","Jeer","Next"}),"Crowded first page: "+string.Join(", ",first));
                        SelectQuickAction(quick,"Next",2);
                        Require(QuickLabels(quick).SequenceEqual(first),"The second player changed the first player's menu.");
                        SelectQuickAction(quick,"Next");
                        var second=QuickLabels(quick);
                        Require(second.Contains("Gift")&&second.Contains("Collect")&&second.Last()=="Back","Missing native work or gift action.");
                        SelectQuickAction(quick,"Back");
                        Require(QuickLabels(quick).SequenceEqual(first),"Back did not restore the social actions.");
                        SelectQuickAction(quick,"Next");
                        SelectQuickAction(quick,"Collect");
                        report["quickMenuPages"]=new JArray(new JArray(first),new JArray(second));
                    }finally{giftTime.SetValue(relationships,originalGiftTime);brain.isChatReady=ready;}
                    Require(Recorded("collection")==seen+1&&LastFact("collection").Contains("2 Flour")&&player.inventory.ItemCount(flour)==before+2,"The collection was not credited exactly once.");
                    Require(passing?.Speaker==next&&InReplyRange(next),"The work handover did not queue its nearby worker's line.");
                    var menu=PlayerCharacterController.Instance.GetComponent<PlayerChat>();Plugin.Selected.SetValue(menu,next.GetComponent<NPCChatController>());Require(!Choices(menu).Any(o=>o.Name=="Collect"),"Collect still available after payout.");
                });
                yield return ReadCue(report,next,"What did I pick up from your work?",true);
                Case(report,"Private two-resident recall without an invented transcript",()=>{
                    var a=npc.GetComponent<NPCChatController>();var b=next.GetComponent<NPCChatController>();var card=a.dnaData.chatActions.First(x=>x.name=="Small Talk");
                    int n=Recorded("social");AccessTools.Method(typeof(NPCChatController),"ReceiveResponse").Invoke(a,new object[]{card,null,b});
                    var people=NativeCues.Trace.Last(x=>(string)x["kind"]=="social")["people"] as JArray;
                    Require(Recorded("social")==n+1&&people.Count==2&&(string)people[0]["action"]=="Small Talk","Participants or initiator missing.");
                });
                var outsider=FindResident(owners[2]);
                yield return ReadCue(report,outsider,"Were you there when I gave that other resident a gift? What was it?");
                yield return StoryCases(report,npc,next);
                Case(report,"Full inventory produces an observed work drop",()=>{
                    GameState.SetCurrentGeneralState(GameState.State.Gameplay);GameState.SetCurrentState(1,GameState.State.Gameplay);GameState.UnPause();
                    var ingredient=Resources.FindObjectsOfTypeAll<ItemData>().First(x=>x&&x.ItemNameLocalized=="Flour"&&MirthwoodPlayer.Instance.QuestGenerator.GetItemDataByName(x.name)==x);
                    var bag=player.inventory;
                    for(int i=0;i<128&&bag.CanAddItem(ingredient,2);i++){
                        if(bag.AddItemPartial(ingredient,Math.Max(2,ingredient.maxStackSize),true,true)==0)break;
                    }
                    Require(!bag.CanAddItem(ingredient,2),"Could not fill the test pack.");
                    next.SetHiringData(CharacterData.Character.HiringTypes.Farmhand);next.AddToHiringItemList(ingredient.name,2);
                    AccessTools.Field(typeof(NPCController),"_readyToCollect").SetValue(next,true);
                    next.transform.position=player.transform.position+Vector3.right;
                    int before=bag.ItemCount(ingredient),recorded=Recorded("collection");next.ResetHiringStatusAndReward();
                    Require(Recorded("collection")==recorded+1&&LastFact("collection").Contains("dropped on the ground")&&bag.ItemCount(ingredient)==before,"Unconfirmed destination or inventory credit.");
                    TickPassingLine();
                    Require(passing?.Speaker==next&&passing.Bubble!=null,"No nearby work prompt on collection, game state "+GameState.GetCurrentState(1)+", paused "+GameState.IsPaused+", line "+passing?.Line+".");
                });
                Case(report,"Walking away from a work remark leaves time and input free",()=>{
                    Require(passing.Line.Contains("ground")&&!Visible&&!GameState.IsPaused,"The worker described the wrong destination or opened chat.");
                    string line=passing.Line;var pos=player.transform.position;
                    try{player.transform.position=next.transform.position+Vector3.right*8;TickPassingLine();}
                    finally{player.transform.position=pos;}
                    Require(passing==null&&!Visible&&!GameState.IsPaused,"Walking out of earshot left an active prompt.");
                    report["walkAway"]=line;
                });
                yield return Await(()=>NativeCues.Pending==0,"Queued context did not reach the test backend.",30);
                string after=NativeCues.Head;int beforeLoad=NativeCues.Trace.Count;
                yield return LoadSnapshot(snapshot,owners[0]);
                // Loading can itself trigger a native social event or select a gift letter.
                string resolved=NativeCues.Head;
                var newEvents=NativeCues.Trace.Skip(beforeLoad).Reverse().ToArray();
                foreach(var e in newEvents)if(!string.IsNullOrEmpty(resolved)&&resolved==(string)e["id"])resolved=(string)e["parent"];
                report["restoredHead"]=resolved;report["liveAfterReload"]=NativeCues.Head;report["postLoadEvents"]=newEvents.Length;
                Require(after!=morning&&string.Equals(resolved??"",morning??"",StringComparison.Ordinal),"Restored ancestry did not reach the morning save.");
                report["interactionsComplete"]=true;
            }finally{RemoveSnapshot(IsolatedGameRun.Active.Root,snapshot);}
        }
    }
}
