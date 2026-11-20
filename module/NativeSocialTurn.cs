using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using UnityEngine;
using _Mirthwood.Scripts.Demo.Chat;
using _Mirthwood.Scripts.Demo.Menus;
using _Mirthwood.Scripts.Demo.ScriptableObjects;
using _Mirthwood.Scripts.Localization;

namespace Mirthwood.Echoes
{
    internal sealed class TalkResult
    {
        public readonly string Reaction;
        public readonly JObject Handoff;
        public readonly float PositiveRun;
        public TalkResult(string reaction,JObject handoff=null,float positiveRun=0)
        {Reaction=reaction;Handoff=handoff;PositiveRun=positiveRun;}
        internal static float ReadRun(JToken value)
        {
            float run=(float?)value??0;
            return float.IsNaN(run)||float.IsInfinity(run)?0:Mathf.Clamp01(run);
        }
        public static TalkResult Read(JObject data)=>new TalkResult((string)data["reaction"],data["handoff"] as JObject,ReadRun(data["positiveRun"]));
        public JObject Record()=>new JObject{["reaction"]=Reaction,["handoff"]=Handoff,["positiveRun"]=PositiveRun};
    }

    // Owns one main-thread card dispatch, through its native score, appetite check and return card.
    // Other residents and other players keep their stock handlers throughout.
    [HarmonyPatch]
    internal sealed class NativeSocialTurn
    {
        static readonly FieldInfo LastScore=AccessTools.Field(typeof(ChatController),"_scoreOfLastReceived");
        static Dictionary<string,string[]> topicText;
        static NativeSocialTurn active;
        readonly PlayerChat player;
        readonly NPCChatController npc;
        readonly int minimumCostPercent;
        readonly float previousRun;
        float positiveRun;
        IEnumerator receive;
        bool answered;
        DnaData.Dna next;
        NativeSocialTurn(PlayerChat player,NPCChatController npc,float previousRun,int minimumCostPercent)
        {this.player=player;this.npc=npc;this.previousRun=previousRun;this.minimumCostPercent=minimumCostPercent;}

        public static TalkResult Play(PlayerChat player,NPCChatController npc,DnaData.Dna action,float previousRun,int minimumCostPercent)
        {
            if(active!=null)throw new InvalidOperationException("A native Talk exchange is already in progress.");
            var turn=new NativeSocialTurn(player,npc,previousRun,minimumCostPercent);
            try{
                active=turn;
                npc.PrepareForChat(player);player.PrepareForChat(npc);
                player.SendInteraction(action);
                if(turn.receive==null)throw new InvalidOperationException("Mirthwood did not receive the Talk card.");
                FinishCard(turn.receive);
                if(!turn.answered)throw new InvalidOperationException("Mirthwood did not return a Talk decision.");
                float score=(float)LastScore.GetValue(npc);
                var handoff=new JObject{["kind"]=turn.next==null?"ended":"followup",["cue"]=turn.Cue()};
                if(turn.next!=null)handoff["action"]=turn.next.name;
                return new TalkResult(score>0?"warm":score<0?"cool":"neutral",handoff,turn.next==null?0:turn.positiveRun);
            }finally{active=null;}
        }

        static void FinishCard(IEnumerator routine)
        {
            // The installed receiver yields only nested receivers and presentation delays.
            // Complete the card in one save update. An unfamiliar yield cannot be safely fast-forwarded.
            try{
                while(routine.MoveNext()){
                    if(routine.Current is WaitForSeconds)continue;
                    if(routine.Current is IEnumerator child)FinishCard(child);
                    else throw new InvalidDataException("The native Talk sequence has changed.");
                }
            }finally{(routine as IDisposable)?.Dispose();}
        }

