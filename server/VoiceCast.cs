using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;

public sealed record CastMember(string Id,string Name,string Accent,string Sex,string Direction);
public sealed record PlayerVoice(string? Voice=null,int Pitch=0);
public sealed record VoicePreferences(bool Enabled,PlayerVoice Default,Dictionary<string,PlayerVoice> Players)
{
    public static VoicePreferences Fresh() => new(true,new(),new());
}

public sealed class VoiceCast(Database db,RuntimePaths paths)
{
    public const string PackFile="Mirthwood-Echoes-Voices.zip";
    readonly SemaphoreSlim edits=new(1);
    const string PluginFolder="BepInEx/plugins/MirthwoodEchoes";
    static string PackPath => Path.Combine(AppContext.BaseDirectory,"bundle",PluginFolder,PackFile);
    CastMember[]? roster;
    public CastMember[] Roster()
    {
        if(roster!=null)return roster;
        using var zip=ZipFile.OpenRead(PackPath);
        using var stream=zip.GetEntry("cast.json")!.Open();
        return roster=JsonNode.Parse(stream)!["voices"]!.Deserialize<CastMember[]>(Database.Json)!;
    }
    void ValidateVoice(string id,bool includeNarrator)
    {
        if(!(includeNarrator&&id=="narrator")&&!Roster().Any(v=>v.Id==id))throw new UserError("Unknown cast voice.",404);
    }
    public byte[] Sample(string id)
    {
        ValidateVoice(id,false);
        return File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,"bundle",PluginFolder,"Voices",id+".wav"));
    }
    public string InstalledReference(string id)
    {
        ValidateVoice(id,true);
        var path=Path.Combine(paths.Game,PluginFolder,"Voices",id+".wav");
        if(!File.Exists(path))throw new UserError("Install the Echoes voice pack to use this voice in game.",503);
        return path;
    }
    public async Task<VoicePreferences> Settings() =>
        (await db.Config("voices"))?.Deserialize<VoicePreferences>(Database.Json) ?? VoicePreferences.Fresh();
    public async Task<Dictionary<string,string>> AutomaticPlayers()
    {
        var players=await db.One("""
            SELECT coalesce(jsonb_agg(to_jsonb(p)), '[]')::text FROM (
                SELECT c.id,c.created,c.player,coalesce(a.raw#>>'{controller,data,characterInfo,isMale}',
                    a.raw#>>'{controller,data,characterData,isMale}') AS male
                FROM slots s JOIN campaigns c ON c.id=s.campaign JOIN actors a ON a.campaign=c.id
                WHERE a.kind='Player' AND a.native_id=c.player AND a.present
            ) p
            """);
        var result=new Dictionary<string,string>();
        foreach(var row in players?.AsArray()??[]){
            if(!bool.TryParse(row.S("male"),out bool male))continue;
            var group=Roster().Where(v=>v.Sex==(male?"Male":"Female")).ToArray();
            int index=Mirthwood.Echoes.VoiceAssignment.Index(row.S("created"),row.S("player"),"player",row.S("player"),group.Length,out _);
            result[row.S("id")]=group[index].Id;
        }
        return result;
    }
    public async Task Change(JsonObject input,bool toggle)
    {
        await edits.WaitAsync();
        try{
            var settings=await Settings();
            if(toggle){
                if(input["enabled"] is not JsonValue val || !val.TryGetValue<bool>(out var on))throw new UserError("Choose whether voice playback is on.");
                settings=settings with {Enabled=on};
            }else{
                string scope=input.S("scope"), voice=input.S("voice");
                if(scope!="default" && (scope.Length!=32||!scope.All(char.IsAsciiHexDigit)||
                    await db.One("SELECT to_jsonb(id)::text FROM campaigns WHERE id=@id",("id",scope))==null))
                    throw new UserError("Choose an available save.");
                if(voice.Length>0 && !Roster().Any(v=>v.Id==voice))throw new UserError("Choose a voice from this cast.");
                if(input["pitch"] is not JsonValue p || !p.TryGetValue<int>(out int pitch) || pitch < -2 || pitch > 2)
                    throw new UserError("Choose one of the five pitch levels.");
                var player=new PlayerVoice(voice.Length==0?null:voice,pitch);
                if(scope=="default")settings=settings with {Default=player};
                else if(input["inherit"]?.GetValue<bool>()==true)settings.Players.Remove(scope);
                else settings.Players[scope]=player;
            }
            await db.SetConfig("voices",settings);
            Publish(settings);
        }finally{edits.Release();}
    }
    public async Task RefreshModuleSettings()
    {
        await edits.WaitAsync();
        try{Publish(await Settings());}finally{edits.Release();}
    }
    void Publish(VoicePreferences settings)
    {
        if(!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("MIRTHWOOD_ECHOES_HARNESS_ID")))return;
        var dir=Path.Combine(paths.Game,"BepInEx","config");
        if(!Directory.Exists(dir))return;
        var target=Path.Combine(dir,"mirthwood.echoes.voices.json");
        byte[] data=JsonSerializer.SerializeToUtf8Bytes(settings,Database.Json);
        if(File.Exists(target)&&File.ReadAllBytes(target).AsSpan().SequenceEqual(data))return;
        var temp=target+"."+Guid.NewGuid().ToString("N")+".tmp";
        try{File.WriteAllBytes(temp,data);File.Move(temp,target,true);}finally{File.Delete(temp);}
    }
}
