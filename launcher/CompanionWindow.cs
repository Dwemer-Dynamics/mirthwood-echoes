using System.Diagnostics;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

internal sealed class CompanionWindow : Form
{
    const string Address="http://127.0.0.1:17841/";
    readonly WebView2 browser=new(){Dock=DockStyle.Fill};
    readonly Label notice=new(){Dock=DockStyle.Fill,TextAlign=ContentAlignment.MiddleCenter,AutoSize=false,
        ForeColor=Color.FromArgb(51,66,48),BackColor=Color.FromArgb(240,235,216),Font=new Font("Georgia",15)};
    readonly SemaphoreSlim transition=new(1);
    readonly System.Windows.Forms.Timer pulse=new(){Interval=1500};
    WindowSession? session;
    bool initialized,ending,closed,restoreBackend,backendOn;
    bool Up=>initialized&&!ending&&Visible&&WindowState!=FormWindowState.Minimized;
    string distro="";

    public CompanionWindow()
    {
        Text=ModIdentity.Name;StartPosition=FormStartPosition.CenterScreen;ClientSize=new Size(1160,800);MinimumSize=new Size(440,420);
        Controls.Add(browser);Controls.Add(notice);ShowNotice("Opening Mirthwood: Echoes…");
        Shown+=async (_,_)=>await Initialize();
        Resize+=async (_,_)=>{
            if(!initialized||ending)return;
            if(WindowState==FormWindowState.Minimized){restoreBackend|=backendOn;session?.End();}
            await Reconcile();
        };
        FormClosing+=(_,e)=>{
            if(closed)return;
            e.Cancel=true;
            if(ending)return;
            ending=true;pulse.Stop();session?.End();ShowNotice("Closing Mirthwood: Echoes…");
            BeginInvoke(async ()=>{await Reconcile();closed=true;Close();});
        };
        pulse.Tick+=(_,_)=>{if(Up)session?.Write(GameOpen()?"playing":"idle");};
    }
    static bool GameOpen()
    {
        var games=Process.GetProcessesByName("Mirthwood");
        foreach(var game in games)game.Dispose();return games.Length>0;
    }
    void ShowNotice(string message){notice.Text=message;notice.Visible=true;notice.BringToFront();}
    async Task Initialize()
    {
        try{
            distro=File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"distro.txt")).Trim();
            var profile=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"MirthwoodEchoes","WebView");
            var env=await CoreWebView2Environment.CreateAsync(null,profile);
            await browser.EnsureCoreWebView2Async(env);
            if(ending)return;
            var core=browser.CoreWebView2;
            core.Settings.IsStatusBarEnabled=false;core.Settings.AreDevToolsEnabled=false;
            await ProviderClipboard.Attach(core,()=>Up&&ContainsFocus);
            core.NavigationStarting+=(_,e)=>{if(!e.Uri.StartsWith(Address,StringComparison.Ordinal))e.Cancel=true;};
            core.NewWindowRequested+=(_,e)=>e.Handled=true;
            core.ProcessFailed+=(_,e)=>{
                if(!ending&&e.ProcessFailedKind is CoreWebView2ProcessFailedKind.BrowserProcessExited or CoreWebView2ProcessFailedKind.RenderProcessExited or CoreWebView2ProcessFailedKind.RenderProcessUnresponsive)
                    Close();
            };
            core.NavigationCompleted+=(_,e)=>{
                if(!Up||session==null)return;
                if(!e.IsSuccess){
                    if(e.WebErrorStatus==CoreWebView2WebErrorStatus.OperationCanceled)return;
                    session.End();ShowNotice("The Web UI could not be displayed ("+e.WebErrorStatus+"). Reopen Mirthwood: Echoes to retry.");return;
                }
                notice.Visible=false;browser.Focus();
            };
            initialized=true;pulse.Start();await Reconcile();
        }catch(Exception e){if(!ending)ShowNotice("Mirthwood: Echoes could not open.\n\n"+e.Message);}
    }
    async Task Reconcile()
    {
        await transition.WaitAsync();
        try{
            if(!Up){
                if(session!=null)await StopSession();
                return;
            }
            if(session!=null){
                if(!session.Ending&&!session.Completion.IsCompleted)return;
                await StopSession();if(!Up)return;
            }
            ShowNotice("Opening Mirthwood: Echoes…");
            for(int attempt=0; ;attempt++){
                var current=new WindowSession(distro,Receive);
                session=current;current.Write(GameOpen()?"playing":"idle");
                try{await current.WaitForPage();break;}
                catch when(attempt<4&&Up&&current.Error.Contains("already exists",StringComparison.OrdinalIgnoreCase)){
                    await StopSession();await Task.Delay(500);
                }
                if(!Up)return;
            }
            if(!Up){await StopSession();return;}
            var live=session!;
            if(restoreBackend){
                await live.ResumeBackend();if(!Up){await StopSession();return;}
                restoreBackend=false;
            }
            if(browser.Source?.AbsoluteUri.StartsWith(Address,StringComparison.Ordinal)==true)browser.Reload();
            else browser.CoreWebView2.Navigate(Address);
            _=Watch(live);
        }catch(Exception e){
            if(session!=null)await StopSession();
            if(Up)ShowNotice("The Web UI could not start.\n\n"+e.Message);
        }finally{transition.Release();}
    }
    async Task Watch(WindowSession owned)
    {
        await owned.Completion;
        if(Up&&ReferenceEquals(session,owned)){
            ShowNotice("The local Web UI closed. Reopen Mirthwood: Echoes to reconnect.");
            backendOn=false;
        }
    }
    async Task StopSession()
    {
        var current=session!;session=null;backendOn=false;current.End();
        try{await current.Completion.WaitAsync(TimeSpan.FromSeconds(18));}catch(TimeoutException){}
        current.Dispose();
    }
    void Receive(WindowSession sender,string command)
    {
        if(IsDisposed||!IsHandleCreated)return;
        try{BeginInvoke(()=>{
            if(!ReferenceEquals(sender,session)||ending)return;
            if(command=="exit"){Close();return;}
            if(command=="backend-on"||command=="backend-off"){backendOn=command=="backend-on";return;}
            if(command!="play"||!Up)return;
            try{
                if(!GameOpen())using(Process.Start(new ProcessStartInfo("steam://rungameid/2272900"){UseShellExecute=true})){}
                sender.Write(GameOpen()?"playing":"dispatched");
            }catch(Exception){sender.Write("launch-failed");}
        });}catch(InvalidOperationException){}
    }
    protected override void Dispose(bool disposing)
    {
        if(disposing){pulse.Dispose();session?.End();}
        base.Dispose(disposing);
    }
}
