using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using FMOD;
using FMOD.Studio;
using FMODUnity;
using HarmonyLib;
using Lowscope.Saving;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Sonity;
using UnityEngine;
using _Mirthwood.Scripts.Demo.AI;
using _Mirthwood.Scripts.Demo.Chat;
using _Mirthwood.Scripts.Demo.Combat;

namespace Mirthwood.Echoes
{
    internal sealed class ReactionVoices : IDisposable
    {
        internal const string Filename = "Mirthwood-Echoes-Voices.zip";
        const string ChatPrefix = "event:/UI/Player Sounds/Chat Sounds/";
        readonly ConfigEntry<bool> enabled;
        readonly ManualLogSource log;
        readonly List<(Channel Channel, Sound Sound)> active = new List<(Channel,Sound)>();
        readonly List<EventInstance> nativeReactions = new List<EventInstance>();
        readonly List<(SoundEvent Cue, Transform Owner)> childCalls = new List<(SoundEvent,Transform)>();
        readonly HashSet<string> warned = new HashSet<string>();
        ZipArchive pack;
        Dictionary<string,string> events;
        string[] male, female;
        bool attempted;
        readonly string settingsFile = Path.Combine(Paths.ConfigPath,"mirthwood.echoes.voices.json");
        DateTime lastWriteUtc;
        float nextSettingsCheck;
        JObject voiceSettings;
        internal bool Enabled { get { RefreshSettings(); return enabled.Value && ((bool?)voiceSettings?["enabled"] ?? true); } }

        void RefreshSettings()
        {
            if (UnityEngine.Time.unscaledTime < nextSettingsCheck) return;
            nextSettingsCheck = UnityEngine.Time.unscaledTime + 0.5f;
            try
            {
                var write = File.GetLastWriteTimeUtc(settingsFile);
                if (write == lastWriteUtc) return;
                var next = File.Exists(settingsFile) ? JObject.Parse(File.ReadAllText(settingsFile)) : null;
                voiceSettings = next; lastWriteUtc = write;
            }
            catch (Exception error) { Warn("settings",error.GetBaseException().Message); }
        }

        internal ReactionVoices(ConfigEntry<bool> enabled, ManualLogSource log)
        {
            this.enabled = enabled;
            this.log = log;
        }

        bool Open()
        {
            if (attempted) return pack != null;
            attempted = true;
            try
            {
                var path = Path.Combine(Path.GetDirectoryName(typeof(ReactionVoices).Assembly.Location), Filename);
                var opened = new ZipArchive(File.OpenRead(path), ZipArchiveMode.Read);
                try
                {
                    JObject cast = ReadJson(opened, "cast.json");
                    var voices = (JArray)cast["voices"];
                    male = Roster(voices, "Male");
                    female = Roster(voices, "Female");
                    events = ReadJson(opened, "cues.json")["events"].ToObject<Dictionary<string,string>>();
                    if (male.Length == 0 || female.Length == 0 || events.Count == 0)
                        throw new InvalidDataException("The cast or reaction list is empty.");
                    pack = opened;
                }
                catch { opened.Dispose(); throw; }
            }
            catch (Exception error) { Warn("pack",error.GetBaseException().Message); }
            return pack != null;
        }

        static JObject ReadJson(ZipArchive file, string name)
        {
            using (var input = new StreamReader(file.GetEntry(name).Open())) return JObject.Parse(input.ReadToEnd());
        }

        static string[] Roster(JArray voices, string sex) => voices.Where(v => (string)v["sex"] == sex)
            .Select(v => (string)v["id"]).ToArray();

