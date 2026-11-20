using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using UnityEngine;
using _Mirthwood.Scripts.Demo.Chat;

namespace Mirthwood.Echoes
{
    internal sealed partial class NativeConversation
    {
        static readonly System.Reflection.FieldInfo LastNativeScore=AccessTools.Field(typeof(ChatController),"_scoreOfLastReceived");
        static JObject MeasuredCard(NPCController npc,string topic,int seed,int decision,float previous,int percent)
        {
            sampleSeed=seed;decisionSeed=decision;observedScores=appetiteDebits=0;
            float before=Appetite(npc),friend=Friendship(npc);
            var result=PairedCard(npc,0,topic,previous,percent);
            return JObject.FromObject(new{topic,seed,previous,percent,score=topic=="None"?0:(float)LastNativeScore.GetValue(npc.GetComponent<NPCChatController>()),
                branch=result.Reaction,handoff=(string)result.Handoff?["kind"],run=result.PositiveRun,
                cost=before-Appetite(npc),friendship=Friendship(npc)-friend,scoreCalls=observedScores,debits=appetiteDebits});
        }
        static bool Near(float a,float b)=>Mathf.Abs(a-b)<.00002f;

        static void MeasureDesireScale(NPCController npc,Action<float> restore,JArray stock,float start,JObject report)
        {
            var longChat=new JArray();var comparisons=new JArray();
            var proof=new JObject{["resident"]=npc.CharacterInfo.name,["stockTurns"]=stock.Count,["scaled"]=longChat,["comparisons"]=comparisons};
            report["desireScale"]=proof;
            float nativeCost=(float)stock[0]["desireBefore"]-(float)stock[0]["desireAfter"];
            float previous=0;restore(start);
            for(int j=0;j<45;j++){
                var row=MeasuredCard(npc,"Leisure",35,29+j,previous,25);longChat.Add(row);
                float score=(float)row["score"];
                bool ended=(string)row["handoff"]=="ended";
                float strength=score>0?1-(1-previous)*(1-Mathf.Clamp01(score)):score==0?previous/2:0;
                float amount=ended?0:score<=0?nativeCost:nativeCost*(1-.75f*strength);
                if(!Near((float)row["cost"],amount)||!Near((float)row["run"],ended?0:strength)||
                    (int)row["debits"]!=(ended?0:1)||(int)row["scoreCalls"]!=1)
                    throw new InvalidOperationException("The positive-run cost or retry did not match its native turn.");
                previous=(float)row["run"];
                if(ended)break;
            }
            if(longChat.Count<=stock.Count||(string)longChat.Last["handoff"]!="ended"||
                !Near((float)longChat[0]["friendship"],(float)stock[0]["after"]-(float)stock[0]["before"]))
                throw new InvalidOperationException("The positive sequence did not last longer and still end naturally.");

            // Find each kind of actual native reaction on the loaded character. Compare matched starting states.
            var found=new HashSet<string>();
            foreach(int seed in new[]{32,35,34,31,33,36,37,38,39,40}){
                foreach(string topic in new[]{"Dumb","Deep","Small Talk","Leisure","Intellectual"}){
                    restore(start);var full=MeasuredCard(npc,topic,seed,29,.6f,100);
                    string sign=(string)full["branch"];
                    if((string)full["handoff"]!="followup"||!found.Add(sign))continue;
                    var row=new JObject{["full"]=full};comparisons.Add(row);
                    foreach(int percent in new[]{10,25,70}){
                        restore(start);var scaled=MeasuredCard(npc,topic,seed,29,.6f,percent);
                        row[percent.ToString()]=scaled;
                        float score=(float)full["score"];
                        float strength=score>0?1-.4f*(1-Mathf.Clamp01(score)):score==0?.3f:0;
                        float expected=(float)full["cost"]*(score>0?1-(1-percent/100f)*strength:1);
                        if(!Near((float)scaled["cost"],expected)||!Near((float)scaled["run"],strength)||
                            !Near((float)scaled["friendship"],(float)full["friendship"])||(float)scaled["score"]!=score||
                            (string)scaled["handoff"]!="followup"||(int)scaled["scoreCalls"]!=1||(int)scaled["debits"]!=1)
                            throw new InvalidOperationException("A matched native reaction diverged at "+percent+"%.");
                    }
                    if(found.Count==3)break;
                }
                if(found.Count==3)break;
            }
            restore(-.9f);var tired=MeasuredCard(npc,"Deep",35,29,1,10);proof["tired"]=tired;
            var gesture=MeasuredCard(npc,"None",35,29,.6f,10);proof["unscored"]=gesture;
            proof["passed"]=found.Count==3&&(string)tired["handoff"]=="ended"&&(int)tired["debits"]==0&&(float)tired["run"]==0&&
                (int)gesture["scoreCalls"]==0&&(float)gesture["cost"]==0&&Near((float)gesture["run"],.6f);
            if(!(bool)proof["passed"])throw new InvalidOperationException("Missing native reaction, refusal, or unscored pacing case.");
        }
    }
}
