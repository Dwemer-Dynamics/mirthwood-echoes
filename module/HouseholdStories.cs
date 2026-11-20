using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using InfinityPBR;
using InfinityPBR.Modules;
using Lowscope.Saving;
using Newtonsoft.Json.Linq;
using UnityEngine;
using _Mirthwood.Scripts.Demo;
using _Mirthwood.Scripts.Demo.AI;
using _Mirthwood.Scripts.Demo.Chat;
using _Mirthwood.Scripts.Demo.Combat;
using _Mirthwood.Scripts.Demo.GameModules;
using _Mirthwood.Scripts.Demo.Items;
using _Mirthwood.Scripts.Demo.Menus;
using _Mirthwood.Scripts.Demo.ScriptableObjects;
using _Mirthwood.Scripts.Runtime.Demo.Combat.CharacterControllers.Visuals;
using _Mirthwood.Scripts.Runtime.Demo.Effects;
using _Mirthwood.Scripts.Runtime.Monobehaviors;

namespace Mirthwood.Echoes
{
    internal static partial class InteractionHooks
    {
        static NPCController household;
        static NPCController Spouse(Character character)
        {
            var npc=character as NPCController;
            return Enabled&&npc&&Player&&npc.CharacterInfo?.relationships?.IsPlayersSpouse()==true?npc:null;
        }
        [HarmonyPatch(typeof(SpouseManager),"OnNightStart")]
        class MissedVisit
        {
            static void Prefix(Character ___npcCharacter,bool ____wasVisited,bool ____inDivorceMode,out int __state){
                __state=!____wasVisited&&!____inDivorceMode&&Spouse(___npcCharacter)?___npcCharacter.CharacterInfo.eventList?.Count??-1:-1;
            }
            static void Postfix(Character ___npcCharacter,int __state){
                if(__state>=0&&Spouse(___npcCharacter)&&___npcCharacter.CharacterInfo.eventList.Count==__state+1)
                    NativeCues.Fact((NPCController)___npcCharacter,"household","My spouse did not visit me that day, and I missed them.");
            }
        }
        [HarmonyPatch]
        class HouseholdAction
        {
            static IEnumerable<MethodBase> TargetMethods()=>new[]{"GiveRemoveItem","WiltRandomCrops","ReduceAnimalsHappiness"}.Select(n=>AccessTools.Method(typeof(SpouseManager),n));
            static void Prefix(Character ___npcCharacter,out NPCController __state){__state=household;household=Spouse(___npcCharacter);}
            static void Finalizer(NPCController __state){household=__state;}
        }
        [HarmonyPatch(typeof(SpouseManager),"GiveRemoveGold")]
        class HouseholdGold
        {
            static void Prefix(Character ___npcCharacter,out int? __state){__state=Spouse(___npcCharacter)?(int?)Player.inventory.gold:null;}
            static void Postfix(Character ___npcCharacter,int? __state){
                if(!__state.HasValue||!Spouse(___npcCharacter))return;
                int delta=Player.inventory.gold-__state.Value;
                if(delta!=0)NativeCues.Fact((NPCController)___npcCharacter,"household","Through my household activity our gold "+(delta>0?"increased":"decreased")+" by "+Math.Abs(delta)+".",false);
            }
        }
        [HarmonyPatch(typeof(Inventory),"AddItem",typeof(ItemDataBase),typeof(int),typeof(bool))]
        class HouseholdItems
        {
            static void Prefix(Inventory __instance,ItemDataBase itemData,out int __state){__state=household&&__instance==household.GetNpcInventory()&&itemData?__instance.ItemCount(itemData):-1;}
            static void Postfix(Inventory __instance,ItemDataBase itemData,int __state){
                if(__state<0||!household)return;int delta=__instance.ItemCount(itemData)-__state;
                if(delta>0)NativeCues.Fact(household,"household","I added "+delta+" "+NativeCues.ItemName(itemData)+" to my household inventory.",false);
            }
        }
        [HarmonyPatch(typeof(Animal),"AddHappiness")]
        class HouseholdAnimal
        {
            static void Prefix(Animal __instance,out float __state){__state=household?__instance.Happiness:float.NaN;}
            static void Postfix(Animal __instance,float __state){
                if(household&&__instance.Happiness<__state)NativeCues.Fact(household,"household","A farm animal became less happy during my household activity.",false);
            }
        }