        internal string Profile(Character actor) => Select(actor,out _);
        internal string Select(Character actor,out int pitch)
        {
            pitch = 0;
            if (!actor || actor.CharacterInfo == null || !SaveMaster.IsSlotLoaded() || !Open()) return null;
            var player = PlayerCharacterController.Instance;
            if (!player || player.CharacterInfo == null || string.IsNullOrEmpty(player.CharacterInfo.id)) return null;
            var owner = actor == player ? "player" : Plugin.Owner(actor);
            var native = actor.CharacterInfo.id;
            if (string.IsNullOrEmpty(owner) || string.IsNullOrEmpty(native)) return null;
            var created = SaveMaster.GetSaveCreationTime();
            if (actor != player && (created == default || string.IsNullOrWhiteSpace(player.CharacterInfo.id))) return null;
            var birth = created.ToString("yyyy-MM-ddTHH:mm:ss",CultureInfo.InvariantCulture);
            var group = actor.CharacterInfo.isMale ? male : female;
            int index = VoiceAssignment.Index(birth,player.CharacterInfo.id,owner,native,group.Length,out pitch);
            int slot = IsolatedGameRun.Active?.SourceSlot ?? SaveMaster.GetActiveSlot();
            string campaign = VoiceAssignment.Key(slot.ToString(CultureInfo.InvariantCulture), birth, player.CharacterInfo.id);
            if (actor == player)
            {
                RefreshSettings();
                return PlayerVoice(voiceSettings, campaign, group[index], male, female, out pitch);
            }
            try
            {
                var storage = IsolatedGameRun.Active?.Root ?? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MirthwoodEchoes");
                return ResidentVoicePool.Assign(storage, campaign, actor.CharacterInfo.isMale, owner, native, group);
            }
            catch (Exception error) when (error is IOException || error is InvalidDataException ||
                                          error is UnauthorizedAccessException || error is JsonException)
            {
                Warn("resident-pool", "Could not read or save a resident voice. " + error.GetBaseException().Message);
                return null;
            }
        }

        static string PlayerVoice(JObject settings, string campaign, string automatic, string[] male, string[] female, out int pitch)
        {
            var setting = settings?["players"]?[campaign] ?? settings?["default"];
            pitch = Math.Max(-2,Math.Min(2,(int?)setting?["pitch"] ?? 0));
            string selected = (string)setting?["voice"];
            return selected != null && (male.Contains(selected) || female.Contains(selected)) ? selected : automatic;
        }

        internal int Played { get; private set; }
        internal string LastProfile { get; private set; }
        internal string LastCue { get; private set; }
        internal string[] EventPaths => Open() ? events.Keys.OrderBy(s => s,StringComparer.Ordinal).ToArray() : Array.Empty<string>();
        internal bool IsActive => active.Any(p => p.Channel.isPlaying(out bool playing) == RESULT.OK && playing);

        bool HandleChatSound(ChatController chat, EventInstance original)
        {
            if (!original.isValid() || original.getDescription(out var description) != RESULT.OK ||
                description.getPath(out string path) != RESULT.OK || path == null ||
                !(path.StartsWith(ChatPrefix + "Male/", StringComparison.Ordinal) ||
                  path.StartsWith(ChatPrefix + "Female/", StringComparison.Ordinal))) return false;

            if (Plugin.TalkActive || TryPlay(chat, original, description, path))
            {
                original.release();
                return true;
            }
            // Vanilla starts and releases this instance. Its handle stays valid until playback ends.
            nativeReactions.Add(original);
            return false;
        }

        bool TryPlay(ChatController chat, EventInstance original, EventDescription description, string path)
        {
            if (!Enabled || !chat || !Open() || !events.TryGetValue(path,out string cue)) return false;
            string profile = Select(chat.Character,out int pitch);
            if (profile == null) return false;
            string entryName = "voices/" + profile + "/" + cue + ".wav";
            try
            {
                var entry = pack.GetEntry(entryName);
                if (entry == null || entry.Length < 44 || entry.Length > 512000) return false;
                byte[] bytes;
                using (var reader = new BinaryReader(entry.Open())) bytes = reader.ReadBytes((int)entry.Length);
                if (bytes.Length != entry.Length || description.is3D(out bool spatial) != RESULT.OK) return false;
                var studio = RuntimeManager.StudioSystem;
                if (studio.getBus("bus:/",out var bus) != RESULT.OK || bus.getChannelGroup(out var group) != RESULT.OK) return false;
                var core = RuntimeManager.CoreSystem;
                var info = new CREATESOUNDEXINFO {cbsize = Marshal.SizeOf(typeof(CREATESOUNDEXINFO)), length = (uint)bytes.Length};
                var mode = MODE.OPENMEMORY | MODE.CREATESAMPLE | MODE.LOOP_OFF | (spatial ? MODE._3D : MODE._2D);
                if (core.createSound(bytes,mode,ref info,out var sound) != RESULT.OK) return false;
                bool playing = false;
                Channel channel = default;
                try
                {
                    if (core.playSound(sound,group,true,out channel) != RESULT.OK) return false;
                    if (spatial)
                    {
                        var position = RuntimeUtils.ToFMODVector(chat.transform.position);
                        var velocity = new VECTOR();
                        if (channel.set3DAttributes(ref position,ref velocity) != RESULT.OK) return false;
                        if (description.getMinMaxDistance(out float min,out float max) == RESULT.OK)
                            channel.set3DMinMaxDistance(min,max);
                    }
                    if (original.getVolume(out float volume) == RESULT.OK) channel.setVolume(volume);
                    if (channel.setPitch((float)Math.Pow(2,pitch/12.0)) != RESULT.OK) return false;
                    if (channel.setPaused(false) != RESULT.OK) return false;
                    active.Add((channel,sound));
                    playing = true;
                    Played++; LastProfile = profile; LastCue = cue;
                    return true;
                }
                finally
                {
                    if (!playing) { if (channel.hasHandle()) channel.stop(); sound.release(); }
                }
            }
            catch (Exception error) { Warn(entryName,error.GetBaseException().Message); return false; }
        }

