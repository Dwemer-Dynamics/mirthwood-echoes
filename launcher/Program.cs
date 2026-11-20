using System.Diagnostics;
using System.Runtime.InteropServices;

internal static class Program
{
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] static extern bool ShowWindowAsync(IntPtr window,int command);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr window);

    [STAThread]
    static void Main()
    {
        using var singleton=new Mutex(false,@"Local\Mirthwood.Echoes.Window",out bool first);
        if(!first){
            foreach(var other in Process.GetProcessesByName("Mirthwood.Echoes.Play")){
                using(other){
                    var window=other.MainWindowHandle;
                    if(window==IntPtr.Zero||other.Id==Environment.ProcessId)continue;
                    if(IsIconic(window))ShowWindowAsync(window,9);
                    SetForegroundWindow(window);break;
                }
            }
            return;
        }
        ApplicationConfiguration.Initialize();
        Application.Run(new CompanionWindow());
    }
}
