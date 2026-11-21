using System;
using System.Collections;
using System.Linq;
using FMOD.Studio;
using FMODUnity;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using UnityEngine;
using _Mirthwood.Scripts.Demo.Chat;
using _Mirthwood.Scripts.Demo.Combat;

namespace Mirthwood.Echoes
{
    internal static class VoiceHarness
    {
        internal static IEnumerator Run(JObject report,NativeConversation talk)
        {
            var voices = Plugin.Voices;
            if(Plugin.World() is object world)report["voiceWorld"]=JObject.FromObject(world);
            NPCController[] Residents()=>UnityEngine.Object.FindObjectsOfType<NPCController>()
                .Where(n => n && n.isActiveAndEnabled && voices.Profile(n) != null && n.GetComponent<NPCChatController>())
                .GroupBy(n => n.CharacterInfo.isMale).Select(g => g.OrderBy(n=>
                    (n.transform.position-PlayerCharacterController.Instance.transform.position).sqrMagnitude).First()).ToArray();
            var residents=Residents();
            float deadline=Time.realtimeSinceStartup+10;
            while(residents.Length<2 && Time.realtimeSinceStartup<deadline){
                yield return new WaitForSecondsRealtime(.4f);residents=Residents();
            }
            var participants = residents.Cast<Character>().Concat(new Character[] {PlayerCharacterController.Instance}).ToArray();
            var method = AccessTools.Method(typeof(ChatController),"PlaySound",new[] {typeof(EventInstance)});
            var rows = new JArray();
            report["voiceRows"] = rows;
            foreach (var actor in participants)
            {
                var chat = actor.GetComponent<ChatController>();
                string expected = voices.Profile(actor);
                int count = 0;
                bool playing = false,greeted = false;
                foreach (var path in voices.EventPaths)
                {
                    bool greeting=path.EndsWith("/Male Greet",StringComparison.Ordinal);
                    int before = voices.Played;
                    EventInstance native=default;
                    try{
                        native=RuntimeManager.CreateInstance(path);
                        native.setVolume(greeting ? 0.7f : 0f);
                        method.Invoke(chat,new object[] {native});
                    }catch(Exception e){
                        if(native.isValid())native.release();
                        if(report["probeError"]==null)report["probeError"]=path+" · "+e.GetBaseException().Message;
                    }
                    if (voices.Played == before+1 && voices.LastProfile == expected) count++;
                    playing |= voices.IsActive;
                    if(greeting&&voices.Played==before+1&&voices.LastCue=="greet"){
                        greeted=true;
                        yield return new WaitForSecondsRealtime(1.2f);
                    }
                    yield return null;
                }
                rows.Add(new JObject {["actor"] = actor.CharacterInfo.name,["profile"] = expected,
                    ["player"] = actor == PlayerCharacterController.Instance,["male"] = actor.CharacterInfo.isMale,
                    ["played"] = playing,["greeting"] = greeted,["events"] = count});
            }
            int initial = voices.Played;
            var unrelated = RuntimeManager.CreateInstance("event:/UI/Player Sounds/Chat Sounds/Chat Icon Bubble");
            unrelated.setVolume(0f);
            method.Invoke(participants[0].GetComponent<ChatController>(),new object[] {unrelated});
            report["unrelatedPreserved"] = initial == voices.Played;
            var player=PlayerCharacterController.Instance;
            var speaker=residents.OrderBy(n=>voices.Profile(n)==voices.Profile(player))
                .ThenBy(n=>(n.transform.position-player.transform.position).sqrMagnitude).FirstOrDefault();
            if(speaker)yield return talk.CheckVoicedLines(speaker,report);
        }
    }