        void Warn(string key,string message)
        {
            if (warned.Add(key)) log.LogWarning("Echoes voice audio: " + message);
        }

        internal void Collect()
        {
            nativeReactions.RemoveAll(sound => !sound.isValid());
            childCalls.RemoveAll(call => !call.Cue || !call.Owner ||
                call.Cue.GetSoundEventState(call.Owner) == SoundEventState.NotPlaying);
            if (active.Count>0 && !Enabled)
            {
                foreach (var item in active) item.Channel.stop();
            }
            for (int i = active.Count-1; i >= 0; i--)
            {
                var item = active[i];
                if (item.Channel.isPlaying(out bool playing) == RESULT.OK && playing) continue;
                item.Sound.release();
                active.RemoveAt(i);
            }
        }

        internal void StopPlayingReactions()
        {
            foreach (var item in active) item.Channel.stop();
            foreach (var original in nativeReactions)
                if (original.isValid()) original.stop(FMOD.Studio.STOP_MODE.IMMEDIATE);
            nativeReactions.Clear();
            foreach (var call in childCalls)
                if (call.Cue && call.Owner) call.Cue.StopImmediate(call.Owner);
            childCalls.Clear();
        }

        public void Dispose()
        {
            foreach (var item in active) { item.Channel.stop(); item.Sound.release(); }
            active.Clear(); pack?.Dispose(); pack = null;
        }

        [HarmonyPatch(typeof(ChatController),"PlaySound",new[] {typeof(EventInstance)})]
        internal static class NativeSound
        {
            static bool Prefix(ChatController __instance, EventInstance __0)
            {
                return Plugin.Voices?.HandleChatSound(__instance, __0) != true;
            }
        }

        [HarmonyPatch]
        internal static class ChildSounds
        {
            static IEnumerable<MethodBase> TargetMethods()
            {
                yield return AccessTools.Method(typeof(PlaySoundsRandomly), "PlayRandomSound");
                yield return AccessTools.Method(typeof(ToddlerAI), "PlayRandomSound");
            }

            // Leave each emitter's native timing and random selection in place.
            static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
            {
                var body = instructions.ToList();
                var native = AccessTools.Method(typeof(SoundEvent), nameof(SoundEvent.Play), new[] { typeof(Transform) });
                if (native == null || body.Count(instruction => instruction.Calls(native)) != 1)
                    throw new InvalidOperationException("Expected one Sonity playback call in " + __originalMethod.DeclaringType.Name + "." + __originalMethod.Name);
                return Transpilers.MethodReplacer(body, native, AccessTools.Method(typeof(ChildSounds), nameof(Play)));
            }

            static void Play(SoundEvent cue, Transform owner)
            {
                var voices = Plugin.Voices;
                if (voices != null && IsChildCall(cue.name))
                {
                    if (Plugin.TalkActive) return;
                    var call = (cue, owner);
                    if (!voices.childCalls.Contains(call)) voices.childCalls.Add(call);
                }
                cue.Play(owner);
            }

            static bool IsChildCall(string name) =>
                name == "baby1SE" || name == "baby2SE" || name == "baby3SE" ||
                name == "toddler1SE" || name == "toddler2SE" || name == "toddler3SE";
        }
    }
}
