using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

public sealed class NativeProfile
{
    public static readonly (string Label, string Low, string High)[] Axes =
    [
        ("Temper", "anxious", "calm"), ("Manner", "aggressive", "genial"),
        ("Decisions", "impulsive", "cautious"), ("Company", "reserved", "sociable"),
        ("Pace", "active", "passive")
    ];
    static readonly string[] Origins = ["Turien","Kelsyr","Osora","Cir","Velora"];
    static readonly string[] Classes = ["Royal","Noble","Peasant"];
    static readonly string[] Skills = ["Cultivation","Animal care","Manual labor","Woodcutting","Combat","Scavenging","Parsimony","Survivalism","Hunting","Cooking"];
    static readonly string[] Traits = ["Comedian","Prankster","Grump","Bully","Optimist","Seeker","Dullard","Casanova","Braggart","Kindly"];
    static readonly string[] TalkTopics=["Small Talk","Leisure","Intellectual","Deep","Dumb"];
    static readonly (int Until,string Name)[] Friendships=[(-35,"Hostile"),(-10,"Unfriendly"),(25,"Acquaintance"),(55,"Friend"),(90,"Buddy"),(101,"Confidant")];
    static readonly (int Until,string Name)[] Romances=[(-8,"Frigid"),(15,"Cold"),(35,"Neutral"),(60,"Sparks"),(90,"Spicy"),(101,"Love")];
    readonly JsonNode catalog = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"native-catalog.json")))!;
    readonly bool knownAssembly,knownAssets;

    public NativeProfile(RuntimePaths paths)
    {
        var data = Path.Combine(paths.Game, "Mirthwood_Data");
        knownAssembly = Matches(Path.Combine(data,"Managed","Assembly-CSharp.dll"),catalog.S("assemblySha256"));
        knownAssets = knownAssembly && Matches(Path.Combine(data,"resources.assets"),catalog.S("resourcesSha256"));
    }

    static bool Matches(string path,string expected)
    {
        try { using var file=File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(file)).Equals(expected,StringComparison.OrdinalIgnoreCase); }
        catch(IOException){return false;} catch(UnauthorizedAccessException){return false;}
    }
    static double? Number(JsonNode? n) => n is JsonValue j && j.TryGetValue<double>(out var value) && double.IsFinite(value) ? value : null;
    static string? NameOf(int id,string[] names) => id>=0 && id<names.Length?names[id]:null;
    static string[] EnumList(JsonNode? n,string[] names) => n is JsonArray list
        ? list.Select(x=>int.TryParse(x?.ToString(),out var i)?NameOf(i,names):null).OfType<string>().ToArray():[];
    static string? Text(JsonNode? val) => val is JsonValue j && j.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s)?s.Trim():null;
    static JsonNode? At(JsonNode? list,int index) => list is JsonArray a && index>=0 && index<a.Count?a[index]:null;
    public static string Words(string s) => Regex.Replace(s.Replace('-',' ').Replace('_',' '),@"(?<=[a-z])(?=[A-Z])"," ").Trim();
    public static string Lean(double value,int axis)
    {
        if(Math.Abs(value)<.15)return "Balanced";
        var word=value<0?Axes[axis].Low:Axes[axis].High;
        return Math.Abs(value)<.65 ? "Leans "+word : "Strongly "+word;
    }
    public (int Axis,int Direction)? Action(string action)
    {
        if(!knownAssets || catalog["actions"]?[action] is not JsonArray values || values.Count!=5)return null;
        var weights=values.Select(x=>Number(x)??0).ToArray();
        var axis=Array.IndexOf(weights,weights.MaxBy(Math.Abs));
        return Math.Abs(weights[axis])>=.5?(axis,Math.Sign(weights[axis])):null;
    }
    JsonObject? TopicFit(JsonArray? values)
    {
        if(!knownAssets||values==null||values.Count<5||values.Take(5).Any(x=>Number(x)==null))return null;
        var ranked=TalkTopics.Select(name=>{
            var weights=catalog["actions"]![name]!.AsArray();
            double fit=Enumerable.Range(0,5).Sum(i=>Number(values[i])!.Value*Number(weights[i])!.Value);
            return (name,fit);
        }).ToArray();
        return JsonSerializer.SerializeToNode(new {
            closer=ranked.Where(x=>x.fit>.0001).OrderByDescending(x=>x.fit).Take(2).Select(x=>x.name),
            farther=ranked.Where(x=>x.fit<-.0001).OrderBy(x=>x.fit).Take(2).Select(x=>x.name)
        },Database.Json)!.AsObject();
    }
    public static JsonObject? LiveTopics(JsonNode? node)
    {
        if(node is not JsonObject obj)return null;
        var result=new JsonObject();
        foreach(var side in new[]{"closer","farther"}){
            if(obj[side] is not JsonArray arr||arr.Count>2)return null;
            var labels=arr.Select(x=>Text(x)).ToArray();
            if(labels.Any(x=>x==null||!TalkTopics.Contains(x)))return null;
            result[side]=arr.DeepClone();
        }
        return result;
    }
    public JsonObject Describe(JsonNode actor)
    {
        var raw=actor["raw"]; var registry=raw?["registry"] as JsonArray;
        var data=(raw?["controller"]?["data"]??raw?["lastKnownController"]?["data"]) as JsonObject;
        var registryData=registry?.Select(x=>x?["data"]).OfType<JsonObject>().FirstOrDefault();
        var info=(data?["characterInfo"]??data?["characterData"]??registryData?["characterData"]) as JsonObject;
        var skills=knownAssembly?info?["relationships"]?["skillsTraits"]:null;
        var values=knownAssembly?info?["personality"]?["values"] as JsonArray:null;
        var axes=Axes.Select((axis,i)=> {
            var score=Number(At(values,i));
            return new { axis.Label,axis.Low,axis.High,score,reading=score.HasValue?Lean(score.Value,i):"Unrecorded" };
        });
        var need=Number(At(values,5));
        var colors=data?["skinColors"] as JsonArray??data?["colors"] as JsonArray;
        var outfits=(data?["skins"] as JsonArray??[]).Select((node,i)=>{
            var parts=(Text(node)??"").Split('/',StringSplitOptions.RemoveEmptyEntries);
            return parts.Length<1?null:new { part=parts.Length>1?Words(parts[^2]):"Appearance",style=Words(parts[^1]),color=Text(At(colors,i)?["uiName"]) };
        }).OfType<object>().ToArray();
        var klass=info.I("Class");
        var gifts=knownAssets?At(catalog["gifts"],klass):null;
        string? Gift(string key,string side)=>Text(At(gifts?[side], info.I(key)));
        var relations=data?["relationships"];
        var links=new List<object>();
        if(relations?["characterIDs"] is JsonArray ids){
            for(int i=0;i<ids.Count;i++){
                var r=At(relations["relationshipData"],i);
                if(Text(ids[i]) is not { } id || r==null)continue;
                var friendship=Number(r["friendship"]);var romance=Number(r["romance"]);
                links.Add(new {id,friendship,romance,happiness=Number(r["happiness"]),
                    friendshipStatus=knownAssembly?Friendships.FirstOrDefault(x=>friendship<x.Until).Name:null,
                    romanceStatus=knownAssembly?Romances.FirstOrDefault(x=>romance<x.Until).Name:null});
            }
        }
        var home=data?["homePosition"] as JsonObject;
        var hasHome=home!=null && new[]{"x","y","z"}.Any(k=>Number(home[k]) is { } v && Math.Abs(v)>.001);
        var components=(raw?["components"]??raw?["lastKnownComponents"]) as JsonArray??[];
        var inventory=components.FirstOrDefault(x=>x.S("type")=="InventoryCharacter")?["data"];
        var storySources=new List<JsonNode?>{info,data,registryData};
        foreach(var comp in components)
            if(comp.S("type") is "Biography" or "Backstory" or "CharacterBiography")storySources.Add(comp?["data"]);
        string? story=null;
        foreach(var candidate in storySources.OfType<JsonObject>()){
            foreach(var (k,v) in candidate){
                if(k.ToLowerInvariant() is "backstory" or "biography" or "backgroundstory" && Text(v) is { } text){story=text;break;}
            }
            if(story!=null)break;
        }
        var lookups=new { positiveTraits=EnumList(skills?["positiveTraits"],Traits),negativeTraits=EnumList(skills?["negativeTraits"],Traits),
            strengths=EnumList(skills?["positiveSkills"],Skills),weaknesses=EnumList(skills?["negativeSkills"],Skills) };
        return JsonSerializer.SerializeToNode(new { name=actor.S("name"),kind=actor.S("kind"),lastKnown=raw?["lastKnownController"]!=null,town=Text(actor["town"]),job=actor.S("job") is "" or "None"?null:Words(actor.S("job")),
            origin=knownAssembly?NameOf((info??registryData).I("origin"),Origins):null,socialClass=knownAssembly?NameOf(klass,Classes):null,family=Text(info?["family"]),
            renown=Number(info?["renown"]),morality=Number(info?["morality"]),metPlayer=data?["metPlayer"],isHired=data?["isHired"],
            hasHome, spouse=Text(relations?["spouse"])??Text(info?["relationships"]?["_spouse"]),axes,topicFit=TopicFit(values),
            socialNeed=need is null?null:need<-.15?"Low":need>.15?"High":"Moderate",
            skills=lookups,liked=new[]{Gift("favoriteItem","liked"),Gift("favoriteItem2","liked")}.OfType<string>().Distinct(),
            disliked=Gift("leastFavoriteItem","disliked"),outfits,links,gold=Number(inventory?["gold"]),nativeBackstory=story
        },Database.Json)!.AsObject();
    }
    public static JsonObject Grounding(JsonObject p)
    {
        var context=new JsonObject();
        foreach(var key in new[]{"name","kind","town","origin","socialClass","job","family","spouse","renown","morality","skills","liked","disliked","topicFit"})
            if(p[key]!=null)context[key]=p[key]!.DeepClone();
        context["temperament"]=JsonSerializer.SerializeToNode(p["axes"]!.AsArray().Where(x=>x?["score"]!=null)
            .Select(x=>x.S("label")+" — "+x.S("reading")));
        return context;
    }
}