        static IEnumerator Empty(){yield break;}
        [HarmonyPostfix,HarmonyPatch(typeof(NPCChatController),"ReceiveChat",typeof(DnaData.Dna),typeof(ChatController),typeof(bool))]
        static void OwnReceiver(NPCChatController __instance,ChatController other,ref IEnumerator __result)
        {
            if(active==null||__instance!=active.npc||other!=active.player||active.receive!=null)return;
            active.receive=__result;__result=Empty();
        }
        [HarmonyPostfix,HarmonyPatch(typeof(PlayerChat),"ReceiveChat",typeof(DnaData.Dna),typeof(ChatController),typeof(bool))]
        static void ReadReturn(PlayerChat __instance,ChatController other,DnaData.Dna action,bool forceNoResponse,ref IEnumerator __result)
        {
            if(active!=null&&__instance==active.player&&other==active.npc){active.answered=true;active.next=action;return;}
            if(!Plugin.ReplacesTalk||forceNoResponse||!(other?.Character is NPCController resident))return;
            // The stock incoming-card coroutine closes quick actions and opens the response deck.
            // Keep the resident's chosen subject available for the player's next voluntary Talk.
            if(action!=null&&ReferenceEquals(Plugin.Selected.GetValue(__instance),other))
                NativeCues.Recent(resident,null,"card","I brought up "+action.DnaNameLocalized+" with the player. The card supplied no spoken quotation.");
            __result=Empty();
        }
        [HarmonyPrefix,HarmonyPatch(typeof(NPCChatController),"DecreaseSocialNeed")]
        static void BeforeSocialCost(NPCChatController __instance,out float __state)
        {
            __state=float.NaN;
            if(active!=null&&active.npc==__instance&&active.next!=null&&__instance.chatTarget==active.player)
                __state=__instance.Character.CharacterInfo.personality.values[5];
        }
        [HarmonyPostfix,HarmonyPatch(typeof(NPCChatController),"DecreaseSocialNeed")]
        static void AfterSocialCost(NPCChatController __instance,float __state)
        {
            if(float.IsNaN(__state))return;
            var turn=active;
            float score=(float)LastScore.GetValue(__instance);
            if(score<=0){
                turn.positiveRun=score==0?turn.previousRun*.5f:0;
                return;
            }
            turn.positiveRun=turn.previousRun+(1-turn.previousRun)*Mathf.Clamp01(score);
            if(turn.minimumCostPercent==100)return;
            var values=__instance.Character.CharacterInfo.personality.values;
            float nativeCost=__state-values[5];
            // Use this resident's actual return-card debit. Recovery and other interactions are outside this call.
            if(nativeCost>0)values[5]=__state-nativeCost*Mathf.Lerp(1,turn.minimumCostPercent/100f,turn.positiveRun);
        }
        [HarmonyPrefix,HarmonyPatch(typeof(PlayerChat),"OpenChatMenu")]
        static bool OpenNativeMenu()=>!Plugin.ReplacesTalk;
        [HarmonyPrefix,HarmonyPatch(typeof(PlayerChat),"CloseQuickChat")]
        static bool CloseNativePrompt(PlayerChat __instance)=>active==null||__instance!=active.player||__instance.chatTarget!=active.npc;
        [HarmonyPrefix,HarmonyPatch(typeof(ChatMenuController),"OnChatStart")]
        static bool ShowNativeCard()=>!Plugin.ReplacesTalk;
        string Cue()
        {
            string name=npc.Character.CharacterInfo.name;
            if(topicText==null && Resources.Load<TextAsset>("Chat Topics") is TextAsset asset)
                topicText=CSVReaderString.SplitCsvRowsDictionary(asset.text);
            if(topicText!=null&&topicText.TryGetValue(next?.name??"Ignore",out var row)&&row.Length>2){
                string fixedPart=row[1],variable=row[UnityEngine.Random.Range(2,row.Length)];
                var words=new List<string>();
                if(!string.IsNullOrWhiteSpace(fixedPart))words.Add(LocalizationManager.GetLocalizedStringUI(fixedPart));
                if(!string.IsNullOrWhiteSpace(variable))words.Add(LocalizationManager.GetLocalizedStringUI(variable));
                var phrase=string.Join(" ",words).Trim();
                if(phrase.Length>0)return name+" "+phrase;
            }
            return next==null?name+" is done talking for now.":name+" changes the subject to "+next.DnaNameLocalized+".";
        }
    }
}
