using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Newtonsoft.Json;

namespace Mirthwood.Echoes
{
    internal static class ResidentVoicePool
    {
        sealed class CastState
        {
            [JsonProperty(Required = Required.Always)]
            public Dictionary<string,string> Assignments { get; set; } = new Dictionary<string,string>();
            [JsonProperty(Required = Required.Always)]
            public HashSet<string> IssuedThisRound { get; set; } = new HashSet<string>();
        }

        internal static string Assign(string root, string campaign, bool male, string owner, string native, string[] roster)
        {
            if (string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(native) || roster == null) return null;
            if (campaign == null || campaign.Length != 32 || !campaign.All(Uri.IsHexDigit))
                throw new InvalidDataException("Unknown voice campaign.");
            var cast = roster.Where(id => !string.IsNullOrWhiteSpace(id) && id != "narrator").Distinct(StringComparer.Ordinal).ToArray();
            if (cast.Length == 0) return null;
            var folder = Path.Combine(root, "ResidentVoices");
            var path = Path.Combine(folder, campaign + (male ? "-male.json" : "-female.json"));
            var resident = JsonConvert.SerializeObject(new[] { owner, native });
            var lockName = @"Global\mirthwood.echoes.voices." + VoiceAssignment.Key(Path.GetFullPath(path).ToUpperInvariant());
            using (var mutex = new Mutex(false, lockName))
            {
                try
                {
                    if (!mutex.WaitOne(TimeSpan.FromSeconds(2))) throw new IOException("The resident voice pool is busy.");
                }
                catch (AbandonedMutexException) { /* We now own the lock. Read the last committed file. */ }
                try
                {
                    Directory.CreateDirectory(folder);
                    var state = Read(path);
                    if (state.Assignments.TryGetValue(resident, out string previous) && cast.Contains(previous))
                        return previous;
                    var available = cast.Where(id => !state.IssuedThisRound.Contains(id)).ToArray();
                    if (available.Length == 0)
                    {
                        state.IssuedThisRound.Clear();
                        available = cast;
                    }
                    string voice = available[RandomIndex(available.Length)];
                    state.IssuedThisRound.Add(voice);
                    state.Assignments[resident] = voice;
                    Write(path, state);
                    return voice;
                }
                finally { mutex.ReleaseMutex(); }
            }
        }

        static CastState Read(string path)
        {
            string json;
            try { json = File.ReadAllText(path); }
            catch (FileNotFoundException) { return new CastState(); }
            var state = JsonConvert.DeserializeObject<CastState>(json);
            if (state == null || state.Assignments.Values.Any(string.IsNullOrWhiteSpace) || state.IssuedThisRound.Any(string.IsNullOrWhiteSpace))
                throw new InvalidDataException("Unreadable resident voice pool.");
            return state;
        }

        static void Write(string path, CastState state)
        {
            var tmp = path + ".tmp";
            using (var file = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                byte[] bytes = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(state));
                file.Write(bytes, 0, bytes.Length);
                file.Flush(true);
            }
            if (File.Exists(path)) File.Replace(tmp, path, null);
            else File.Move(tmp, path);
        }

        static int RandomIndex(int count)
        {
            if (count == 1) return 0;
            // Reject the incomplete interval so every remaining ID has exactly the same chance.
            uint limit = uint.MaxValue - uint.MaxValue % (uint)count;
            using (var random = RandomNumberGenerator.Create())
            {
                var bytes = new byte[4];
                uint sample;
                do { random.GetBytes(bytes); sample = BitConverter.ToUInt32(bytes, 0); } while (sample >= limit);
                return (int)(sample % (uint)count);
            }
        }
    }
}