        static Mailbox checkingMail;
        static Relationships selectedRelationship;
        [HarmonyPatch(typeof(Inventory),"RemoveItem",typeof(ItemDataBase),typeof(int),typeof(bool))]
        class HouseholdChest
        {
            static void Prefix(Inventory __instance,ItemDataBase itemData,out int __state){__state=household&&__instance is InventoryChest&&itemData?__instance.ItemCount(itemData):-1;}
            static void Postfix(Inventory __instance,ItemDataBase itemData,int __state){
                if(__state<0||!household)return;int delta=__state-__instance.ItemCount(itemData);
                if(delta>0)NativeCues.Fact(household,"household","During my household activity "+delta+" "+NativeCues.ItemName(itemData)+" was taken from storage.",false);
            }
        }
        [HarmonyPatch(typeof(Growable),"Wilt")]
        class HouseholdCrop
        {
            static void Postfix(){if(household)NativeCues.Fact(household,"household","A crop wilted during my household activity.",false);}
        }
        static bool readingGift;
        static NoteView letterView;
        static List<string> mailClaim;
        static void ResetStory(){household=null;checkingMail=null;selectedRelationship=null;readingGift=false;letterView=null;mailClaim=null;ceremony=null;}
        [HarmonyPatch(typeof(Mailbox),"CheckForNewLetter")]
        class MailSelection
        {
            static void Prefix(Mailbox __instance){checkingMail=Enabled?__instance:null;selectedRelationship=null;}
            static void Finalizer(){checkingMail=null;selectedRelationship=null;}
        }
        [HarmonyPatch]
        class SenderSelected
        {
            static MethodBase TargetMethod()=>AccessTools.Method(typeof(Relationships),"GetRelationship",new[]{typeof(string),typeof(float).MakeByRefType(),typeof(float).MakeByRefType()});
            static void Postfix(Relationships __instance){if(checkingMail)selectedRelationship=__instance;}
        }
        [HarmonyPatch(typeof(Mailbox),"AddLetter")]
        class LetterSent
        {
            static void Postfix(Mailbox __instance,CharacterData ___characterData,Mailbox.LetterChoice ____pendingLetter,bool ____hasNewLetter,string friendName){
                if(!checkingMail||checkingMail!=__instance||!____hasNewLetter||____pendingLetter==null||____pendingLetter.fromName!=friendName||selectedRelationship==null)return;
                var matches=___characterData.characters.Where(x=>ReferenceEquals(x.relationships,selectedRelationship)&&x.name==friendName).Take(2).ToArray();
                if(matches.Length!=1)return;
                var senders=UnityEngine.Object.FindObjectsOfType<NPCController>().Where(x=>x.CharacterInfo?.id==matches[0].id).Take(2).ToArray();
                if(senders.Length!=1)return;
                if(__instance.choices==null||____pendingLetter.choiceIndex<0||____pendingLetter.choiceIndex>=__instance.choices.Length)return;
                var choices=__instance.choices[____pendingLetter.choiceIndex].items;
                if(choices==null||____pendingLetter.itemIndex<0||____pendingLetter.itemIndex>=choices.Length)return;
                var item=choices[____pendingLetter.itemIndex];
                if(item?.itemData!=null)NativeCues.Fact(senders[0],"letter","I sent the player a gift letter enclosing "+item.count+" "+NativeCues.ItemName(item.itemData)+". I have no confirmation they collected it.",false);
            }
        }
        static void PlayerFact(string kind,string text){
            var p=NativeCues.Person(Player,text);if(p!=null)NativeCues.Record(kind,new JArray(p));
        }
        [HarmonyPatch(typeof(Mailbox),"ReadLetter")]
        class LetterRead
        {
            static void Prefix(Mailbox.LetterChoice ____pendingLetter,int playerIndex){
                readingGift=Enabled&&playerIndex==1&&____pendingLetter!=null&&!____pendingLetter.isFuneralLetter&&!____pendingLetter.isQuestLetter;
            }
            static void Finalizer(){readingGift=false;}
        }
        [HarmonyPatch(typeof(NoteView),"DisplayNoteWithGift")]
        class LetterDisplayed
        {
            static void Postfix(NoteView __instance,Item __1){
                if(!readingGift||__1?.itemData==null)return;
                letterView=__instance;PlayerFact("letter","I opened a gift letter enclosing "+__1.count+" "+NativeCues.ItemName(__1.itemData)+".");
            }
        }
        [HarmonyPatch(typeof(NoteView),"GiveItem")]
        class LetterClaim
        {
            static void Prefix(NoteView __instance){mailClaim=letterView==__instance?new List<string>():null;}
            static void Postfix(){
                if(mailClaim==null)return;
                if(mailClaim.Count>0)PlayerFact("letter","Gift letter attachment: "+string.Join(" ",mailClaim));
                letterView=null;
            }
            static void Finalizer(){mailClaim=null;}
        }

