using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

public sealed record PackageFile(string Path, string Sha256, string Kind);
public sealed record ModulePackage(string Id, string Name, string Version, PackageFile[] Files);
public sealed record Installation(string Status, string Version, string Detail, DateTime CheckedUtc);

public sealed class ModuleInstaller(RuntimePaths paths,VoiceCast voices,ILogger<ModuleInstaller> log) : BackgroundService
{
    const string ModulePath = "BepInEx/plugins/MirthwoodEchoes/Mirthwood.Echoes.dll";
    const string LegacyPath = "BepInEx/plugins/MirthwoodAI/MirthwoodAI.dll";

    // Original published binaries may have been installed before this server acquired its receipt.
    static bool IsOriginalRelease(string hash) => hash is
        "E4223DF760462EE0716590461644999EE5BBCCF48B9B374A3877EF29F04E0FDC" or
        "54A50C39E84773D9481897F46ABD928A7EEABA7731BCD8396F6F1C00BD9CF22E";
    sealed record Pending(string Path,string? Before,string After);
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    readonly SemaphoreSlim gate = new(1);
    readonly Dictionary<string,(long Length,long Modified,string Sha)> voiceHashes = new();
    volatile Installation status = new("checking", "", "Checking the server package.", DateTime.UtcNow);
    public Installation Status => status;

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            var installation=await Check(stop,true);
            if (installation.Status=="ready")
            {
                try { await voices.RefreshModuleSettings(); }
                catch (Exception e) when (!stop.IsCancellationRequested) { log.LogWarning(e,"Could not publish the player voice settings."); }
            }
            await Task.Delay(TimeSpan.FromSeconds(30), stop);
        }
    }

    string InstalledHash(string path,bool voice,bool background)
    {
        if(!voice)return Hash(path);
        var info=new FileInfo(path);
        long size=info.Length, modified=info.LastWriteTimeUtc.Ticks;
        if(background && voiceHashes.TryGetValue(path,out var cached) && cached.Length==size && cached.Modified==modified)
            return cached.Sha;
        string sha=Hash(path);
        voiceHashes[path]=(size,modified,sha);
        return sha;
    }

    public async Task<Installation> Check(CancellationToken ct,bool background=false)
    {
        await gate.WaitAsync(ct);
        string version = "";
        try
        {
            var bundle = Path.Combine(AppContext.BaseDirectory, "bundle");
            var package = JsonSerializer.Deserialize<ModulePackage>(await File.ReadAllBytesAsync(Path.Combine(bundle, "manifest.json"), ct), Json)
                ?? throw new InvalidDataException("The bundled module manifest is missing.");
            version = package.Version;
            if (package.Id != ModIdentity.Id || package.Name != ModIdentity.Name ||
                package.Files.Count(f => f.Kind == "module" && f.Path == ModulePath) != 1 ||
                package.Files.DistinctBy(f => f.Path, StringComparer.OrdinalIgnoreCase).Count() != package.Files.Length)
                throw new InvalidDataException("Invalid module manifest.");
            var game = Path.GetFullPath(paths.Game);
            if (!File.Exists(Path.Combine(game, "Mirthwood.exe")) || !File.Exists(Path.Combine(game, "Mirthwood_Data", "Managed", "Assembly-CSharp.dll")))
                return Set("game_missing", version, "The configured Steam installation could not be found.");

            var receiptName = "module-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(game)))[..16] + ".json";
            var ledger = Path.Combine(paths.State, receiptName);
            var owned = File.Exists(ledger) ? JsonSerializer.Deserialize<Dictionary<string,string>>(await File.ReadAllBytesAsync(ledger, ct), Json)! : new();
            var journal = ledger + ".pending";
            if(File.Exists(journal))
            {
                var pending=JsonSerializer.Deserialize<Pending>(await File.ReadAllBytesAsync(journal,ct),Json) ?? throw new InvalidDataException("Unreadable installation journal.");
                var dest=Under(game,pending.Path);
                var actual=File.Exists(dest)?Hash(dest):null;
                if(actual==pending.After){owned[pending.Path]=pending.After;AtomicWrite(ledger,JsonSerializer.SerializeToUtf8Bytes(owned,Json));}
                else if(actual!=pending.Before)return Set("conflict",version,"The interrupted update encountered an independently changed file: "+pending.Path);
                File.Delete(journal);
            }
            var changes = new List<(PackageFile File, string Source, string Target)>();
            var legacy = Under(game, LegacyPath);
            var oldConfig = Under(game, "BepInEx/config/mirthwoodai.companion.cfg");
            var newConfig = Under(game, "BepInEx/config/mirthwood.echoes.cfg");
            bool renameConfig = File.Exists(oldConfig);
            if (renameConfig && File.Exists(newConfig) && Hash(oldConfig) != Hash(newConfig))
                return Set("conflict", version, "Both plugin configuration files exist with different settings.");
            var legacyHash = File.Exists(legacy) ? Hash(legacy) : null;
            if (legacyHash != null && legacyHash != owned.GetValueOrDefault(LegacyPath) && !IsOriginalRelease(legacyHash))
                return Set("conflict", version, "The previous module has been independently changed: " + LegacyPath);
            bool adopt = false;
            foreach (var file in package.Files)
            {
                if (!Allowed(file)) throw new InvalidDataException("Unsupported package file " + file.Path);
                var source = Under(bundle, file.Path);
                var target = Under(game, file.Path);
                if (file.Kind == "default" && File.Exists(target)) continue;
                var current = File.Exists(target) ? InstalledHash(target,file.Kind=="voices",background) : null;
                if (current == file.Sha256)
                {
                    if(file.Kind!="default" && owned.GetValueOrDefault(file.Path)!=current)
                    {if(Hash(source)!=current)throw new InvalidDataException("Package checksum mismatch for "+file.Path);owned[file.Path]=current;adopt=true;}
                    continue;
                }
                if (current != null && (!owned.TryGetValue(file.Path, out var previous) || previous != current))
                    return Set("conflict", version, "An independently changed file needs review: " + file.Path);
                if (Hash(source) != file.Sha256) throw new InvalidDataException("Package checksum mismatch for " + file.Path);
                changes.Add((file, source, target));
            }
            if (legacyHash == null && owned.Remove(LegacyPath)) adopt = true;
            if(adopt)AtomicWrite(ledger,JsonSerializer.SerializeToUtf8Bytes(owned,Json));
            if (changes.Count == 0 && legacyHash == null && !renameConfig) return Set("ready", version, "The installed game module matches the server.");
            await using var guard = await GameGuard.TryEnter(game, ct);
            if (guard == null) return Set("waiting_for_game_exit", version, "The update is queued until Mirthwood closes.");
            if (renameConfig && File.Exists(oldConfig))
            {
                if (!File.Exists(newConfig)) File.Move(oldConfig, newConfig);
                else if (Hash(oldConfig) == Hash(newConfig)) File.Delete(oldConfig);
                else return Set("conflict", version, "The plugin configuration changed during the rename.");
            }
            if (legacyHash != null)
            {
                if (!File.Exists(legacy) || Hash(legacy) != legacyHash)
                    return Set("conflict", version, "The previous module changed during the rename.");
                var target = Under(game, ModulePath);
                if (!File.Exists(target))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    // Persist the accepted bytes before the atomic rename. Either filename can be
                    // recovered after a restart, and BepInEx sees only one DLL during this upgrade.
                    owned[ModulePath] = legacyHash;
                    AtomicWrite(ledger, JsonSerializer.SerializeToUtf8Bytes(owned, Json));
                    File.Move(legacy, target);
                }
            }
            var priorLedger=new Dictionary<string,string>(owned);
            var applied=new List<(string Target,string Installed,byte[]? Previous)>();
            try
            {
              foreach (var item in changes.OrderBy(x => x.File.Path == "winhttp.dll"))
              {
                ct.ThrowIfCancellationRequested();
                Directory.CreateDirectory(Path.GetDirectoryName(item.Target)!);
                string temp = item.Target + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    File.Copy(item.Source, temp);
                    if (Hash(temp) != item.File.Sha256) throw new IOException("Staged checksum mismatch.");
                    var previous = owned.GetValueOrDefault(item.File.Path);
                    var actual = File.Exists(item.Target) ? Hash(item.Target) : null;
                    if (actual != null && actual != previous && actual != item.File.Sha256)
                        throw new IOException("The file changed during installation: " + item.File.Path);
                    var before=actual==null?null:File.ReadAllBytes(item.Target);
                    AtomicWrite(journal,JsonSerializer.SerializeToUtf8Bytes(new Pending(item.File.Path,actual,item.File.Sha256),Json));
                    File.Move(temp, item.Target, actual != null);
                    applied.Add((item.Target,item.File.Sha256,before));
                    owned[item.File.Path] = item.File.Sha256;
                    AtomicWrite(ledger, JsonSerializer.SerializeToUtf8Bytes(owned, Json));
                    if (Hash(item.Target) != item.File.Sha256) throw new IOException("Installed checksum mismatch.");
                    File.Delete(journal);
                }
                finally { File.Delete(temp); }
              }
            }
            catch
            {
                foreach(var item in applied.AsEnumerable().Reverse())
                {
                    if(!File.Exists(item.Target) || Hash(item.Target)!=item.Installed)
                        throw new IOException("An installation target changed during recovery. Review " + item.Target);
                    if(item.Previous==null)File.Delete(item.Target);else AtomicWrite(item.Target,item.Previous);
                }
                AtomicWrite(ledger,JsonSerializer.SerializeToUtf8Bytes(priorLedger,Json));
                File.Delete(journal);
                throw;
            }
            if (legacyHash != null)
            {
                var bundled = package.Files.Single(f => f.Kind == "module");
                if (Hash(Under(game, ModulePath)) != bundled.Sha256)
                    throw new IOException("The renamed module did not pass verification.");
                if (File.Exists(legacy))
                {
                    if (Hash(legacy) != legacyHash) return Set("conflict", version, "The previous module changed during the rename.");
                    File.Delete(legacy);
                }
                var folder = Path.GetDirectoryName(legacy)!;
                if (Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any())
                    Directory.Delete(folder, recursive: false);
            }
            if (owned.Remove(LegacyPath))
                AtomicWrite(ledger, JsonSerializer.SerializeToUtf8Bytes(owned, Json));
            return Set("ready", version, "Installed and verified the server's bundled game files.");
        }
        catch (Exception e) when (!ct.IsCancellationRequested) { return Set("error", version, e.GetBaseException().Message); }
        finally { gate.Release(); }
    }

    Installation Set(string state, string version, string detail) => status = new(state, version, detail, DateTime.UtcNow);
    static bool Allowed(PackageFile f) => !f.Path.Contains('\\') && f.Kind switch {
        "module" => f.Path == ModulePath,
        "voices" => f.Path == "BepInEx/plugins/MirthwoodEchoes/Mirthwood-Echoes-Voices.zip" ||
            System.Text.RegularExpressions.Regex.IsMatch(f.Path,@"\ABepInEx/plugins/MirthwoodEchoes/Voices/(?:narrator|(?:us|gb|es|de|ru|cn)-[mf]0[1-6])\.wav\z"),
        "loader" => f.Path is "winhttp.dll" or ".doorstop_version" ||
            f.Path.StartsWith("BepInEx/core/", StringComparison.Ordinal) && Path.GetExtension(f.Path) == ".dll" && f.Path.Count(c => c == '/') == 2,
        "default" => f.Path is "doorstop_config.ini" or "BepInEx/config/BepInEx.cfg",
        _ => false
    };

    static string Under(string root, string relative)
    {
        var result = root;
        foreach (var segment in relative.Split('/'))
        {
            if (segment is "" or "." or ".." || segment.Contains(':')) throw new InvalidDataException("Invalid package path.");
            result = Path.Combine(result, segment);
            if ((File.Exists(result) || Directory.Exists(result)) && File.GetAttributes(result).HasFlag(FileAttributes.ReparsePoint))
                throw new IOException("A managed path contains a filesystem link: " + relative);
        }
        return result;
    }
    static string Hash(string path) { using var file = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(file)); }
    internal static void AtomicWrite(string path, byte[] bytes)
    {
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllBytes(temp, bytes); File.Move(temp, path, true); }
        finally { File.Delete(temp); }
    }
}

