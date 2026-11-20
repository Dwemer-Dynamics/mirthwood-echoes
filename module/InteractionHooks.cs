using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using UnityEngine;
using _Mirthwood.Scripts.Demo.Chat;
using _Mirthwood.Scripts.Demo.Combat;
using _Mirthwood.Scripts.Demo.Items;
using _Mirthwood.Scripts.Demo.ScriptableObjects;
using _Mirthwood.Scripts.Demo.CharacterOverheadUI;
using _Mirthwood.Scripts.Runtime.Demo.Menus;
using _Mirthwood.Scripts.Runtime.Demo.Spawning;
using _Mirthwood.Scripts.Runtime.Monobehaviors;

namespace Mirthwood.Echoes
{
    internal static partial class InteractionHooks
    {
        internal static bool Compatible {get;private set;}
        internal static readonly List<string> Installed=new List<string>();
        static PlayerCharacterController Player=>PlayerCharacterController.Instance;
        static readonly BindingFlags Hidden=BindingFlags.Instance|BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
        static MethodBase Step(Type owner,string coroutine)=>owner.GetNestedTypes(Hidden)
            .Single(t=>t.Name.StartsWith("<"+coroutine+">",StringComparison.Ordinal)).GetMethod("MoveNext",Hidden);
        internal static void Install(Harmony harmony,ManualLogSource log)
        {
            Compatible=typeof(NPCController).Assembly.ManifestModule.ModuleVersionId==new Guid("ff37d4d6-1f8b-48dd-9cc1-0f5998bb593b");
            if(!Compatible){Enabled=false;log.LogWarning("The game assembly changed. Native interaction observations await verification for this build.");return;}
            foreach(var type in typeof(InteractionHooks).GetNestedTypes(Hidden).Where(t=>t.GetCustomAttributes(typeof(HarmonyPatch),false).Length!=0)){
                try{harmony.CreateClassProcessor(type).Patch();Installed.Add(type.Name);}
                catch(Exception e){log.LogWarning("Native observation unavailable ("+type.Name+"): "+e.GetBaseException().Message);}
            }
        }
        internal static void Reset(){gift=null;delayed=null;collect=null;origin=null;ResetStory();}
        internal static void DismissGiftRemark(NPCController npc){if(delayed?.Npc==npc)delayed=null;}
        internal static bool Enabled=true;
        [HarmonyPatch(typeof(ChatController),"SendInteraction",typeof(SocialActions.Action.Type))]
        class PassingGesture
        {
            static void Prefix(ChatController __instance,SocialActions.Action.Type __0,out NPCController __state)
            {
                __state=null;
                if(!Enabled||!Plugin.ReplacesTalk||(__0!=SocialActions.Action.Type.Greet&&__0!=SocialActions.Action.Type.Antagonize))return;
                if(__instance is PlayerChat player&&player.chatTarget is NPCChatController target&&ReferenceEquals(Plugin.Selected.GetValue(player),target))
                    __state=target.Character as NPCController;
                else if(__instance is NPCChatController resident&&resident.chatTarget is PlayerChat)__state=resident.Character as NPCController;
            }
            static void Postfix(ChatController __instance,SocialActions.Action.Type __0,NPCController __state)
            {
                if(!__state)return;
                bool greeting=__0==SocialActions.Action.Type.Greet;
                string action=__instance is PlayerChat?(greeting?"The player greeted me in passing.":"The player jeered at me in passing."):
                    (greeting?"I greeted the player in passing.":"I jeered at the player in passing.");
                string id=NativeCues.Fact(__state,"social",action);
                if(id!=null)NativeCues.Recent(__state,id,"greeting",action);
            }
        }
        sealed class Gift
        {
            public NPCController Npc;public ItemDataBase Item;public Inventory Bag;public string Label,Line,Id;
            public int Before;public int? Opinion;public bool Removed;public float Expires;public object Callback;
        }
        static Gift gift,delayed,scheduling;
        static readonly MethodInfo DelayedCallback=typeof(NPCChatController).GetNestedTypes(Hidden).SelectMany(t=>t.GetMethods(Hidden))
            .FirstOrDefault(m=>m.Name.StartsWith("<ShowBarkDelayed>b__",StringComparison.Ordinal));
        [HarmonyPatch(typeof(StorageUIWindow),"OnContainerItemSubmitted")]
        class Give
        {
            static void Prefix(Item item,bool ___isFromGift,ChatController ____currentNPCChatController,Inventory ____playerInventory,out Gift __state)
            {
                __state=gift;gift=null;
                if(!Enabled||!___isFromGift||!(____currentNPCChatController?.Character is NPCController npc)||item?.itemData==null||!____playerInventory)return;
                gift=new Gift{Npc=npc,Item=item.itemData,Bag=____playerInventory,Before=____playerInventory.ItemCount(item.itemData),Label=NativeCues.ItemName(item.itemData)};
            }
            static void Postfix()
            {
                var g=gift;if(g==null||!g.Removed||g.Before-g.Bag.ItemCount(g.Item)!=1)return;
                string reaction=g.Opinion.HasValue?" My reaction to this gift was "+new[]{"strongly negative","negative","neutral","positive","strongly positive"}[g.Opinion.Value+2]+".":"";
                g.Id=NativeCues.Fact(g.Npc,"gift","The player gave me one "+g.Label+"."+reaction,true,
                    JObject.FromObject(new{item=g.Item.name,quantity=1,nativeOpinion=g.Opinion}));
                if(g.Id!=null&&g.Line!=null){g.Expires=Time.time+6;delayed=g;}
                string line=g.Opinion<0?"I'm afraid I have little use for "+g.Label+".":
                    g.Opinion>0?"Thank you for the "+g.Label+"!":"Ah, you brought me "+g.Label+".";
                Plugin.OfferReply(g.Npc,g.Id,"giftReply",line,g.Line==null?0:2.4f);
            }
            static void Finalizer(Gift __state){gift=__state;}
        }
        [HarmonyPatch(typeof(Inventory),"RemoveItem",typeof(ItemDataBase),typeof(int),typeof(bool))]
        class Removed
        {
            static void Postfix(Inventory __instance,ItemDataBase itemData,int amount,bool __result){
                if(gift!=null&&gift.Bag==__instance&&gift.Item==itemData&&amount==1)gift.Removed=__result;
            }
        }
        [HarmonyPatch(typeof(ItemGiftData),"GetLikeDislike")]
        class Opinion
        {
            static void Postfix(Item __0,CharacterData.Character __1,int __result){
                if(gift!=null&&__0?.itemData==gift.Item&&ReferenceEquals(gift.Npc.CharacterInfo,__1)&&__result>=-2&&__result<=2)gift.Opinion=__result;
            }
        }
        [HarmonyPatch(typeof(NPCChatController),"ShowBarkDelayed")]
        class Scheduled
        {
            static void Prefix(NPCChatController __instance,string text,out Gift __state){
                __state=scheduling;scheduling=gift!=null&&gift.Npc==__instance.Character?gift:null;
                if(delayed!=null&&delayed.Npc==__instance.Character)delayed=null;
                if(scheduling!=null)scheduling.Line=text;
            }
            static void Finalizer(Gift __state){scheduling=__state;}
        }
        [HarmonyPatch]
        class GiftCallback
        {
            static MethodBase TargetMethod()=>PatchProcessor.GetOriginalInstructions(AccessTools.Method(typeof(NPCChatController),"ShowBarkDelayed"))
                .Select(i=>i.operand as MethodInfo).First(m=>m!=null&&m.Name=="CallAfterDelay");
            static void Prefix(Action __1){
                if(scheduling!=null&&__1?.Method==DelayedCallback)scheduling.Callback=__1.Target;
            }
        }
        [HarmonyPatch]
        class DelayedOrigin
        {
            static MethodBase TargetMethod()=>DelayedCallback;
            static void Prefix(object __instance,out string __state){__state=origin;origin=delayed?.Callback==__instance?"delayed":null;}
            static void Finalizer(string __state){origin=__state;}
        }
        [HarmonyPatch(typeof(HireUIWindow),"Init")]
        class Quote
        {
            static void Postfix(NPCController npc,int ___price){
                if(Enabled&&npc&&___price>=0)NativeCues.Recent(npc,null,"hire","The hiring window quoted "+___price+" gold. This was a price quote.");
            }
        }
        [HarmonyPatch]
        class Hire
        {
            static IEnumerable<MethodBase> TargetMethods()=>new[]{"Farmhand","Mining","Woodcutting","Hunting","Foraging"}.Select(n=>AccessTools.Method(typeof(HireUIWindow),"On"+n+"Pressed"));
            static void Prefix(NPCController ___npcController,out int __state){__state=Enabled&&Player&&___npcController&&!___npcController.IsHired()?Player.inventory.gold:-1;}
            static void Postfix(NPCController ___npcController,int ___price,int __state,MethodBase __originalMethod){
                if(__state<0||___price<0||!___npcController||!___npcController.IsHired()||!Player||__state-Player.inventory.gold!=___price)return;
                string job=___npcController.GetCurrentHiring().ToString();
                if(__originalMethod.Name=="On"+job+"Pressed")NativeCues.Fact(___npcController,"hire","The player hired me for "+job+" work and paid "+___price+" gold.",true,JObject.FromObject(new{job,quotedGold=___price,paidGold=__state-Player.inventory.gold}));
            }
        }
        sealed class Collection
        {
            public NPCController Npc;
            public readonly List<string> Items=new List<string>();
            public readonly JArray Transfers=new JArray();
        }
        static Collection collect;
        [HarmonyPatch(typeof(NPCController),"ResetHiringStatusAndReward")]
        class Collect
        {
            static void Prefix(NPCController __instance,out Collection __state){
                __state=collect;collect=Enabled&&__instance.IsReadyToCollect()?new Collection{Npc=__instance}:null;
            }
            static void Postfix(){
                if(collect==null||collect.Npc.IsReadyToCollect()||collect.Items.Count==0)return;
                string id=NativeCues.Fact(collect.Npc,"collection","Collected the hired-work output. "+string.Join(" ",collect.Items),true,new JObject{["transfers"]=collect.Transfers});
                bool hasGround=collect.Transfers.Any(t=>(string)t["destination"]=="ground");
                string line;
                if(collect.Transfers.Count==1){
                    var item=collect.Transfers[0];
                    string amount=(int)item["quantity"]==1?".":", "+item["quantity"]+" in all.";
                    line=(hasGround?"I've left the "+item["label"]+" on the ground for you":"Here's your "+item["label"])+amount;
                }else if(hasGround){
                    line=collect.Transfers.All(t=>(string)t["destination"]=="ground")?"I've left your share here on the ground.":"You've got part of the haul. The rest is on the ground here.";
                }else line="There you are. That's the whole lot handed over.";
                Plugin.OfferReply(collect.Npc,id,"collectionReply",line);
            }
            static void Finalizer(Collection __state){collect=__state;}
        }
        [HarmonyPatch(typeof(Character),"AddItem",typeof(ItemDataBase),typeof(int))]
        class Credit
        {
            static void Prefix(Character __instance,ItemDataBase itemData,out int __state){
                __state=(collect!=null||mailClaim!=null)&&__instance==Player&&itemData?Player.inventory.ItemCount(itemData):-1;
            }
            static void Postfix(Character __instance,ItemDataBase itemData,int amount,int __state){
                if(__state<0||__instance!=Player)return;
                int actual=Player.inventory.ItemCount(itemData)-__state;
                if(actual>0&&actual<=amount)Transferred(itemData,actual,true);
            }
        }
        static void Transferred(ItemDataBase item,int quantity,bool inInventory){
            string description=quantity+" "+NativeCues.ItemName(item)+(inInventory?" reached the player's inventory.":" dropped on the ground.");
            if(collect!=null){
                collect.Items.Add(description);
                collect.Transfers.Add(JObject.FromObject(new{item=item.name,label=NativeCues.ItemName(item),quantity,destination=inInventory?"inventory":"ground"}));
            }
            if(mailClaim!=null)mailClaim.Add(description);
        }
        [HarmonyPatch(typeof(DroppedItem),"SetItem")]
        class Dropped
        {
            static void Postfix(DroppedItem __instance,Item __0,Item ___item){
                if((collect!=null||mailClaim!=null)&&__instance&&__instance.gameObject.activeInHierarchy&&__0?.itemData!=null&&
                   ___item?.itemData==__0.itemData&&___item.count==__0.count&&__0.count>0)
                    Transferred(__0.itemData,__0.count,false);
            }
        }
        static string origin;
        [HarmonyPatch(typeof(BarkManager),"OnPlayerInRange")]
        class Directed
        {
            static void Prefix(out string __state){__state=origin;origin="approach";}
            static void Finalizer(string __state){origin=__state;}
        }
        [HarmonyPatch]
        class WarningOrigin
        {
            static IEnumerable<MethodBase> TargetMethods(){
                yield return Step(typeof(BanditRaidController),"CoTriggerBanditRaidBarks");
                yield return Step(typeof(VerminPlagueController),"CoTriggerVerminPlagueBarks");
            }
            static void Prefix(out string __state,MethodBase __originalMethod){__state=origin;origin=__originalMethod.DeclaringType.DeclaringType==typeof(BanditRaidController)?"BanditRaid":"VerminPlague";}
            static void Finalizer(string __state){origin=__state;}
        }
        static bool OnScreen(NPCController n){
            if(!n||!Player||!Camera.main||!n.isActiveAndEnabled||(n.transform.position-Player.transform.position).sqrMagnitude>400)return false;
            var point=Camera.main.WorldToViewportPoint(n.transform.position);return point.z>0 && point.x>=0 && point.x<=1 && point.y>=0 && point.y<=1;
        }
        [HarmonyPatch(typeof(NPCChatController),"ShowBark")]
        class Shown
        {
            static void Postfix(NPCChatController __instance,string text,string barkType,OverheadUIController ___overheadUI){
                var npc=__instance.Character as NPCController;
                if(!Enabled||!__instance.enabled||!OnScreen(npc)||!___overheadUI||!___overheadUI.OverheadText.isActiveAndEnabled||___overheadUI.OverheadText.text!=text)return;
                if(origin=="approach"&&(barkType=="PlayerInRange"||barkType=="MarriagePlayerInRange"))
                    NativeCues.Recent(npc,null,"remark",text);
                if(origin=="delayed"&&barkType=="Ambient"&&delayed!=null&&delayed.Npc==npc&&delayed.Line==text&&Time.time<=delayed.Expires){
                    NativeCues.Recent(npc,delayed.Id,"gift",text);
                    Plugin.HeardGift(npc,delayed.Id,text);delayed=null;
                }
                if(origin==barkType&&(barkType=="BanditRaid"||barkType=="VerminPlague")){
                    string id=NativeCues.Fact(npc,"warning","I gave a "+(barkType=="BanditRaid"?"bandit raid":"vermin plague")+" warning: "+text);
                    NativeCues.Recent(npc,id,"warning",text);
                }
            }
        }
    }
}
