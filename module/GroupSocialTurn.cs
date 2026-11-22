using System;
using System.Linq;
using HarmonyLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Lowscope.Saving;
using UnityEngine;
using _Mirthwood.Scripts.Demo.GameModules;
using _Mirthwood.Scripts.Demo.Chat;
using _Mirthwood.Scripts.Demo.Combat;
using _Mirthwood.Scripts.Demo.ScriptableObjects;
using _Mirthwood.Scripts.Demo.Tests;

namespace Mirthwood.Echoes
{
    internal static class GroupSocialTurn
    {
        static readonly System.Reflection.FieldInfo SpouseResident=AccessTools.Field(typeof(SpouseManager),"npcCharacter");
        internal static TalkResult Apply(string session,int sequence,GroupGathering.Guest listener,Character speaker,
            string topic,int minimumCost,float positiveRun=0)
        {
            if(listener==null||!listener.Arrived||!listener.Unique||!speaker||topic=="None")return null;
            var receiver=listener.Npc.CharacterInfo;
            if(receiver?.relationships==null||receiver.personality==null||receiver.mood==null||
                speaker.CharacterInfo?.personality==null||listener.Chat?.dnaData?.chatActions==null)return null;
            var player=speaker==PlayerCharacterController.Instance;
            if(receiver.relationships.IsPlayersSpouse()&&(!MirthwoodPlayer.Instance.SpouseManager||
                !ReferenceEquals(SpouseResident?.GetValue(MirthwoodPlayer.Instance.SpouseManager),listener.Npc)))return null;
            if(!player&&(receiver.id==speaker.CharacterInfo.id||
                !receiver.relationships.GetSaveData().characterIDs.Contains(speaker.CharacterInfo.id)))return null;
            var matches=listener.Chat.dnaData.chatActions.Where(c=>c!=null&&c.category==DnaData.Dna.DnaCategories.Chat&&c.name==topic).Take(2).ToArray();
            if(matches.Length!=1)return null;
            var card=matches[0];
            string key="mirthwood.echoes.group.score."+session;
            string source=player?"player":Plugin.Owner(speaker);
            try{
                string saved=SaveMaster.GetString(key);
                if(!string.IsNullOrEmpty(saved)){
                    var receipt=JObject.Parse(saved);
                    var previous=(int)receipt["seq"];
                    if(previous>=sequence)
                        return previous==sequence&&(string)receipt["speaker"]==source&&(string)receipt["listener"]==listener.Actor&&
                            receipt["outcome"] is JObject outcome?TalkResult.Read(outcome):null;
                }
                // Main-thread dispatch and the marker share one save update. A retried line cannot spend the card again.
                var record=new JObject{["seq"]=sequence,["speaker"]=source,["listener"]=listener.Actor};
                SaveMaster.SetString(key,record.ToString(Formatting.None));
                TalkResult result;
                if(player)result=NativeSocialTurn.Play(speaker.GetComponent<PlayerChat>(),listener.Chat,card,positiveRun,minimumCost);
                else{
                    DnaTesting.ScoreAction(card,receiver,speaker.CharacterInfo,out float score,false);
                    result=new TalkResult(score>0?"warm":score<0?"cool":"neutral");
                }
                record["outcome"]=result.Record();SaveMaster.SetString(key,record.ToString(Formatting.None));
                return result;
            }catch(Exception e){
                Debug.LogWarning("Echoes could not verify a group reaction. "+e.GetBaseException().Message);
                return null;
            }
        }
    }
}