// Hold a Windows file handle throughout replacement, including the interval before Steam starts the process.
internal sealed class GameGuard(FileStream? handle, Process? bridge) : IAsyncDisposable
{
    public static async Task<GameGuard?> TryEnter(string game, CancellationToken ct)
    {
        if (OperatingSystem.IsWindows())
        {
            var processes = Process.GetProcessesByName("Mirthwood");
            foreach (var process in processes) process.Dispose();
            if (processes.Length != 0) return null;
            try { return new GameGuard(File.Open(Path.Combine(game, "Mirthwood.exe"), FileMode.Open, FileAccess.Read, FileShare.None), null); }
            catch (IOException) { return null; }
        }
        if (game.Length < 8 || !game.StartsWith("/mnt/") || !char.IsAsciiLetter(game[5]) || game[6] != '/')
            throw new IOException("A Windows Steam library mounted in WSL is required.");
        var winExe = char.ToUpperInvariant(game[5]) + @":\" + game[7..].Replace('/', '\\') + @"\Mirthwood.exe";
        var encodedPath = Convert.ToBase64String(Encoding.UTF8.GetBytes(winExe));
        var script = "$ErrorActionPreference='Stop';$p=[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('" + encodedPath + "'));" +
            "if(Get-Process -Name Mirthwood -ErrorAction SilentlyContinue){[Console]::WriteLine('busy');exit};" +
            "try{$f=[IO.File]::Open($p,'Open','Read','None')}catch [IO.IOException]{[Console]::WriteLine('busy');exit};" +
            "try{[Console]::WriteLine('ready');[Console]::Out.Flush();[void][Console]::ReadLine()}finally{$f.Dispose()}";
        var start = new ProcessStartInfo("/mnt/c/Windows/System32/WindowsPowerShell/v1.0/powershell.exe") {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false
        };
        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-NoLogo", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) }) start.ArgumentList.Add(arg);
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WSL_INTEROP"))) start.Environment["WSL_INTEROP"] = "/run/WSL/1_interop";
        var proc = Process.Start(start) ?? throw new IOException("Could not start the Windows game check.");
        var guard = new GameGuard(null, proc);
        try
        {
            string? result = (await proc.StandardOutput.ReadLineAsync(ct).AsTask().WaitAsync(TimeSpan.FromSeconds(12), ct))?.Trim();
            if (result == "ready") return guard;
            if (result != "busy") throw new IOException("Windows could not confirm whether Mirthwood is closed.");
            await guard.DisposeAsync();
            return null;
        }
        catch { await guard.DisposeAsync(); throw; }
    }

    public async ValueTask DisposeAsync()
    {
        handle?.Dispose();
        if (bridge == null) return;
        try
        {
            bridge.StandardInput.Close();
            await bridge.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3));
        }
        catch (TimeoutException) { bridge.Kill(true); }
        finally { bridge.Dispose(); }
    }
}