        static FuneralCeremonyController ceremony;
        static readonly FieldInfo Deceased=AccessTools.Field(typeof(FuneralCeremonyController),"_deceasedCharacterId");
        [HarmonyPatch]
        class FuneralStart
        {
            static MethodBase TargetMethod()=>Step(typeof(FuneralCeremonyController),"CoCeremonyStart");
            static void Prefix(object __instance){ceremony=Enabled?AccessTools.Field(__instance.GetType(),"<>4__this").GetValue(__instance) as FuneralCeremonyController:null;}
            static void Finalizer(){ceremony=null;}
        }
        [HarmonyPatch(typeof(Mailbox),"ReadFuneralLetter")]
        class FuneralLetter
        {
            static void Postfix(Mailbox.LetterChoice letter,bool ____hasNewLetter){
                if(!Enabled||____hasNewLetter||string.IsNullOrEmpty(letter?.deadNPCId)||string.IsNullOrWhiteSpace(letter.deadNPCName))return;
                SaveMaster.SetString("mirthwood.echoes.funeral",new JObject{["id"]=letter.deadNPCId,["name"]=letter.deadNPCName}.ToString(Newtonsoft.Json.Formatting.None));
            }
        }
        [HarmonyPatch]
        class FuneralGuest
        {
            static IEnumerable<MethodBase> TargetMethods(){
                yield return AccessTools.Method(typeof(CharacterSkinController),"CopySkinsTo");
                yield return AccessTools.Method(typeof(SpineMultiSkeleton),"CopySkinsTo");
            }
            static void Postfix(object __instance,Spine.Skeleton __0){
                if(!ceremony||__0==null||!Player)return;
                string id=(string)Deceased.GetValue(ceremony);
                if(string.IsNullOrEmpty(id)||id==Player.CharacterInfo.id)return;
                if(!ceremony.funeralGuests.Any(g=>g&&ReferenceEquals(g.Skeleton,__0))&&!(ceremony.spouse&&ReferenceEquals(ceremony.spouse.Skeleton,__0)))return;
                var guests=UnityEngine.Object.FindObjectsOfType<NPCController>().Where(n=>n.Visuals!=null&&ReferenceEquals(n.Visuals.SkinController,__instance)).Take(2).ToArray();
                if(guests.Length!=1)return;
                string who=null,raw=SaveMaster.GetString("mirthwood.echoes.funeral");
                if(!string.IsNullOrEmpty(raw)){try{var letter=JObject.Parse(raw);if((string)letter["id"]==id)who=(string)letter["name"];}catch(Newtonsoft.Json.JsonException){}}
                var guest=guests[0];
                string bond=FuneralRelationship(guest.CharacterInfo.relationships,id);
                string fact=string.IsNullOrEmpty(who)?"I attended a funeral.":"I attended the funeral of "+who+".";
                if(bond=="Spouse")fact+=" The person who died was my spouse.";
                else if(bond!=null)fact+=" My recorded friendship with the deceased was "+bond+".";
                NativeCues.Fact(guest,"funeral",fact,false,new JObject{["deceasedId"]=id,["name"]=who,["relationship"]=bond});
            }
        }
        internal static string FuneralRelationship(Relationships relationships,string deceased)
        {
            if(relationships==null||string.IsNullOrEmpty(deceased))return null;
            if(relationships.GetSpouse()==deceased)return "Spouse";
            // The native indexer creates a new relationship for an unknown ID.
            var saved=relationships.GetSaveData();
            if(!saved.characterIDs.Contains(deceased))return null;
            relationships.GetRelationship(deceased,out float friendship,out float romance);
            return FriendshipBand(friendship);
        }
        internal static string FriendshipBand(float friendship)
        {
            foreach(var status in CharacterData.Character.AllFriendshipStatuses)
                if(friendship<(int)status)return status.ToString();
            return null;
        }
        static bool ContainsEffect(IEffect root,QuestEffect seek){
            if(ReferenceEquals(root,seek))return true;
            return root is NodeGroupEffect group && AccessTools.Field(typeof(NodeGroupEffect),"effects").GetValue(group) is IEffect[] effects &&
                effects.Any(e=>ReferenceEquals(e,seek));
        }
        internal static string QuestEvidence;
        sealed class HandIn {public NPCController Npc;public GameQuest Entry;public int Steps;}
        [HarmonyPatch(typeof(QuestEffect),"ApplyEffect")]
        class QuestHandIn
        {
            static void Prefix(QuestEffect __instance,object source,QuestEffect.EffectType ___effectType,out HandIn __state){
                __state=null;if(NativeCues.Trace!=null)QuestEvidence="source";
                var q=__instance.GetQuest;
                if(!Enabled||!q||!q.isGenerated||___effectType==QuestEffect.EffectType.Give||!(source is NPCController npc))return;
                var entry=MirthwoodPlayer.Instance.QuestList.Get(q);if(NativeCues.Trace!=null)QuestEvidence="entry: "+entry?.status;
                if(entry==null||entry.status!=QuestStep.QuestStepStatus.InProgress)return;
                var brain=npc.GetComponent<NPCChatController>();
                if(NativeCues.Trace!=null)QuestEvidence="dialogue effect";
                if(!brain||!brain.ConditionalDialogues.Any(d=>d?.dialogueData&&ReferenceEquals(d.quest,q)&&!d.requireQuestNotStarted&&ContainsEffect(d.dialogueData.GetOnChosenEffect(),__instance)))return;
                __state=new HandIn{Npc=npc,Entry=entry,Steps=entry.StepsLeft};
            }
            static void Postfix(QuestEffect __instance,HandIn __state){
                if(__state==null)return;
                var q=__instance.GetQuest;var entry=__state.Entry;var active=MirthwoodPlayer.Instance.QuestList.Get(q);
                if(NativeCues.Trace!=null)QuestEvidence="after: "+entry.status+" current: "+active?.status;
                if(active!=null&&!ReferenceEquals(active,entry)&&active.status==QuestStep.QuestStepStatus.InProgress)return;
                string status=entry.status==QuestStep.QuestStepStatus.Succeeded?"was completed":
                    entry.status==QuestStep.QuestStepStatus.Failed?"failed":entry.StepsLeft<__state.Steps?"advanced one step":null;
                string title=q.FullName;
                if(string.IsNullOrWhiteSpace(title)||title.Contains("{"))title=q.generatedQuestType.ToString();
                if(status!=null)NativeCues.Fact(__state.Npc,"quest","During the hand-in with me, the quest “"+title+"” "+status+".",true,
                    JObject.FromObject(new{quest=q.Uid(),title,status=entry.status.ToString(),remainingSteps=entry.StepsLeft}));
            }
        }
    }
}
