using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

// Run against the built module and its cast archive. All writes go into disposable fixture folders.
internal static class Program
{
    delegate string Draw(string root, string campaign, bool male, string owner, string native, string[] roster);
    delegate int Index(string birth, string player, string owner, string native, int count, out int pitch);
    delegate string Player(JObject settings, string campaign, string automatic, string[] male, string[] female, out int pitch);
    static Draw assign;
    static Index index;
    static Player playerVoice;
    static Func<string[],string> key;
    static readonly string[] Nine = Enumerable.Range(0, 9).Select(i => "voice-" + i).ToArray();
    const string Birth = "2026-08-10T06:30:00", PlayerId = "player-17";
    static string Campaign(string slot = "0", string birth = Birth, string player = PlayerId) => key(new[] { slot, birth, player });
    static T Bind<T>(Assembly module, string type, string method) where T : Delegate =>
        (T)Delegate.CreateDelegate(typeof(T), module.GetType("Mirthwood.Echoes." + type, true)
            .GetMethod(method, BindingFlags.NonPublic | BindingFlags.Static));

    static int Main(string[] args)
    {
        try
        {
            if (args.Length < 2) throw new ArgumentException("Supply the module DLL and its voice pack ZIP, then the optional game directory.");
            string game = args.Length > 2 ? args[2] : @"C:\SteamLibrary\steamapps\common\Mirthwood";
            AppDomain.CurrentDomain.AssemblyResolve += (sender, e) => {
                var filename = new AssemblyName(e.Name).Name + ".dll";
                var path = new[] { "Mirthwood_Data/Managed", "BepInEx/core" }.Select(d => Path.Combine(game, d, filename)).FirstOrDefault(File.Exists);
                return path == null ? null : Assembly.LoadFrom(path);
            };
            var module = Assembly.LoadFrom(Path.GetFullPath(args[0]));
            assign = Bind<Draw>(module, "ResidentVoicePool", "Assign");
            key = Bind<Func<string[],string>>(module, "VoiceAssignment", "Key");
            if (args.Length == 6 && args[3] == "--worker")
            {
                Console.WriteLine(JsonConvert.SerializeObject(Worker(args[4], args[5])));
                return 0;
            }
            index = Bind<Index>(module, "VoiceAssignment", "Index");
            playerVoice = Bind<Player>(module, "ReactionVoices", "PlayerVoice");
            JArray cast;
            using (var zip = ZipFile.OpenRead(args[1]))
            using (var reader = new StreamReader(zip.GetEntry("cast.json").Open()))
                cast = (JArray)JObject.Parse(reader.ReadToEnd())["voices"];
            string[] Cast(string sex) => cast.Where(v => (string)v["sex"] == sex).Select(v => (string)v["id"]).ToArray();
            var male = Cast("Male");
            var female = Cast("Female");
            var root = Path.Combine(Path.GetTempPath(), "mirthwood-echoes-voices-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                TestRounds(root, male, female);
                TestIdentities(root);
                TestRosterChanges(root);
                TestWriteFailure(root);
                TestParallel(root, args[0], args[1], game);
                TestPlayer(male, female);
            }
            finally { Directory.Delete(root, true); }
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    static void Require(bool yes, string message)
    {
        if (!yes) throw new InvalidOperationException(message);
    }

    static void TestRounds(string root, string[] male, string[] female)
    {
        var rounds = Path.Combine(root, "rounds");
        void Run(bool isMale, string[] roster)
        {
            var assignments = new Dictionary<string,string>();
            for (int round = 0; round < 3; round++)
            {
                var voices = new HashSet<string>();
                for (int seat = 0; seat < roster.Length; seat++)
                {
                    var id = round + "/" + seat;
                    string voice = assign(rounds, Campaign(), isMale, "owner", id, roster);
                    Require(voices.Add(voice), "Repeated voice before the cast was exhausted.");
                    assignments.Add(id, voice);
                    Require(assign(rounds, Campaign(), isMale, "owner", id, roster) == voice, "A retry recast a resident.");
                }
                Require(voices.SetEquals(roster), "Incomplete cast round.");
            }
            foreach (var pair in assignments.Reverse())
                Require(assign(rounds, Campaign(), isMale, "owner", pair.Key, roster.Reverse().ToArray()) == pair.Value,
                    "Reload or enumeration order changed an assignment.");
        }
        Require(!male.Intersect(female).Any() && !male.Concat(female).Contains("narrator"), "The narrator or opposite cast entered a deck.");
        Run(true, male); Run(false, female);
        rounds = Path.Combine(root, "unequal");
        Run(true, new[] { "solo" }); Run(false, new[] { "one", "two", "three" });
        Console.WriteLine($"PASS Three complete rounds for packaged casts ({male.Length}/{female.Length}), singleton and unequal casts, retries and reverse reload.");
    }

    static void TestIdentities(string root)
    {
        var storage = Path.Combine(root, "identities");
        string DrawAt(string campaign, string owner, string native, string[] pool) => assign(storage, campaign, true, owner, native, pool);
        var groups = new[] { Campaign(), Campaign("1"), Campaign(birth: "2026-08-11T06:30:00"), Campaign(player: "another") };
        for (int i = 0; i < groups.Length; i++)
            Require(DrawAt(groups[i], "saved-owner", "resident", new[] { "campaign-" + i }) == "campaign-" + i, "Campaigns shared an assignment.");
        var roster = Enumerable.Range(0, groups.Length).Select(i => "campaign-" + i).ToArray();
        for (int i = groups.Length - 1; i >= 0; i--)
            Require(DrawAt(groups[i], "saved-owner", "resident", roster) == roster[i], "Switching saves changed a known resident.");
        var replacement = DrawAt(groups[0], "saved-owner", "replacement", roster);
        var other = DrawAt(groups[0], "new-owner", "resident", roster);
        Require(new[] { roster[0], replacement, other }.Distinct().Count() == 3, "Owner or native identity collapsed.");
        var copy = Path.Combine(root, "copied-session");
        var saved = Directory.GetFiles(Path.Combine(storage, "ResidentVoices")).ToDictionary(f => f, File.ReadAllText);
        Require(assign(copy, groups[0], true, "saved-owner", "resident", new[] { "copy-only" }) == "copy-only", "Copy allocation failed.");
        Require(saved.All(f => File.ReadAllText(f.Key) == f.Value), "Copy allocation changed the original pool.");
        var missing = Path.Combine(root, "missing-identity");
        Require(assign(missing, groups[0], true, "", "resident", roster) == null &&
                assign(missing, groups[0], true, "owner", "", roster) == null &&
                assign(missing, groups[0], true, "owner", "resident", Array.Empty<string>()) == null &&
                !Directory.Exists(missing), "Incomplete inputs consumed an assignment.");
        Console.WriteLine("PASS Separate slots, save creation and player IDs, stable owner/native pairs, replacements, copied roots and missing evidence.");
    }

    static void TestRosterChanges(string root)
    {
        var storage = Path.Combine(root, "roster-changes");
        string Next(string resident, params string[] roster) => assign(storage, Campaign(), false, "owner", resident, roster);
        Require(Next("first", "old", "narrator") == "old" && Next("second", "old", "added", "added") == "added", "Narrator entered the cast or a new voice missed the current round.");
        Require(Next("first", "added", "replacement") == "replacement", "Removed voice did not draw from remaining eligible IDs.");
        Require(Next("second", "replacement", "added", "old") == "added" &&
                Next("first", "old", "added", "replacement") == "replacement", "Reintroduced or reordered roster recast an assigned resident.");
        Console.WriteLine("PASS Mid-round additions, removals, duplicates, reorder and reintroduction.");
    }

    static void TestWriteFailure(string root)
    {
        var storage = Path.Combine(root, "write-failure");
        string Next(string resident) => assign(storage, Campaign(), true, "owner", resident, Nine);
        var before = Next("existing");
        string path = Directory.GetFiles(Path.Combine(storage, "ResidentVoices"), "*.json").Single();
        string committed = File.ReadAllText(path);
        Directory.CreateDirectory(path + ".tmp");
        bool failed = false;
        try { Next("new"); } catch (Exception e) when (e is UnauthorizedAccessException || e is IOException) { failed = true; }
        Require(failed && File.ReadAllText(path) == committed && Next("existing") == before, "Failed commit changed durable state.");
        Directory.Delete(path + ".tmp");
        var retry = Next("new");
        Require(retry != before && Next("new") == retry, "Retry after storage recovery lost or spent a second voice.");
        committed = File.ReadAllText(path);
        failed = false;
        using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            try { Next("replacement-blocked"); } catch (IOException) { failed = true; }
        }
        Require(failed && File.ReadAllText(path) == committed, "Failed atomic replacement overwrote the original.");
        var recovered = Next("replacement-blocked");
        Require(recovered != before && recovered != retry && Next("replacement-blocked") == recovered, "Stale temporary state was published.");
        File.WriteAllText(path, "{}");
        failed = false;
        try { Next("existing"); } catch (JsonException) { failed = true; }
        Require(failed && File.ReadAllText(path) == "{}", "Corrupt state was silently reset.");
        Console.WriteLine("PASS Failed write, existing voice while unwritable, recovery and unreadable state.");
    }

    static Dictionary<string,string> Worker(string storage, string prefix)
    {
        var ids = new[] { "shared" }.Concat(Enumerable.Range(0, 4).Select(i => prefix + i));
        return ids.ToDictionary(id => id, id => assign(storage, Campaign(), true, "owner", id, Nine));
    }

    static void TestParallel(string root, string dll, string zip, string game)
    {
        var storage = Path.Combine(root, "parallel");
        string[] results = new string[32];
        Parallel.For(0, results.Length, i => results[i] = assign(storage, Campaign(), true, "owner", "shared", Nine));
        Require(results.Distinct().Count() == 1, "Parallel requests disagreed about one resident.");
        Process Start(string worker)
        {
            var start = new ProcessStartInfo("dotnet") {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true
            };
            foreach (var arg in new[] { Assembly.GetExecutingAssembly().Location, Path.GetFullPath(dll), Path.GetFullPath(zip), game, "--worker", storage, worker })
                start.ArgumentList.Add(arg);
            return Process.Start(start);
        }
        var observed = new Dictionary<string,string>();
        using (var first = Start("first"))
        using (var second = Start("second"))
        {
            foreach (var process in new[] { first, second })
            {
                string output = process.StandardOutput.ReadToEnd();
                process.WaitForExit();
                Require(process.ExitCode == 0, "Concurrent process failed.");
                foreach (var row in JsonConvert.DeserializeObject<Dictionary<string,string>>(output))
                {
                    if (observed.TryGetValue(row.Key, out var previous)) Require(previous == row.Value, "Processes recast a resident.");
                    observed[row.Key] = row.Value;
                }
            }
        }
        Require(observed.Count == 9 && observed.Values.Distinct().Count() == 9 && observed["shared"] == results[0], "Concurrent draws lost assignments.");
        foreach (var row in observed) Require(assign(storage, Campaign(), true, "owner", row.Key, Nine) == row.Value, "Lost a durable voice after restart.");
        Console.WriteLine("PASS Parallel requests, concurrent processes and a fresh read of every saved assignment.");
    }

    static void TestPlayer(string[] male, string[] female)
    {
        string campaign = Campaign();
        Require(campaign == "edc5a06c8fd10296b90f3cb97810ff44", "Player campaign key changed.");
        // First two bytes of the pre-pool automatic player hash for this saved identity.
        const int oldPlayerHash = 0x3178;
        int automatic = index(Birth, PlayerId, "player", PlayerId, male.Length, out _);
        Require(automatic == oldPlayerHash % male.Length &&
                index(Birth, PlayerId, "player", PlayerId, female.Length, out _) == oldPlayerHash % female.Length,
                "Automatic player selection changed.");
        index(Birth, PlayerId, "owner", "after", male.Length, out int residentPitch);
        Require(residentPitch == -2, "Resident pitch changed.");
        string Pick(JObject settings, out int pitch) => playerVoice(settings, campaign, male[automatic], male, female, out pitch);
        Require(Pick(null, out int pitch) == male[automatic] && pitch == 0, "Fresh player changed.");
        var settings = new JObject { ["default"] = new JObject { ["voice"] = female[0], ["pitch"] = -2 } };
        Require(Pick(settings, out pitch) == female[0] && pitch == -2, "Default player override failed.");
        settings["players"] = new JObject { [campaign] = new JObject { ["voice"] = male.Last(), ["pitch"] = 5 } };
        Require(Pick(settings, out pitch) == male.Last() && pitch == 2, "Campaign override or pitch limit failed.");
        settings["players"][campaign]["voice"] = "unknown";
        Require(Pick(settings, out pitch) == male[automatic] && pitch == 2, "Unavailable player override changed the fallback.");
        Console.WriteLine("PASS Original automatic player ID, both casts, default and campaign overrides, fallback and pitch.");
    }
}
