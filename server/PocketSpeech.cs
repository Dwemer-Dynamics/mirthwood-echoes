using System.Buffers.Binary;
using System.Net.Http.Json;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Mirthwood.Echoes;

public sealed record WordCue(int Ms,int End);
public sealed record SpokenPart(string Voice,bool Narrated,int Start,int End,byte[] Audio,WordCue[] Cues,string Timing);

// Dwemer owns the audio.cpp service. Echoes owns the recordings used as voice references.
public sealed class PocketSpeech(VoiceCast cast) : IDisposable
{
    readonly HttpClient http=new(new SocketsHttpHandler {
        UseProxy=false, AllowAutoRedirect=false, ConnectTimeout=TimeSpan.FromSeconds(2)
    }) { BaseAddress=new Uri("http://127.0.0.1:8086/"), Timeout=TimeSpan.FromSeconds(30) };
    readonly SemaphoreSlim gate=new(1);
    readonly WordAlignment words=new();

    public async Task<bool> Available(CancellationToken ct)
    {
        using var limit=CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(TimeSpan.FromSeconds(2));
        try{
            var data=await http.GetFromJsonAsync<JsonObject>("v1/models",limit.Token);
            return data?["data"] is JsonArray models && models.Any(m=>m?["id"]?.ToString()=="pocket-tts");
        }catch(HttpRequestException){return false;}
        catch(OperationCanceledException) when(!ct.IsCancellationRequested){return false;}
        catch(System.Text.Json.JsonException){return false;}
    }

    public async Task<SpokenPart[]> Speak(JsonObject input,CancellationToken ct)
    {
        string voice=input.Required("voice",32), text=input.Required("text",1200);
        if(!cast.Roster().Any(v=>v.Id==voice))throw new UserError("Unknown cast voice.");
        if(!(await cast.Settings()).Enabled)throw new UserError("Voice playback is off.",409);
        await gate.WaitAsync(ct);
        try{
            var result=new List<SpokenPart>();
            foreach(var part in Sections(text)){
                var line=text[part.Start..part.End];
                string selected=part.Action?"narrator":voice;
                var reference=cast.InstalledReference(selected);
                // Dwemer audio.cpp requires a length-delimited request body.
                byte[] payload=JsonSerializer.SerializeToUtf8Bytes(new {
                    model="pocket-tts", input=line, voice_ref=reference, response_format="wav"
                });
                using var body=new ByteArrayContent(payload);
                body.Headers.ContentType=new MediaTypeHeaderValue("application/json");
                using var reply=await http.PostAsync("v1/audio/speech",body,ct);
                if(!reply.IsSuccessStatusCode)throw new UserError($"Dwemer could not speak this line (HTTP {(int)reply.StatusCode}).",503);
                byte[] wave=await reply.Content.ReadAsByteArrayAsync(ct);
                var samples=ReadSamples(wave);
                var cues=words.Align(line,samples,part.Start,Path.Combine(AppContext.BaseDirectory,"alignment"),out string timing);
                result.Add(new(selected,part.Action,part.Start,part.End,wave,cues,timing));
            }
            return result.ToArray();
        }catch(HttpRequestException){throw new UserError("Dwemer Pocket TTS is offline on port 8086.",503);}
        catch(OperationCanceledException) when(!ct.IsCancellationRequested){throw new UserError("Dwemer Pocket TTS took too long to respond.",503);}
        finally{gate.Release();}
    }

    static float[] ReadSamples(byte[] wave)
    {
        bool pcm=false;
        if(wave.Length>=44 && wave.Length<=16_000_000 && wave.AsSpan(0,4).SequenceEqual("RIFF"u8) && wave.AsSpan(8,4).SequenceEqual("WAVE"u8)){
            for(int at=12;at+8<=wave.Length;){
                var tag=wave.AsSpan(at,4); int size=BinaryPrimitives.ReadInt32LittleEndian(wave.AsSpan(at+4,4));
                if(size<0||size>wave.Length-at-8)break;
                var body=wave.AsSpan(at+8,size);
                if(tag.SequenceEqual("fmt "u8)&&size>=16)pcm=BinaryPrimitives.ReadUInt16LittleEndian(body)==1&&
                    BinaryPrimitives.ReadUInt16LittleEndian(body[2..])==1&&BinaryPrimitives.ReadInt32LittleEndian(body[4..])==24000&&
                    BinaryPrimitives.ReadUInt16LittleEndian(body[14..])==16;
                if(tag.SequenceEqual("data"u8)&&pcm&&size>=2&&size%2==0){
                    var samples=new float[size/2];
                    for(int i=0;i<samples.Length;i++)samples[i]=BinaryPrimitives.ReadInt16LittleEndian(body[(i*2)..])/32768f;
                    return samples;
                }
                at+=8+size+(size&1);
            }
        }
        throw new UserError("Dwemer returned an unreadable speech recording.",502);
    }

    static List<(int Start,int End,bool Action)> Sections(string text)
    {
        var sections=new List<(int,int,bool)>();
        void Add(int start,int end,bool action){
            if(start>=end)return;
            foreach(Match m in Regex.Matches(text[start..end],@"[^.!?\r\n]+[.!?]*")){
                int a=start+m.Index,b=a+m.Length;
                while(a<b&&char.IsWhiteSpace(text[a]))a++;
                while(b>a&&char.IsWhiteSpace(text[b-1]))b--;
                if(a<b&&text[a..b].Any(char.IsLetterOrDigit))sections.Add((a,b,action));
            }
        }
        int at=0;
        foreach(var span in ActionNotation.Find(text)){
            Add(at,span.Open,false);Add(span.Open+1,span.End,true);at=span.End+1;
        }
        Add(at,text.Length,false);return sections;
    }
    public void Dispose(){http.Dispose();words.Dispose();gate.Dispose();}
}
