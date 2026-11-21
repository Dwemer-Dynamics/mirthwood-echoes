using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using FMOD;
using FMODUnity;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Networking;
using _Mirthwood.Scripts.Demo.Combat;

namespace Mirthwood.Echoes
{
    // The text and the native turn can finish independently of the audio service.
    internal sealed class ConversationSpeech
    {
        sealed class Line
        {
            public string Key, Text, Voice;
            public int Pitch;
            public bool ShowPlaceholder;
            public Action<string,bool> Draw;
        }
        readonly Plugin host;
        readonly string speechAddress;
        readonly Func<bool> voicesEnabled;
        readonly Queue<Line> waiting = new Queue<Line>();
        readonly Dictionary<string,string> offered = new Dictionary<string,string>();
        Coroutine runner;
        UnityWebRequest request;
        Line current;
        Sound sound;
        Channel channel;
        internal bool Busy => current != null || waiting.Count != 0;
        internal Action<JObject> Witness;
        internal string LastError {get;private set;}

        internal ConversationSpeech(Plugin host,string speechAddress="http://127.0.0.1:17841/api/voices/speech",Func<bool> voicesEnabled=null)
        {
            this.host = host;
            this.speechAddress = speechAddress;
            this.voicesEnabled = voicesEnabled ?? (() => Plugin.Voices?.Enabled == true);
        }

        internal void Say(string key, Character speaker, string text, Action<string,bool> draw,bool showPlaceholder=true)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            if (offered.TryGetValue(key,out string previous))
            {
                if (text==previous) return;
                offered[key]=text;
                // A recovered final receipt owns the text. Update an unsaid line in place.
                foreach(var pending in waiting)
                    if(pending.Key==key){pending.Text=text;pending.Draw=draw;return;}
                if(current?.Key==key)Stop();
                draw?.Invoke(text,false);return;
            }
            offered.Add(key,text);
            var cast = Plugin.Voices;
            if (cast == null || !voicesEnabled() || (IsolatedGameRun.Active != null && Witness == null))
            {
                if(Busy)Stop(true);
                draw?.Invoke(text,false); return;
            }
            string voice = cast.Select(speaker,out int pitch);
            if (voice == null) { draw?.Invoke(text,false); return; }
            waiting.Enqueue(new Line { Key=key, Text=text, Voice=voice, Pitch=pitch, Draw=draw, ShowPlaceholder=showPlaceholder });
            if (runner == null) runner = host.StartCoroutine(Drain());
        }

        IEnumerator Drain()
        {
            // Let StartCoroutine publish its handle even when a local request fails immediately.
            yield return null;
            while (waiting.Count > 0)
            {
                current = waiting.Dequeue();
                if (voicesEnabled())
                    yield return Speak(current);
                current.Draw?.Invoke(current.Text,false);
                current = null;
            }
            runner = null;
        }

