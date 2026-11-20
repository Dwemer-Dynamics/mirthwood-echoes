using System.Diagnostics;
using System.Text;

internal sealed class WindowSession : IDisposable
{
    readonly Process child;
    readonly TaskCompletionSource ready=new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly CancellationTokenSource ended=new();
    readonly StringBuilder errors=new();
    bool inputClosed;
    public bool Ending=>inputClosed;
    public Task Completion {get;}

    public WindowSession(string distro,Action<WindowSession,string> receive)
    {
        var info=new ProcessStartInfo(Path.Combine(Environment.SystemDirectory,"wsl.exe")){
            UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,
            RedirectStandardOutput=true,RedirectStandardError=true,StandardOutputEncoding=Encoding.UTF8,StandardErrorEncoding=Encoding.UTF8};
        foreach(string arg in new[]{"-d",distro,"-u","root","--exec","/usr/bin/systemd-run","--quiet","--wait","--pipe","--collect",
            "--service-type=exec","--unit=mirthwood-echoes-console.service","--property=User=mirthwood_echoes_console",
            "--property=EnvironmentFile=/etc/mirthwood-echoes.env","--property=TimeoutStopSec=10s",
            "--setenv=MIRTHWOOD_ECHOES_APP=/opt/mirthwood-echoes/current","/usr/bin/python3","-u","-B",
            "/opt/mirthwood-echoes/current/web-control.py","--desktop"})info.ArgumentList.Add(arg);
        child=new Process{StartInfo=info};
        child.OutputDataReceived+=(_,e)=>{
            if(e.Data=="ready")ready.TrySetResult();
            else if(e.Data!=null)receive(this,e.Data);
        };
        child.ErrorDataReceived+=(_,e)=>{if(e.Data!=null)lock(errors){if(errors.Length<2000)errors.AppendLine(e.Data);}};
        child.Start();child.BeginOutputReadLine();child.BeginErrorReadLine();
        Completion=child.WaitForExitAsync();
    }
    public string Error {get{lock(errors)return errors.ToString().Trim();}}
    public async Task WaitForPage()
    {
        if(await Task.WhenAny(ready.Task,Completion,Task.Delay(TimeSpan.FromSeconds(40)))!=ready.Task)
            throw new IOException(Error.Length>0?Error:"The local Web UI did not finish starting.");
        // WSL signals that it has bound its Linux socket before Windows begins forwarding it.
        using var http=new HttpClient(new HttpClientHandler{UseProxy=false}){Timeout=TimeSpan.FromSeconds(1)};
        for(int i=0;i<45&&!Ending&&!Completion.IsCompleted;i++){
            try{
                using var response=await http.GetAsync("http://127.0.0.1:17841/api/control/game");
                if(response.IsSuccessStatusCode)return;
            }catch(Exception e) when(e is HttpRequestException or TaskCanceledException){}
            await Task.Delay(200);
        }
        throw new IOException("Windows could not reach the local Web UI.");
    }
    public void Write(string line)
    {
        if(inputClosed)return;
        try{child.StandardInput.WriteLine(line);child.StandardInput.Flush();}
        catch(IOException){inputClosed=true;}
    }
    public async Task ResumeBackend()
    {
        using var http=new HttpClient(new HttpClientHandler{UseProxy=false}){Timeout=TimeSpan.FromSeconds(30)};
        using var body=new StringContent("""{"running":true}""",Encoding.UTF8,"application/json");
        using var result=await http.PostAsync("http://127.0.0.1:17841/api/control/server",body,ended.Token);
        result.EnsureSuccessStatusCode();
    }
    public void End()
    {
        if(inputClosed)return;
        inputClosed=true;ended.Cancel();
        try{child.StandardInput.Close();}catch(IOException){}
    }
    public void Dispose(){End();ended.Dispose();child.Dispose();}
}
