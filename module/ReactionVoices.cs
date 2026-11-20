using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using FMOD;
using FMOD.Studio;
using FMODUnity;
using HarmonyLib;
using Lowscope.Saving;
using Newtonsoft.Json.Linq;
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
            var birth = SaveMaster.GetSaveCreationTime().ToString("yyyy-MM-ddTHH:mm:ss",CultureInfo.InvariantCulture);
            var group = actor.CharacterInfo.isMale ? male : female;
            var assigned=group[VoiceAssignment.Index(birth,player.CharacterInfo.id,owner,native,group.Length,out pitch)];
            if(actor!=player)return assigned;
            using (var sha = SHA256.Create())
            {
                RefreshSettings();
                int slot = IsolatedGameRun.Active?.SourceSlot ?? SaveMaster.GetActiveSlot();
                var seed = string.Join("\0",slot.ToString(CultureInfo.InvariantCulture),birth,player.CharacterInfo.id);
                var campaign = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(seed)),0,16).Replace("-","").ToLowerInvariant();
                var setting = voiceSettings?["players"]?[campaign] ?? voiceSettings?["default"];
                pitch = Math.Max(-2,Math.Min(2,(int?)setting?["pitch"] ?? 0));
                string selected = (string)setting?["voice"];
                return selected != null && (male.Contains(selected) || female.Contains(selected)) ? selected : assigned;
            }
        }

        internal int Played { get; private set; }
        internal string LastProfile { get; private set; }
        internal string LastCue { get; private set; }
        internal string[] EventPaths => Open() ? events.Keys.OrderBy(s => s,StringComparer.Ordinal).ToArray() : Array.Empty<string>();
        internal bool IsActive => active.Any(p => p.Channel.isPlaying(out bool playing) == RESULT.OK && playing);

        bool TryPlay(ChatController chat, EventInstance original)
        {
            if (!Enabled || !chat || !original.isValid() ||
                original.getDescription(out var description) != RESULT.OK ||
                description.getPath(out string path) != RESULT.OK || path == null ||
                !path.StartsWith(ChatPrefix,StringComparison.Ordinal) || !Open() ||
                !events.TryGetValue(path,out string cue)) return false;
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
                if (Plugin.Voices?.TryPlay(__instance,__0) != true) return true;
                __0.release();
                return false;
            }
        }
    }
}