        IEnumerator Speak(Line line)
        {
            if(line.ShowPlaceholder)line.Draw?.Invoke("…",true);
            request = NativeConversation.Post(speechAddress,
                new {voice=line.Voice,text=line.Text});
            request.timeout = 40;
            var operation = request.SendWebRequest();
            while (!operation.isDone && voicesEnabled()) yield return null;
            JArray parts = null;
            if (operation.isDone && request.responseCode == 200)
            {
                try { parts = JArray.Parse(request.downloadHandler.text); }
                catch (Newtonsoft.Json.JsonException) { LastError="Unreadable speech response."; }
            }
            else if(operation.isDone)LastError=NativeConversation.Error(request,"Speech request returned HTTP "+request.responseCode);
            if (!operation.isDone) request.Abort();
            request.Dispose(); request = null;
            if (parts == null) yield break;
            foreach (var part in parts)
            {
                if (!voicesEnabled()) yield break;
                int pitch = (bool?)part["narrated"] == true ? 0 : line.Pitch;
                byte[] bytes;
                try { bytes = Convert.FromBase64String((string)part["audio"] ?? ""); }
                catch (FormatException) { yield break; }
                if (!(part["cues"] is JArray cues) || !StartClip(bytes,pitch)) yield break;
                int next=0, observed=0, lastEnd=0;
                uint furthest=0;
                float appliedPitch=0,mixPeak=0;
                DSP meter=default;
                if(Witness!=null){
                    channel.getPitch(out appliedPitch);
                    if(channel.getDSP(CHANNELCONTROL_DSP_INDEX.FADER,out meter)==RESULT.OK)meter.setMeteringEnabled(false,true);
                }
                while (voicesEnabled() && channel.isPlaying(out bool on) == RESULT.OK && on)
                {
                    if (channel.getPosition(out uint ms,TIMEUNIT.MS) == RESULT.OK)
                    {
                        if(meter.hasHandle()&&meter.getMeteringInfo(IntPtr.Zero,out var levels)==RESULT.OK&&levels.peaklevel!=null)
                            for(int ch=0;ch<levels.numchannels;ch++)mixPeak=Math.Max(mixPeak,levels.peaklevel[ch]);
                        furthest = Math.Max(furthest,ms);
                        int previous = next;
                        while (next < cues.Count && (int)cues[next]["ms"] <= ms)
                            lastEnd = Math.Max(lastEnd,Math.Min(line.Text.Length,(int)cues[next++]["end"]));
                        if (next != previous)
                        {
                            if (lastEnd < line.Text.Length && line.Text[lastEnd] == '*') lastEnd++;
                            line.Draw?.Invoke(line.Text.Substring(0,lastEnd),true);
                            observed++;
                        }
                    }
                    yield return null;
                }
                ReleaseClip();
                if (Witness != null)
                using(var sha=SHA256.Create())
                    Witness(new JObject {["line"]=line.Key,["voice"]=(string)part["voice"],["pitch"]=pitch,
                        ["channelPitch"]=appliedPitch,["mixPeak"]=mixPeak,["waveSha256"]=BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-",""),
                        ["narrated"]=(bool)part["narrated"],["positionMs"]=(int)furthest,["captionSteps"]=observed,
                        ["wordCues"]=cues.Count,["timing"]=(string)part["timing"],
                        ["text"]=line.Text.Substring((int)part["start"],(int)part["end"]-(int)part["start"])});
            }
        }

        bool StartClip(byte[] bytes,int pitch)
        {
            if (bytes.Length < 44) return false;
            var core=RuntimeManager.CoreSystem;
            var result=RuntimeManager.StudioSystem.getBus("bus:/",out var bus);
            if(result!=RESULT.OK){LastError="FMOD master bus: "+result;return false;}
            result=bus.getChannelGroup(out var group);
            if(result!=RESULT.OK){LastError="FMOD master channel group: "+result;return false;}
            var info=new CREATESOUNDEXINFO {cbsize=Marshal.SizeOf(typeof(CREATESOUNDEXINFO)),length=(uint)bytes.Length};
            result=core.createSound(bytes,MODE.OPENMEMORY|MODE.CREATESAMPLE|MODE._2D|MODE.LOOP_OFF,ref info,out sound);
            if(result!=RESULT.OK){LastError="FMOD WAV decode: "+result;return false;}
            result=core.playSound(sound,group,true,out channel);
            if(result==RESULT.OK)result=channel.setPitch((float)Math.Pow(2,pitch/12.0));
            if(result==RESULT.OK)result=channel.setPaused(false);
            if(result==RESULT.OK)return true;
            LastError="FMOD speech playback: "+result;ReleaseClip();return false;
        }
        void ReleaseClip()
        {
            if (channel.hasHandle()) { channel.stop(); channel=default; }
            if (sound.hasHandle()) { sound.release(); sound=default; }
        }
        internal void Stop(bool reveal=false)
        {
            if (runner!=null) { host.StopCoroutine(runner); runner=null; }
            if (request!=null) { request.Abort(); request.Dispose(); request=null; }
            ReleaseClip();
            if (reveal) {
                current?.Draw?.Invoke(current.Text,false);
                foreach(var line in waiting) line.Draw?.Invoke(line.Text,false);
            }
            current=null; waiting.Clear();
        }
    }
}