    internal sealed partial class NativeConversation
    {
        internal IEnumerator CheckVoicedLines(NPCController npc,JObject report)
        {
            if(IsolatedGameRun.Active?.PathsMatch()!=true || Visible)yield break;
            const string id="voice-probe", first="Good morning. *I lift the sack.*", second="The mill is open.";
            var player=PlayerCharacterController.Instance;
            var previous=npc.transform.position;
            npc.transform.position=player.transform.position+Vector3.right*1.5f;
            yield return null;
            if(!Build(npc)){npc.transform.position=previous;yield break;}
            targetIdentity=new{world=Plugin.World(),owner=Plugin.Owner(npc),nativeId=npc.CharacterInfo.id};
            FaceForTalk(npc.GetComponent<NPCChatController>(),player.GetComponent<PlayerChat>());
            var rows=new JArray();report["speech"]=rows;
            var playerText=new JArray();var npcText=new JArray();report["playerCaptions"]=playerText;report["npcCaptions"]=npcText;
            report["playerVoice"]=Plugin.Voices.Select(player,out int playerPitch);
            report["residentVoice"]=Plugin.Voices.Select(npc,out int residentPitch);
            report["playerPitch"]=playerPitch;report["residentPitch"]=residentPitch;
            speech=new ConversationSpeech(host,Address+"voices/speech");
            speech.Witness=rows.Add;
            try{
                DraftPause.End();phase=Phase.Waiting;playerBubble.Show(first);
                PlayerLine(id,first);
                DisplayTalk(id,second,null);
                DisplayTalk(id,second,null);
                float until=Time.realtimeSinceStartup+95;
                bool playerPartial=false,npcPartial=false,actionItalic=false,requestedCapture=false,playerCapture=false;
                while(Visible && speech.Busy && Time.realtimeSinceStartup<until){
                    if(playerBubble.Visible){
                        var text=playerBubble.ReadRenderedText();
                        if((string)playerText.Last!=text && playerText.Count<32)playerText.Add(text);
                        playerPartial|=playerBubble.ShowsCompleteMessage(first)&&playerBubble.SpokenWordMarked();
                        actionItalic|=rows.Count==1&&playerBubble.Styled("lift",true);
                        if(actionItalic&&!requestedCapture){
                            requestedCapture=true;
                            host.StartCoroutine(Capture("voice-player.png",ok=>playerCapture=ok));
                        }
                    }
                    if(npcBubble.Visible){
                        var text=npcBubble.ReadRenderedText();
                        if((string)npcText.Last!=text && npcText.Count<32)npcText.Add(text);
                        npcPartial|=npcBubble.ShowsCompleteMessage(second)&&npcBubble.SpokenWordMarked();
                    }
                    yield return null;
                }
                if(Visible){
                    yield return new WaitForEndOfFrame();
                    report["speechComplete"]=!speech.Busy&&npcBubble.ReadRenderedText()==second;
                    if(speech.LastError!=null)report["probeError"]=speech.LastError;
                    report["npcPartial"]=npcPartial;report["playerPartial"]=playerPartial;report["actionItalic"]=actionItalic;
                    report["speakerAnchor"]=npcBubble.AnchoredTo(npc.transform);
                    yield return Capture("voice-resident.png",ok=>report["residentCapture"]=ok);
                    report["playerCapture"]=playerCapture;
                    // Exercise a refused loopback connection without interrupting the live voice service.
                    speech.Stop();
                    speech=new ConversationSpeech(host,"http://127.0.0.1:1/api/voices/speech");
                    var failedClips=new JArray();speech.Witness=failedClips.Add;
                    const string offline="The mill is open. *I nod.*";
                    DisplayTalk("voice-offline",offline,null);
                    yield return Await(()=>!speech.Busy,"Offline speech did not return the text.",48);
                    yield return new WaitForEndOfFrame();
                    report["audioFallback"]=speech.LastError!=null && failedClips.Count==0 &&
                        npcBubble.ReadRenderedText()==new BubbleText(offline,false).Plain && npcBubble.Styled("nod",true);
                }
            }finally{Close();if(npc)npc.transform.position=previous;}
        }
    }
}
