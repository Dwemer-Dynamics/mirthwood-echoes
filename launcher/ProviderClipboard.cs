using System.Text.Json;
using Microsoft.Web.WebView2.Core;

internal static class ProviderClipboard
{
    public static async Task Attach(CoreWebView2 core,Func<bool> active)
    {
        const string providers="http://127.0.0.1:17841/#providers";
        await core.AddScriptToExecuteOnDocumentCreatedAsync("if(window===window.top)window.echoesProviderClipboard=true;");
        core.WebMessageReceived+=(_,message)=>{
            if(!active()||message.Source!=providers||core.Source!=providers)return;
            try{
                using var packet=JsonDocument.Parse(message.WebMessageAsJson);
                var data=packet.RootElement;
                if(data.ValueKind!=JsonValueKind.Object||!data.TryGetProperty("action",out var action)||action.GetString()!="pasteProviderField"||
                    !data.TryGetProperty("field",out var field)||field.GetString() is not ("provider-key" or "default-model")||
                    !data.TryGetProperty("ticket",out var token)||!Guid.TryParseExact(token.GetString(),"D",out var ticket))return;
                string? value=null,error=null;
                try{value=Clipboard.GetText(TextDataFormat.UnicodeText);if(value.Length>8192)value=value[..8192];}
                catch(System.Runtime.InteropServices.ExternalException){error="The clipboard is busy. Try Paste again.";}
                core.PostWebMessageAsJson(JsonSerializer.Serialize(new{ticket,text=value,error}));
            }catch(JsonException){}catch(InvalidOperationException){}
        };
    }
}
