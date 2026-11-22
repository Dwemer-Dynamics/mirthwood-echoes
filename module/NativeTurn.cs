using System;
using System.Collections;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Mirthwood.Echoes
{
    internal sealed partial class NativeConversation
    {
        bool SameResident(int epoch)
        {
            if(epoch!=generation||!Visible||resident?.CharacterInfo==null||!resident.isActiveAndEnabled)return false;
            var target=JObject.FromObject(targetIdentity);var world=Plugin.World();
            if(world==null)return false;
            var now=JObject.FromObject(world);
            return new[]{"slot","created","player","memorySession"}.All(key=>JToken.DeepEquals(now[key],target["world"]?[key]))&&
                (string)target["owner"]==Plugin.Owner(resident)&&(string)target["nativeId"]==resident.CharacterInfo.id;
        }
        void CapturePlayerTurn(string words)
        {
            if((string)submittedTurn?["text"]==words)return;
            submittedTurn=new JObject{["text"]=words,["context"]=NativeCues.Binding(freshCue,resident,conversationId),
                ["worldAwareness"]=WorldAwareness.Capture(resident,interruption?.Recent)};
        }
        void Retry(string message,string note){busy=false;accepted=null;Edit(message,note);}
        void RecoverTalk(string original,string note)
        {
            if(phase!=Phase.Reading){Retry(original,note);return;}
            busy=false;accepted=null;pendingFinish=original;phaseFrame=Time.frameCount;ReadingHint();
        }
        void PlayerLine(string id,string text)
        {
            var bubble=playerBubble;
            speech.Say(id+"/player",_Mirthwood.Scripts.Demo.Combat.PlayerCharacterController.Instance,text,
                (heard,partial)=>{if(playerBubble==bubble&&bubble.Visible&&!bubble.Editing)bubble.ShowSpoken(text,heard,partial);},false);
        }
        void DisplayTalk(string id,string reply,JObject handoff,bool playerLeaves=false)
        {
            string kind=(string)handoff?["kind"];
            conversationOver=playerLeaves||kind=="ended"||kind=="unknown";
            phase=Phase.Reading;phaseFrame=Time.frameCount;
            speech.Say(id+"/npc",resident,reply,(heard,partial)=>ShowResidentReply(heard,partial,reply),showPlaceholder:false);
            ReadingHint();
        }
        IEnumerator NativeTurn(string message,int epoch)
        {
            if(!SameResident(epoch)){if(epoch==generation)Close();yield break;}
            var outbox=exchange;JObject turn=null;
            try{
                CapturePlayerTurn(message);
                turn=outbox.Accept(message,positiveRun,submittedTurn["context"] as JObject,resident,submittedTurn["worldAwareness"] as JObject);
                submittedTurn=turn;interruption=null;freshCue=null;
            }catch(Exception){Retry(message,"Could not save this exchange. Enter to retry.");}
            if(turn==null)yield break;
            string id=(string)turn["id"],original=(string)turn["text"];
            if(original!=message){Retry(original,"An earlier exchange is pending. Enter to recover your original words.");yield break;}
            PlayerLine(id,message);
            string stage="prepare";TalkResult result=null;
            float deadline=Time.realtimeSinceStartup+100;
            while(SameResident(epoch)){
                var values=resident.CharacterInfo.personality?.values;
                var payload=new {id,campaign,actor,text=message,context=turn["context"],reaction=result?.Reaction,handoff=result?.Handoff,supportsSpokenFarewells=true,
                    worldAwareness=stage=="prepare"?turn["worldAwareness"]:null,
                    returnCues=stage=="prepare"?turn["returnCues"]:null,playerImpression=stage=="prepare"?turn["playerImpression"]:null,
                    socialDesire=stage=="prepare"&&values?.Length>5?(float?)values[5]:null,
                    liveTopicFit=stage=="prepare"?TopicFit(resident,true):null};
                JObject answer=null;
                using(var r=Post(Address+"dialogue/"+stage,payload)){
                    active=r;r.timeout=85;yield return r.SendWebRequest();
                    if(epoch!=generation||!Visible)yield break;
                    active=null;
                    if(r.responseCode==425&&Time.realtimeSinceStartup<deadline){yield return new WaitForSecondsRealtime(.3f);continue;}
                    if(r.responseCode!=200){RecoverTalk(message,Error(r,"Connection interrupted. Enter to recover this exchange."));yield break;}
                    try{answer=JObject.Parse(r.downloadHandler.text);}catch(Exception){RecoverTalk(message,"Could not read the reply. Enter to recover it.");}
                }
                if(answer==null)yield break;
                if(!SameResident(epoch))break;
                switch((string)answer["state"]){
                    case "pending":
                        if(Time.realtimeSinceStartup>deadline){Retry(message,"Still waiting. Enter to check this exchange.");yield break;}
                        yield return new WaitForSecondsRealtime(.4f);continue;
                    case "expired":
                        try{positiveRun=outbox.Acknowledge(id);}catch(Exception){RecoverTalk(message,"Enter to close the saved exchange.");yield break;}
                        Close();yield break;
                    case "complete":
                        try{positiveRun=outbox.Acknowledge(id);}catch(Exception){RecoverTalk(message,"Your reply was saved. Enter to recover it.");yield break;}
                        busy=false;accepted=null;submittedTurn=null;DisplayTalk(id,(string)answer["reply"],answer["handoff"] as JObject,(bool?)answer["playerLeaves"]==true);yield break;
                    case "failed":case "interrupted":
                        try{outbox.Acknowledge(id);}catch(Exception){Retry(message,"Could not close the interrupted exchange.");yield break;}
                        submittedTurn=null;Retry(message,((string)answer["error"]??"The reply was interrupted.")+" Enter to start a new exchange.");yield break;
                    case "ready":
                        try{
                            var choices=(JObject)answer["choices"];
                            var endings=(JObject)answer["endingChoices"]??choices;
                            foreach(string key in new[]{"warm","neutral","cool"})
                                if(string.IsNullOrWhiteSpace((string)choices[key])||string.IsNullOrWhiteSpace((string)endings[key]))throw new FormatException();
                            result=outbox.Apply(id,(string)answer["topic"],resident,(int?)answer["positiveDesireCostPercent"]??100);
                            string card=(string)result.Handoff?["action"];
                            var cardChoices=answer["cardChoices"] as JObject;
                            var lines=(string)result.Handoff?["kind"]=="ended"?endings:
                                (card==null?null:cardChoices?[card] as JObject)??choices;
                            DisplayTalk(id,(string)lines[result.Reaction=="unknown"?"neutral":result.Reaction],result.Handoff,(bool?)answer["playerLeaves"]==true);stage="finish";
                        }catch(Exception){Retry(message,"Could not confirm the native reaction. Enter to recover this exchange.");yield break;}
                        if(IsolatedGameRun.Active?.TalkAddress!=null&&interruptBeforeFinish){
                            interruptBeforeFinish=false;yield return new WaitForEndOfFrame();
                            testFirstVisible=Time.realtimeSinceStartup;testFirstBubble=npcBubble.VisibleText;Close();yield break;
                        }
                        continue;
                    default:Retry(message,"Unrecognized exchange state. Enter to recover.");yield break;
                }
            }
            if(epoch==generation)Close();
        }
    }
}
