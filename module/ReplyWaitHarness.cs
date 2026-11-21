using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.AI;
using _Mirthwood.Scripts.Demo.Chat;

namespace Mirthwood.Echoes
{
    internal sealed partial class NativeConversation
    {
        static bool CanProbeReplyWait(NPCController npc)
        {
            var agent=npc.GetComponent<NavMeshAgent>();
            var brain=npc.GetComponent<NPCChatController>();
            return agent&&agent.isActiveAndEnabled&&agent.isOnNavMesh&&brain&&brain.chatEnabled&&!brain.GetIsBusy();
        }

        IEnumerator ObserveReplyWait(NPCController npc,JObject report,int epoch)
        {
            var brain=npc.GetComponent<NPCChatController>();
            var agent=npc.GetComponent<NavMeshAgent>();
            var other=UnityEngine.Object.FindObjectsOfType<NPCChatController>().FirstOrDefault(c=>c!=brain);
            report["waitSelectedOnly"]=brain&&WaitingFor(brain)&&other&&!WaitingFor(other);
            if(!brain||!agent||!agent.isActiveAndEnabled||!agent.isOnNavMesh)yield break;

            // Simulate the stock chat timer expiring before the provider returns in this copied save.
            if(!brain.GetIsBusy())brain.SetBusy(false);
            var stages=new List<string>();
            bool anchored=true,stationary=true,requestedCapture=false;
            float clock=Time.time,until=Time.realtimeSinceStartup+10;
            int frames=0;
            var position=npc.transform.position;
            while(epoch==generation && phase==Phase.Waiting && Time.realtimeSinceStartup<until){
                string displayed=npcBubble.ReadRenderedText();
                if(stages.Count==0||displayed!=stages[stages.Count-1])stages.Add(displayed);
                anchored &= npcBubble.Visible&&npcBubble.AnchoredTo(npc.transform)&&npcBubble.UsesNativeStyle;
                if(frames==1)position=npc.transform.position;
                if(frames>=2)stationary &= brain.IsChatting&&agent.isStopped&&(npc.transform.position-position).sqrMagnitude<.0004f;
                if(displayed==".."&&!requestedCapture){
                    requestedCapture=true;
                    host.StartCoroutine(Capture("latest-npc-waiting.png",ok=>report["waitCapture"]=ok));
                }
                frames++;yield return null;
            }
            report["waitStages"]=string.Join(" ",stages);
            report["waitCycle"]=stages.Take(4).SequenceEqual(new[]{".","..","...","."});
            report["waitAnchored"]=anchored&&frames>2;
            report["waitStationary"]=stationary&&frames>2&&Time.time>clock;
            yield return null;
            report["waitReleased"]=epoch==generation&&phase==Phase.Reading&&!WaitingFor(brain)&&!agent.isStopped;
        }

        IEnumerator CheckWaitCancellation(NPCController npc,string url,string line,JObject report)
        {
            var brain=npc.GetComponent<NPCChatController>();
            if(!brain||!Build(npc))yield break;
            try{
                fixtureUrl=url;fixtureReport=new JObject();campaign="fixture";actor="resident";ready=true;
                Submit(line);
                bool pending=WaitingFor(brain)&&npcBubble.ReadRenderedText()==".";
                Close();
                report["waitCancelled"]=pending&&!WaitingFor(brain)&&!Visible;

                if(!Build(npc))yield break;
                fixtureUrl=url;fixtureReport=new JObject();ready=true;
                const string rejected="The interface fixture rejects this line.";
                Submit(rejected);
                float until=Time.realtimeSinceStartup+10;
                while(busy&&Time.realtimeSinceStartup<until)yield return null;
                report["waitRetry"]=phase==Phase.Draft&&!WaitingFor(brain)&&!npcBubble.Visible&&playerBubble.Input.text==rejected;

                // The reply is selected now, but its first caption may still be queued for speech.
                // Only the local interface fixture receives this audio request.
                speech=new ConversationSpeech(host,url){Witness=_=>{}};
                WaitForReply("Are you ready?");
                const string reply="I am ready.";
                DisplayTalk("interface-wait",reply,null);
                bool queued=speech.Busy,keptDots=true;
                var dots=new HashSet<string>();
                until=Time.realtimeSinceStartup+8;
                while(speech.Busy&&Time.realtimeSinceStartup<until){
                    var value=npcBubble.ReadRenderedText();
                    keptDots &= WaitingFor(brain)&&(value=="."||value==".."||value=="...");
                    dots.Add(value);yield return null;
                }
                report["waitVoiceQueued"]=queued;
                report["waitVoiceReply"]=!speech.Busy&&!WaitingFor(brain)&&npcBubble.ReadRenderedText()==reply&&
                    (!queued||(keptDots&&dots.Count>=2&&speech.LastError!=null));
            }finally{Close();}
        }
    }
}
