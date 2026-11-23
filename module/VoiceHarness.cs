using System;
using System.Collections;
using System.Linq;
using System.Threading;
using FMOD.Studio;
using FMODUnity;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using Sonity;
using UnityEngine;
using _Mirthwood.Scripts.Demo.AI;
using _Mirthwood.Scripts.Demo.Chat;
using _Mirthwood.Scripts.Demo.Combat;
using _Mirthwood.Scripts.Demo.ScriptableObjects;

namespace Mirthwood.Echoes
{
    internal static class VoiceHarness
    {
        internal enum Playback { Quiet, Cast, Native }
        internal const string Greeting = "event:/UI/Player Sounds/Chat Sounds/Male/Male Greet";
        internal const string Icon = "event:/UI/Player Sounds/Chat Sounds/Chat Icon Bubble";
        static readonly System.Reflection.MethodInfo PlaySound = AccessTools.Method(typeof(ChatController), "PlaySound", new[] { typeof(EventInstance) });
        static int starts;
        static readonly EVENT_CALLBACK ObserveStart = (type, instance, data) =>
        {
            Interlocked.Increment(ref starts);
            return FMOD.RESULT.OK;
        };

        internal static bool CheckSound(ChatController chat, Playback expected, string path = Greeting)
        {
            var sound = RuntimeManager.CreateInstance(path);
            if (!sound.isValid()) return false;
            if (sound.setCallback(ObserveStart, EVENT_CALLBACK_TYPE.STARTING) != FMOD.RESULT.OK)
            {
                sound.release();
                return false;
            }
            var voices = Plugin.Voices;
            int nativeBefore = Volatile.Read(ref starts), castBefore = voices.Played;
            PlaySound.Invoke(chat, new object[] { sound });
            if (RuntimeManager.StudioSystem.flushCommands() != FMOD.RESULT.OK) return false;
            int native = Volatile.Read(ref starts) - nativeBefore, cast = voices.Played - castBefore;
            return native == (expected == Playback.Native ? 1 : 0) &&
                   cast == (expected == Playback.Cast ? 1 : 0) &&
                   (expected != Playback.Cast || voices.IsActive);
        }

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
                        PlaySound.Invoke(chat,new object[] {native});
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
            report["unrelatedPreserved"] = CheckSound(participants[0].GetComponent<ChatController>(), Playback.Native, Icon);
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
            const string id="voice-probe", first="Good morning. *I lift the sack.*", second="Farewell for now.";
            var player=PlayerCharacterController.Instance;
            var previous=npc.transform.position;
            npc.transform.position=player.transform.position+Vector3.right*1.5f;
            yield return null;
            var chat=npc.GetComponent<NPCChatController>();
            bool playingOnEntry=VoiceHarness.CheckSound(chat,VoiceHarness.Playback.Cast);
            if(!Build(npc)){npc.transform.position=previous;yield break;}
            report["carryInStopped"]=playingOnEntry&&!Plugin.Voices.IsActive;
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
                report["draftQuiet"]=VoiceHarness.CheckSound(chat,VoiceHarness.Playback.Quiet);
                report["interfaceDuringTalk"]=VoiceHarness.CheckSound(chat,VoiceHarness.Playback.Native,VoiceHarness.Icon);
                WaitForReply(first);
                report["waitingQuiet"]=VoiceHarness.CheckSound(chat,VoiceHarness.Playback.Quiet);
                PlayerLine(id,first);
                DisplayTalk(id,second,null,playerLeaves:true);
                DisplayTalk(id,second,null,playerLeaves:true);
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
                        if(npcPartial&&conversationOver&&report["farewellQuiet"]==null)
                            report["farewellQuiet"]=VoiceHarness.CheckSound(chat,VoiceHarness.Playback.Quiet);
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
                    Close();
                    report["voiceResumed"]=VoiceHarness.CheckSound(chat,VoiceHarness.Playback.Cast);
                    yield return CheckPassingFarewell(npc,report);
                    DismissPassingLine();
                    yield return CheckedProbe(report,CheckChildCalls(npc,report));
                }
            }finally{Close();if(npc)npc.transform.position=previous;}
        }

        IEnumerator CheckPassingFarewell(NPCController npc,JObject report)
        {
            const string line="Thank you, and safe travels.";
            var clips=new JArray();
            var queued=new ConversationSpeech(host,Address+"voices/speech"){Witness=clips.Add};
            if(!OfferPassingLine(npc,"voice-probe/quest","quest",line,0,queued,false))yield break;
            var chat=npc.GetComponent<NPCChatController>();
            bool pending=VoiceHarness.CheckSound(chat,VoiceHarness.Playback.Quiet),during=false,checkedWord=false;
            float until=Time.realtimeSinceStartup+65;
            while(passing!=null&&Time.realtimeSinceStartup<until){
                if(passing.Bubble!=null&&!queued.Busy)break;
                if(!checkedWord&&passing.Bubble?.SpokenWordMarked()==true){
                    checkedWord=true;during=VoiceHarness.CheckSound(chat,VoiceHarness.Playback.Quiet);
                }
                yield return null;
            }
            report["questFarewellQuiet"]=pending&&during&&clips.Count>0&&(float?)clips.Last["mixPeak"]>.0001f&&
                !queued.Busy&&passing?.Bubble?.ReadRenderedText()==line&&VoiceHarness.CheckSound(chat,VoiceHarness.Playback.Cast);
        }

        IEnumerator CheckChildCalls(NPCController npc,JObject report)
        {
            var data=Resources.Load<ChildrenData>("ChildrenData");
            var templates=new MonoBehaviour[]{data.babyVisualPrefab.GetComponentInChildren<PlaySoundsRandomly>(true),
                data.toddlerVisualPrefab.GetComponentInChildren<ToddlerAI>(true)};
            var source=new GameObject("Echoes random voice test");
            source.transform.position=npc.transform.position;
            try{
                foreach(var template in templates){
                    var type=template.GetType();
                    source.SetActive(false);
                    var emitter=(MonoBehaviour)source.AddComponent(type);
                    emitter.enabled=false;
                    var sounds=AccessTools.Field(type,"randomSounds");
                    var cue=((SoundEvent[])sounds.GetValue(template)).First();
                    sounds.SetValue(emitter,new[]{cue});
                    source.SetActive(true);
                    var timer=AccessTools.Field(type,"_nextSoundTime");
                    var play=AccessTools.Method(type,"PlayRandomSound");
                    void Trigger(){timer.SetValue(emitter,0f);play.Invoke(emitter,null);}
                    bool Playing()=>cue.GetSoundEventState(source.transform)==SoundEventState.Playing;
                    Trigger();
                    yield return Await(Playing,cue.name+" did not play before Talk.",3);
                    if(!Build(npc))yield break;
                    yield return null;
                    bool stopped=!Playing();
                    Trigger();
                    bool scheduled=(float)timer.GetValue(emitter)>Time.realtimeSinceStartup;
                    yield return null;
                    bool quiet=cue.GetSoundEventState(source.transform)==SoundEventState.NotPlaying;
                    Close();
                    yield return new WaitForSecondsRealtime(1f);
                    Trigger();
                    yield return Await(Playing,cue.name+" did not resume after Talk.",3);
                    report[type.Name]=stopped&&scheduled&&quiet;
                    cue.StopImmediate(source.transform);
                }
            }finally{Close();UnityEngine.Object.Destroy(source);}
        }
    }
}
