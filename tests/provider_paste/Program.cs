using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Mirthwood.Echoes.Tests;

internal static class Program
{
    [STAThread] static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        using var form=new Form{Text="Mirthwood: Echoes · clipboard test",Width=920,Height=720,ShowInTaskbar=false};
        using var view=new WebView2{Dock=DockStyle.Fill};form.Controls.Add(view);
        string folder=Path.GetFullPath(args[0]);Directory.CreateDirectory(folder);int exit=1;
        form.Shown+=async (_,_)=>{
            try{
                var env=await CoreWebView2Environment.CreateAsync(null,Path.Combine(folder,"webview"));
                await view.EnsureCoreWebView2Async(env);var core=view.CoreWebView2;
                await ProviderClipboard.Attach(core,()=>form.Visible&&form.ContainsFocus);
                var writes=0;
                core.AddWebResourceRequestedFilter("*",CoreWebView2WebResourceContext.All);
                core.WebResourceRequested+=(_,e)=>{
                    if(e.Request.Method is "PUT" or "POST"){writes++;e.Response=env.CreateWebResourceResponse(Stream.Null,405,"Read-only test","");}
                    else if(new Uri(e.Request.Uri).AbsolutePath=="/api/providers/openrouter"){
                        var sampleProfile=new{defaultModel="echoes-test-model",conversation="",memory="",hasKey=false,endpoint="https://openrouter.ai/api/v1/chat/completions"};
                        var body=new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(sampleProfile));
                        e.Response=env.CreateWebResourceResponse(body,200,"OK","Content-Type: application/json");
                    }
                };
                core.Navigate("http://127.0.0.1:17841/#providers");form.Activate();view.Focus();
                async Task Until(string expression){
                    for(int i=0;i<100;i++){if(await core.ExecuteScriptAsync(expression)=="true")return;await Task.Delay(80);}
                    throw new Exception("Clipboard assertion failed: "+expression);
                }
                await Until("!!document.querySelector('#default-model')");
                await core.ExecuteScriptAsync("const picker=document.querySelector('main select');picker.value='openrouter';picker.dispatchEvent(new Event('change'));");
                await Until("!!document.querySelector('#provider-key')");
                const string sample=PasteFixture.Sample;
                var cases=new List<string>();
                foreach(string field in new[]{"provider-key","default-model"})
                foreach(string action in new[]{"button","Ctrl+V","Shift+Insert"}){
                    await core.ExecuteScriptAsync($"{{const f=document.getElementById('{field}');f.focus();f.value='preOLDend';f.setSelectionRange(3,6);}}");
                    if(action=="button"){
                        await core.ExecuteScriptAsync($"document.querySelector('#{field} + button').click()");
                    }else{
                        var v=action=="Ctrl+V";int key=v?86:45,modifier=v?2:8;
                        foreach(string type in new[]{"keyDown","keyUp"})
                            await core.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent",JsonSerializer.Serialize(new{
                                type,key=v?"v":"Insert",code=v?"KeyV":"Insert",windowsVirtualKeyCode=key,nativeVirtualKeyCode=key,modifiers=modifier}));
                    }
                    await Until($"document.getElementById('{field}').value==='pre{sample}end'");cases.Add(field+" "+action);
                }
                await Until("document.getElementById('provider-key').type==='password'");
                if(writes!=0||PasteFixture.Reads!=6)throw new Exception("Unexpected paste reads or form submission.");
                await core.ExecuteScriptAsync("const model=document.getElementById('default-model');model.value='echoes-test-model';model.dispatchEvent(new Event('input'));model.blur();");
                using(var image=File.Create(Path.Combine(folder,"provider-fields.png")))
                    await core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png,image);
                File.WriteAllText(Path.Combine(folder,"paste.json"),JsonSerializer.Serialize(new{passed=cases,masked=true,networkWrites=writes,privateClipboardReads=PasteFixture.Reads}));
                Console.WriteLine(string.Join(Environment.NewLine,cases));exit=0;
            }catch(Exception e){Console.Error.WriteLine(e.Message);}
            finally{form.Close();}
        };
        Application.Run(form);return exit;
    }
}

namespace Mirthwood.Echoes.Tests
{
    // This project compiles the shipping WebView bridge with a private clipboard source.
    // The button and both keyboard shortcuts still cross the real native messaging boundary.
    internal static class PasteFixture
    {
        internal const string Sample="echoes-test-P7";
        internal static int Reads {get;private set;}
        internal static string GetText(TextDataFormat format)
        {
            if(format!=TextDataFormat.UnicodeText)throw new InvalidOperationException("Unexpected paste format.");
            Reads++;
            return Sample;
        }
    }
}
