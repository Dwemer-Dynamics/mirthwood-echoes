using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

// The desktop package owns the ChatGPT sign-in. Exchange only app-server messages over stdio.
internal sealed record WindowsCodex(string Executable,string DirectoryOnHost,string DirectoryOnWindows)
{
    static WindowsCodex? cached;

    internal static void SetInterop(ProcessStartInfo start)
    {
        if(OperatingSystem.IsLinux() && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WSL_INTEROP")))
            start.Environment["WSL_INTEROP"]="/run/WSL/1_interop";
    }

    static string HostPath(string windows)
    {
        if(windows.Length<3 || !char.IsAsciiLetter(windows[0]) || windows[1]!=':' || windows[2]!='\\')
            throw new UserError("Windows Codex reported an unsupported local path.",503);
        return OperatingSystem.IsWindows()?windows:"/mnt/"+char.ToLowerInvariant(windows[0])+"/"+windows[3..].Replace('\\','/');
    }

    internal static async Task<WindowsCodex> Resolve(CancellationToken ct)
    {
        var existing=Volatile.Read(ref cached);
        if(existing!=null && File.Exists(existing.Executable))return existing;
        const string script="""
            $ErrorActionPreference='Stop'
            [Console]::OutputEncoding=[Text.UTF8Encoding]::new($false)
            $pkg=Get-AppxPackage -Name OpenAI.Codex | Where-Object PublisherId -eq '2p2nqsd0c76g0' | Select-Object -First 1
            if(!$pkg){exit 2}
            $exe=Join-Path $pkg.InstallLocation 'app\resources\codex.exe'
            if(!(Test-Path -LiteralPath $exe -PathType Leaf)){exit 2}
            $dir=Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'MirthwoodEchoes\Dialogue'
            [Console]::WriteLine((@{exe=$exe;dir=$dir}|ConvertTo-Json -Compress))
            """;
        var powershell=OperatingSystem.IsWindows()?Path.Combine(Environment.SystemDirectory,@"WindowsPowerShell\v1.0\powershell.exe"):
            "/mnt/c/Windows/System32/WindowsPowerShell/v1.0/powershell.exe";
        var start=new ProcessStartInfo(powershell){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
        SetInterop(start);
        foreach(var a in new[]{"-NoProfile","-NonInteractive","-NoLogo","-EncodedCommand",Convert.ToBase64String(Encoding.Unicode.GetBytes(script))})start.ArgumentList.Add(a);
        using var proc=Process.Start(start)??throw new UserError("Could not open Windows Codex.",503);
        try {
            var output=proc.StandardOutput.ReadToEndAsync(ct);var error=proc.StandardError.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromSeconds(12),ct);
            await error;
            if(proc.ExitCode!=0)throw new UserError("The selected Windows Codex app could not be found for this Windows user.",503);
            var paths=JsonNode.Parse(await output);
            var dir=paths.Required("dir",4096);
            var result=new WindowsCodex(HostPath(paths.Required("exe",4096)),HostPath(dir),dir);
            Volatile.Write(ref cached,result);
            return result;
        } finally {if(!proc.HasExited)proc.Kill(true);}
    }
}
