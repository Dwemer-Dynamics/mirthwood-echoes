using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

public sealed class Saves(RuntimePaths paths, Database db) : BackgroundService
{
    record Row(string Guid,string Scene,string Owner,string Component,string Data);
    record Actor(string Key,string Owner,string Native_id,string Name,string Kind,string Job,string Town,JsonObject Raw);
    readonly SemaphoreSlim gate = new(1);
    readonly Dictionary<int,string> fingerprints = new();
    public volatile string Health = "Waiting for the first save scan.";
    static readonly string[] Jobs = "None Farmer Rancher Lumberjack Miner Herbalist Hunter Actor Scout Fighter Guard Baker Bartender Blacksmith Merchant Lamplighter Bard Seamstress TaxCollector Ratcatcher Criminal Tinkerer".Split();
    static readonly string[] Towns = ["Brightoak","Crossroads","Greyshore","Ashmire"];
    public static string Key(params string[] parts) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\0",parts))))[..32].ToLowerInvariant();
    public static string Created(string raw)
    {
        if (!DateTime.TryParse(raw,CultureInfo.InvariantCulture,DateTimeStyles.AllowWhiteSpaces,out var dt)) throw new InvalidDataException("Unknown save creation date.");
        return dt.ToString("yyyy-MM-ddTHH:mm:ss",CultureInfo.InvariantCulture);
    }
    public static string Campaign(int slot,string created,string player) => Key(slot.ToString(CultureInfo.InvariantCulture),Created(created),player);
    static string Label(JsonNode? n,string key,string[] names) => n.I(key) is var i && i >= 0 && i < names.Length ? names[i] : n.S(key);
    static JsonNode? Decode(string text)
    {
        try { return JsonNode.Parse(text); } catch (JsonException) { return JsonValue.Create(text); }
    }
    static JsonObject ReadFile(string file)
    {
        using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite|FileShare.Delete);
        if (input.Length is < 5 or > 128_000_000) throw new InvalidDataException("Invalid save length.");
        int first = input.ReadByte(); input.Position = 0;
        if (first is '{' or 0xef or ' ' or '\t' or '\n' or '\r')
        {
            using var reader = new StreamReader(input,Encoding.UTF8,true,4096,true);
            try { if (JsonNode.Parse(reader.ReadToEnd()) is JsonObject root) return root; }
            catch(JsonException) { /* A legacy length prefix can itself be a JSON punctuation byte. */ }
            input.Position=0;
        }
        using var bin = new BinaryReader(input,Encoding.UTF8);
        var header = bin.ReadString();
        if (header != "binary") return JsonNode.Parse(header)?.AsObject() ?? throw new InvalidDataException("Unknown save format.");
        var meta=new JsonObject{["creationDate"]=bin.ReadString(),["gameVersion"]=bin.ReadInt32(),["timePlayed"]=bin.ReadString(),["modificationDate"]=bin.ReadString()};
        int count=bin.ReadInt32();
        if (count is < 1 or > 1_000_000) throw new InvalidDataException("Invalid component count.");
        var rows=new JsonArray();
        for (int i=0;i<count;i++) rows.Add(new JsonObject{["data"]=bin.ReadString(),["guid"]=bin.ReadString(),["scene"]=bin.ReadString()});
        return new JsonObject{["metaData"]=meta,["saveData"]=rows};
    }
    static Row? Split(JsonNode? n)
    {
        string guid=n.S("guid");
        int tail=guid.LastIndexOf('-');
        int type=tail>0 ? guid.LastIndexOf('-',tail-1) : -1;
        return type<0 ? null : new(guid,n.S("scene"),guid[..type],guid[(type+1)..tail],n.S("data"));
    }
    static JsonObject Entry(Row r) => new(){["guid"]=r.Guid,["scene"]=r.Scene,["type"]=r.Component,["data"]=Decode(r.Data)};
    async Task Import(int slot,string file)
    {
        var root=ReadFile(file);
        var meta=root["metaData"];
        var cursor=(root["saveData"] as JsonArray)?.FirstOrDefault(r=>r.S("guid")=="SVar-"+CueHistory.SaveKey && r.S("scene")=="Global");
        Guid? savedHead=Guid.TryParseExact(cursor.S("data"),"D",out var marker)?marker:null;
        if (meta.I("minReaderVersion",0)>1) throw new InvalidDataException("Save requires a newer reader.");
        var rows=(root["saveData"] as JsonArray ?? throw new InvalidDataException("Missing component list."))
            .Select(Split).OfType<Row>().ToArray();
        var siblings=rows.ToLookup(r=>(r.Scene,r.Owner));
        var actors=new List<Actor>();
        foreach (var row in rows.Where(r=>r.Component is "PlayerCharacterController" or "NPCController" or "Child"))
        {
            var controller=Entry(row);
            var data=controller["data"];
            var info=data?["characterInfo"] ?? data?["characterData"];
            if (info is not JsonObject) throw new InvalidDataException("Unreadable character record: " + row.Guid);
            var others = siblings[(row.Scene,row.Owner)].Where(r=>r!=row).Select(Entry).ToArray();
            var raw=new JsonObject{["controller"]=controller,["components"]=new JsonArray(others),["registry"]=new JsonArray()};
            string kind=row.Component=="PlayerCharacterController"?"Player":row.Component=="Child"?"Child":
                string.Equals(data.S("isDead"),"true",StringComparison.OrdinalIgnoreCase)?"Deceased NPC":
                string.Equals(data.S("isReplacementNPC"),"true",StringComparison.OrdinalIgnoreCase)?"Replacement":"NPC";
            actors.Add(new(Key(row.Scene,row.Guid),row.Owner,info.S("id"),info.S("name"),kind,Label(info,"job",Jobs),Label(info,"town",Towns),raw));
        }
        foreach (var registry in rows.Where(r=>r.Component=="NPCDeathManager"))
        {
            var data=Decode(registry.Data);
            foreach (var group in new[]{"deadNPCs","permanentDeaths","replacementNPCs","replacementDeadNpcIds"})
            {
                if (data?[group] is not JsonArray list) continue;
                for (int i=0;i<list.Count;i++)
                {
                    var item=list[i]; var info=(item as JsonObject)?["characterData"];
                    var id=info.S("id"); if(id=="") id=item.S("npcId"); if(id=="" && item is JsonValue) id=item.ToString();
                    var evidence=new JsonObject{["guid"]=registry.Guid,["scene"]=registry.Scene,["list"]=group,["data"]=item?.DeepClone()};
                    var matching=actors.Where(a=>id!="" && a.Native_id==id).ToList();
                    if(matching.Count==1) { matching[0].Raw["registry"]!.AsArray().Add(evidence); continue; }
                    var name=info.S("name"); if(name=="") name=item.S("npcName"); if(name=="") name=id==""?"Unnamed registry entry":id;
                    var key=Key(registry.Guid,group,id,id=="" || actors.Any(a=>a.Key==Key(registry.Guid,group,id,"")) ? i.ToString():"");
                    actors.Add(new(key,"",id,name,group=="replacementNPCs"?"Replacement":"Death record",Label(info,"job",Jobs),Label(info??item,"town",Towns),
                        new JsonObject{["registry"]=new JsonArray(evidence)}));
                }
            }
        }
        var player=actors.Single(a=>a.Kind=="Player");
        if(player.Native_id.Length==0) throw new InvalidDataException("Missing player identity.");
        var created=Created(meta.S("creationDate"));
        var campaign=Campaign(slot,created,player.Native_id);
        var prior=(await db.One("SELECT coalesce(jsonb_agg(jsonb_build_object('key',key,'id',native_id,'owner',owner)),'[]')::text FROM actors WHERE campaign=@c",("c",campaign)))!.AsArray();
        // Keep distinct people separate when a replacement reuses a controller. A move or death
        // can carry a journal across only when the original and new identity are both unambiguous.
        var current=actors.ToArray();
        for(int i=0;i<actors.Count;i++){
            var x=current[i];if(x.Native_id=="")continue;
            var atKey=prior.FirstOrDefault(p=>p.S("key")==x.Key);
            if(atKey!=null && atKey.S("id")==x.Native_id)continue;
            var matches=prior.Where(p=>p.S("id")==x.Native_id && (x.Owner=="" || p.S("owner")==x.Owner)).ToArray();
            bool unique=current.Count(p=>p.Native_id==x.Native_id && (x.Owner=="" || p.Owner==x.Owner))==1;
            if(unique && matches.Length==1 && !current.Any(p=>p.Key!=x.Key && p.Key==matches[0].S("key")))
                actors[i]=x with{Key=matches[0].S("key")};
            else if(atKey!=null)actors[i]=x with{Key=Key(x.Key,x.Native_id)};
        }
        var clock=rows.FirstOrDefault(r=>r.Component=="DayNightCycle" && r.Scene==player.Raw["controller"].S("scene"));
        var clockData=clock==null?null:Decode(clock.Data);
        int? day=clockData.I("yearNumber")==0 && clockData.I("dayNumber")>=0?clockData.I("dayNumber"):null;
        await using var conn=await db.Source.OpenConnectionAsync();
        await using var tx=await conn.BeginTransactionAsync();
        await using(var guard=Database.Command(conn,"SELECT pg_advisory_xact_lock(hashtextextended(@c,17))",("c",campaign)))await guard.ExecuteNonQueryAsync();
        await using var cmd=Database.Command(conn,"""
            INSERT INTO campaigns(id,slot,created,player,day,player_name,save_year,save_day_of_year,cue_saved)
              VALUES (@campaign,@slot,@created,@player,@day::int,@name,nullif(@year,-1),nullif(@date,-1),@savedHead::uuid)
              ON CONFLICT(id) DO UPDATE SET day=coalesce(excluded.day,
                CASE WHEN @year BETWEEN 1 AND 10000 AND @date BETWEEN 0 AND 10000 THEN campaigns.year_length*@year+@date END),
                player_name=excluded.player_name,save_year=excluded.save_year,save_day_of_year=excluded.save_day_of_year,cue_saved=excluded.cue_saved;
            INSERT INTO slots VALUES (@slot,@campaign) ON CONFLICT(slot) DO UPDATE SET campaign=excluded.campaign;
            UPDATE actors SET present=false WHERE campaign=@campaign AND present;
            INSERT INTO actors(campaign,key,owner,native_id,name,kind,job,town,present,raw)
            SELECT @campaign,key,owner,native_id,name,kind,job,town,true,raw FROM jsonb_to_recordset(@data::jsonb)
              AS a(key text,owner text,native_id text,name text,kind text,job text,town text,raw jsonb)
            ON CONFLICT(campaign,key) DO UPDATE SET owner=excluded.owner,native_id=excluded.native_id,name=excluded.name,
              kind=excluded.kind,job=excluded.job,town=excluded.town,present=true,
              raw=CASE WHEN excluded.raw ? 'controller' THEN excluded.raw ELSE excluded.raw||jsonb_strip_nulls(jsonb_build_object(
                'lastKnownController',coalesce(actors.raw->'controller',actors.raw->'lastKnownController'),
                'lastKnownComponents',coalesce(actors.raw->'components',actors.raw->'lastKnownComponents'))) END;
            """,("campaign",campaign),("savedHead",savedHead),("slot",slot),("created",created),("player",player.Native_id),("day",day),("name",player.Name),
              ("year",clockData.I("yearNumber")),("date",clockData.I("dayNumber")),("data",JsonSerializer.Serialize(actors,Database.Json)));
        await cmd.ExecuteNonQueryAsync();
        await using(var project=Database.Command(conn,CueHistory.ProjectResidents,("campaign",campaign)))await project.ExecuteNonQueryAsync();
        await tx.CommitAsync();
    }

    public async Task Scan()
    {
        await gate.WaitAsync();
        try
        {
            if (!Directory.Exists(paths.Saves)) { Health="Save folder unavailable."; return; }
            var slots=Directory.EnumerateFiles(paths.Saves,"Slot*.savegame*")
                .Select(f=>(file:new FileInfo(f),match:Regex.Match(Path.GetFileName(f),@"^Slot(\d+)\.savegame(?:\.b)?$")))
                .Where(x=>x.match.Success).GroupBy(x=>int.Parse(x.match.Groups[1].Value)).ToList();
            var issues=new List<string>();
            foreach (var slot in slots)
            {
                var files=slot.Select(x=>x.file).OrderByDescending(f=>f.LastWriteTimeUtc).ToArray();
                var fingerprint=string.Join('|',files.Select(f=>f.Name+":"+f.Length+":"+f.LastWriteTimeUtc.Ticks));
                if(fingerprints.GetValueOrDefault(slot.Key)==fingerprint) continue;
                bool imported=false;
                foreach(var f in files)
                {
                    try { await Import(slot.Key,f.FullName); imported=true; break; }
                    catch(Exception e) when(e is not Npgsql.NpgsqlException) { issues.Add(f.Name+" — "+Fields.Clip(e.Message,170)); }
                }
                if(imported && !issues.Any(s=>s.StartsWith("Slot"+slot.Key+"."))) fingerprints[slot.Key]=fingerprint;
            }
            var active=slots.Select(s=>s.Key).ToArray();
            await db.Run("DELETE FROM slots WHERE NOT (slot=ANY(@active))",("active",active));
            foreach(var absent in fingerprints.Keys.Except(active).ToArray()) fingerprints.Remove(absent);
            Health=issues.Count==0?$"Read {slots.Count} save slot(s).":string.Join(" · ",issues);
        }
        catch(Exception e) { Health="Save scan failed: "+Fields.Clip(e.Message,200); }
        finally {gate.Release();}
    }
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while(!ct.IsCancellationRequested){await Scan();await Task.Delay(TimeSpan.FromSeconds(20),ct);}
    }
}
