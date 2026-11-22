using System.Text.Json;
using System.Text.Json.Nodes;

// A snapshot describes one speaker at one accepted moment. Nothing here updates their journal.
internal static class SpeakerWorld
{
    // Match event words a listener actually heard, even when their own part of the map has
    // no notice or sighting. This vocabulary is used in the query and never sent as news.
    internal const string NewsSubjects=@"\m(joust|merchant|market|trade ship|festival of blossoms|summer solstice|all hallows eve|yuletide|town raid|bandit raid|vermin plague|dust storm|werewolf|full moon)\M";
    internal const string Instructions = """
        worldAwareness is what this resident can know at this moment. Answer the player's last point first. Follow an ongoing personal subject, especially grief or a disclosure. When invited, one fitting local detail is enough. Once mentioned, let it rest until asked, a detail changes, or there is a fresh angle. Answer direct follow-ups in your own words.
        Use place for where you are now (town in the profile is home). When asked what you see or saw at a gathering, lead with your own firsthand observation in today or eventWitnesses and its time. Use the actual detail in seen when available. Carry that answer through every reply alternative, including a farewell. "In sight now" describes what you can see. "Earlier today" describes what you saw then. If that was your last sighting, leave its present state open. Landmarks give directions when someone asks the way.
        Answer a question about company separately. nearbyPeople identifies people near you now. Put someone at a particular event only if your own observation places them there. If the record identifies only the event, describe what you saw and leave its other attendees unknown. A visible person has disclosed nothing. interruptedActivity was what you were doing before this conversation. Its outcome is unknown.
        Weather, company, place and event status can change between turns. When supplied, previousObservation describes your earlier private visit. Use worldAwareness for the present. Describe a change when both observations support it. From indoors you can remember the rain you saw outside and say whether you can see it now. Retract an earlier statement only if evidence shows it was mistaken at the time.
        heardToday identifies named people you heard discussing an event. With a quote, keep the speaker's actual claim, question or uncertainty. With only a subject, you know they mentioned it. Neither proves the event happened. Discuss dates naturally by season and day.
        When asked what's on, cover the relevant today entries. A public notice gives an advertised date. Describe a posted gathering as planned until a start is witnessed or announced. A firsthand entry can also be advertised. Use the observation for what happened and the notice for the published date. An ended event is over. A cancellation needs confirmation. nextNotice is the nearest advertised event overall. For a question about the next festival or joust, use its entry in nextByKind. previousNotice gives a past posted date. Keep unannounced timing unknown. Attribute heardSpeech and dated accounts to their speakers. A plan or promise is a reason to ask whether it happened. Keep personal follow-ups for privacy when others are nearby.
        """;

    static string? Label(JsonNode? node,int limit=120) =>
        node is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) &&
        s.Length<=limit && !s.Any(char.IsControl) ? s.Trim() : null;

    static int? Number(JsonNode? n,int min,int max) =>
        n is JsonValue v && v.TryGetValue<int>(out var i) && i>=min && i<=max ? i : null;

    static void AddText(JsonObject target,JsonObject? source,string key,int limit=120)
    {
        if(Label(source?[key],limit) is {} text)target[key]=text;
    }

    static void AddDate(JsonObject target,JsonObject? source)
    {
        AddText(target,source,"season",64);
        if(Number(source?["dayOfSeason"],1,366) is int date)target["dayOfSeason"]=date;
        if(Number(source?["year"],0,10000) is int year)target["year"]=year;
    }

    static JsonObject? Event(JsonNode? value,bool next)
    {
        if(value is not JsonObject item || Label(item["title"]) is not {} title)return null;
        string? status=Label(item["status"]),source=Label(item["source"]);
        if(status is not ("posted" or "active" or "ended" or "cancelled") ||
           source is not ("public notice" or "public announcement" or "firsthand"))return null;
        if((status=="posted")!=(source=="public notice") || next && status!="posted")return null;
        var result=new JsonObject{["title"]=title,["status"]=status,["source"]=source};
        string when=item.S("when");
        if((status!="posted" && when=="earlier today") || (status=="active" && source=="firsthand" && when=="in sight now"))
            result["when"]=when;
        if(source=="firsthand" && status is ("active" or "ended") && result["when"]!=null)AddText(result,item,"seen");
        AddText(result,item,"place");
        AddDate(result,item);
        if(source=="firsthand" && result["season"]!=null && result["dayOfSeason"]!=null &&
           item["advertised"] is JsonValue notice && notice.TryGetValue<bool>(out var advertised) && advertised)result["advertised"]=true;
        return status=="posted" && (result["season"]==null || result["dayOfSeason"]==null) ? null : result;
    }

    static JsonArray Names(JsonNode? node,int maximum)
    {
        var names=new JsonArray();
        if(node is JsonArray list)
            foreach(string name in list.Select(x=>Label(x)).OfType<string>().Distinct().Take(maximum))names.Add(name);
        return names;
    }

    internal static JsonObject? Read(JsonObject input,JsonObject actor,string campaign)
    {
        if(input["worldAwareness"] is not JsonObject data || data["world"] is not JsonObject world ||
           Number(data["day"],0,1_000_000) is not int day || Number(world["day"],0,1_000_000)!=day ||
           Number(world["slot"],0,int.MaxValue) is not int slot || Label(world["created"],48) is not {} created ||
           Label(world["player"],160) is not {} player || Label(data["owner"],200)!=actor.S("owner") ||
           Label(data["nativeId"],200)!=actor.S("nativeId") || actor["retiredLoad"]?.GetValue<bool>()==true ||
           !Guid.TryParseExact(Label(world["memorySession"]),"D",out var load) || load==Guid.Empty)
            return null;
        // A private outbox may make its first successful request after midnight. Its ID
        // and binding still identify the words and scene the player originally accepted.
        if(actor.I("lastDayOnLoad")>day && (input["context"] is not JsonObject || input["text"]==null ||
           !Guid.TryParseExact(data.S("acceptedTurn"),"D",out var accepted) || accepted==Guid.Empty ||
           accepted.ToString()!=input.S("id")))return null;
        try{if(Saves.Campaign(slot,created,player)!=campaign)return null;}
        catch(InvalidDataException){return null;}
        var head=world["cueHead"];
        if(!world.ContainsKey("cueHead") || head!=null && (!Guid.TryParseExact(Label(head),"D",out var h)||h==Guid.Empty))return null;
        if(input["context"] is JsonObject binding &&
           (binding.S("session")!=world.S("memorySession")||!JsonNode.DeepEquals(binding["head"],head)))return null;
        if(input.ContainsKey("head") && !JsonNode.DeepEquals(input["head"],head))return null;
        if(input["target"] is JsonObject target &&
           (target.S("owner")!=actor.S("owner")||target.S("nativeId")!=actor.S("nativeId")||
            !JsonNode.DeepEquals(target["world"],world)))return null;

        var result=new JsonObject{["day"]=day};
        var calendar=new JsonObject();
        var nativeDate=data["calendar"] as JsonObject;
        AddDate(calendar,nativeDate);
        if(Number(nativeDate?["daysInSeason"],1,366) is int length && length>=calendar.I("dayOfSeason"))
            calendar["daysInSeason"]=length;
        AddText(calendar,nativeDate,"timeOfDay",64);
        if(calendar.Count>0)result["calendar"]=calendar;
        var place=new JsonObject();
        var local=data["place"] as JsonObject;
        foreach(string field in new[]{"name","region"})AddText(place,local,field);
        if(local?["interior"] is JsonValue interior && interior.TryGetValue<bool>(out var inside))place["interior"]=inside;
        if(place.Count>0)result["place"]=place;
        if(data["bystandersPresent"] is JsonValue nearby && nearby.TryGetValue<bool>(out var present) && present)result["bystandersPresent"]=true;
        AddText(result,data,"weather");
        AddText(result,data,"interruptedActivity",220);
        var sights=Names(data["sights"],12);
        var people=Names(data["nearbyPeople"],12);
        if(people.Count>0)result["nearbyPeople"]=people;
        var landmarks=new JsonArray();
        if(data["landmarks"] is JsonArray points)
            foreach(var point in points.OfType<JsonObject>().Take(12))
                if(Label(point["name"]) is {} name && Label(point["direction"],80) is {} direction)
                    landmarks.Add(new JsonObject{["name"]=name,["direction"]=direction});
        if(landmarks.Count>0)result["landmarks"]=landmarks;
        var today=new JsonArray();
        if(data["today"] is JsonArray events){
            foreach(var e in events.Select(x=>Event(x,false)).OfType<JsonObject>()){
                if((e["season"]!=null || e["dayOfSeason"]!=null || e["year"]!=null) && !SameDate(e,calendar))continue;
                today.Add(e);
                if(e.S("when")=="in sight now" && Label(e["seen"]) is {} seen && !sights.Any(s=>s?.ToString()==seen))sights.Add(seen);
            }
            result["today"]=today;
        }
        if(sights.Count>0)result["sights"]=sights;
        foreach(string field in new[]{"nextNotice","previousNotice"}){
            if(data[field] is not JsonObject entry || Event(entry,true) is not {} posted)continue;
            var order=DateOrder(entry,nativeDate);
            if(field=="nextNotice"?order>0:order<0)result[field]=posted;
        }
        var nextByKind=new JsonObject();
        foreach(string kind in new[]{"festival","joust"})
            if(data["nextByKind"] is JsonObject boards && boards[kind] is JsonObject entry &&
               Event(entry,true) is {} posted && DateOrder(entry,nativeDate)>0)nextByKind[kind]=posted;
        if(nextByKind.Count>0)result["nextByKind"]=nextByKind;
        // The HUD displays season and day. Keep the native year for the comparisons
        // above and remove it from every date the resident receives.
        foreach(var date in today.Concat(nextByKind.Select(pair=>pair.Value)).Append(calendar).Append(result["nextNotice"]).Append(result["previousNotice"]).OfType<JsonObject>())
            date.Remove("year");
        return result;
    }

    static bool SameDate(JsonObject entry,JsonObject calendar) =>
        entry.S("season")==calendar.S("season") && entry.I("dayOfSeason")==calendar.I("dayOfSeason") &&
        (entry["year"]==null || entry.I("year")==calendar.I("year"));

    static int? DateOrder(JsonObject entry,JsonObject? calendar)
    {
        if(calendar==null || Number(calendar["year"],0,10000) is not int year ||
           Number(entry["year"],0,10000) is not int advertisedYear ||
           Number(calendar["dayOfSeason"],1,366) is not int day ||
           Number(calendar["daysInSeason"],1,366) is int length && (day>length || entry.I("dayOfSeason")>length))return null;
        if(advertisedYear!=year)return advertisedYear.CompareTo(year);
        if(entry.S("season")==calendar.S("season"))return entry.I("dayOfSeason").CompareTo(day);
        int? current=Number(calendar["seasonIndex"],0,3),other=Number(entry["seasonIndex"],0,3);
        return other.HasValue && current.HasValue ? other.Value.CompareTo(current.Value) : null;
    }
}

public sealed record DialogueDispatch(string Mode,string Turn,string Campaign,string Actor,string NativeId,int Day,JsonObject? Awareness)
{
    internal string Guidance=>Awareness==null?"":"\n"+SpeakerWorld.Instructions;
    internal void Capture(Database db,string provider,string model,string system,string player)
    {
        if(db.HarnessRun is not {} run || Environment.GetEnvironmentVariable("MIRTHWOOD_ECHOES_CAPTURE_AWARENESS")!="1")return;
        Console.WriteLine("Echoes world awareness "+JsonSerializer.Serialize(new {
            run,mode=Mode,turn=Turn,campaign=Campaign,actor=Actor,nativeId=NativeId,day=Day,provider,model,
            awareness=Awareness,system,user=player,systemChars=system.Length,userChars=player.Length
        }));
    }
}

public sealed partial class Conversation
{
    // Generated remarks and group lines have no private Talk outbox row. Keep the first
    // response on this load so losing the HTTP response cannot produce new words or another draw.
    async Task<JsonObject> ResidentLine(JsonObject input,string mode,CancellationToken ct,Func<CancellationToken,Task<JsonObject>> generate)
    {
        if(input["id"]==null)return await generate(ct);
        Guid id=input.Id();
        var world=(input["worldAwareness"] as JsonObject)?["world"] as JsonObject;
        if(world==null && mode=="address")world=input["target"]?["world"] as JsonObject;
        var request=(JsonObject)input.DeepClone();
        request.Remove("worldAwareness");
        var binding=new JsonArray(mode,request,world.S("memorySession"));
        string campaign=mode=="group"?input.Required("campaign",64):World(input["target"]?["world"]);
        string? load=Guid.TryParse(world.S("memorySession"),out var session)?session.ToString():null;
        var args=new (string,object?)[]{("id",id),("binding",binding.ToJsonString()),("c",campaign),("load",load)};
        const string digest="sha256(convert_to(@binding::jsonb::text,'UTF8'))";
        const string current="""
            NOT EXISTS(SELECT FROM cue_events WHERE campaign=@c AND id=@id AND dialogue_expired)
            AND NOT EXISTS(SELECT FROM dialogue_clock WHERE campaign=@c AND @load::text=ANY(retired_loads))
            """;
        int won=await db.Run("INSERT INTO resident_reply_cache(id,campaign,load_id,binding) SELECT @id,@c,@load,"+digest+
            " WHERE "+current+" ON CONFLICT DO NOTHING",args);
        JsonObject? response;
        if(won==0){
            var existing=await db.One("SELECT jsonb_build_object('matches',binding="+digest+
                ",'reply',reply)::text FROM resident_reply_cache WHERE id=@id AND campaign=@c AND "+current,args);
            if(existing==null)throw new UserError("This speaker turn has retired. Start a new turn.",409);
            if(existing["matches"]?.GetValue<bool>()!=true)throw new UserError("That request belongs to a different speaker turn.",409);
            response=existing["reply"] as JsonObject??throw new UserError("The resident is preparing this line.",425);
        }else{
            // The accepted draw belongs to the turn. A lost HTTP connection releases its
            // waiter while the original provider request finishes and saves its answer.
            response=await Complete().WaitAsync(ct);
        }
        if(response["error"]!=null)throw new UserError(response.S("error"),response.I("status"));
        return response;

        async Task<JsonObject> Complete()
        {
            JsonObject result;
            try{result=await generate(CancellationToken.None);}
            catch(UserError e) when(e.Status==425){
                // A preceding group cue has not reached the server; no draw has started.
                await db.Run("DELETE FROM resident_reply_cache WHERE id=@id AND reply IS NULL",("id",id));
                return new JsonObject{["error"]=e.Message,["status"]=e.Status};
            }
            catch(Exception e){
                result=new JsonObject{["error"]=e is UserError?e.Message:"The resident could not finish this line.",
                    ["status"]=e is UserError failure?failure.Status:502};
            }
            await db.Run("UPDATE resident_reply_cache SET reply=@reply::jsonb WHERE id=@id",("id",id),("reply",result.ToJsonString()));
            return result;
        }
    }

    async Task<(JsonObject Actor,DialogueDispatch Dispatch)> Speaker(JsonObject input,string campaign,string actorKey,string mode)
    {
        var world=(input["worldAwareness"] as JsonObject)?["world"] as JsonObject;
        var saved=await journal.Actor(campaign,actorKey,world.S("memorySession"));
        var awareness=SpeakerWorld.Read(input,saved,campaign);
        var (actor,day)=DescribeSpeaker(saved,campaign,awareness?.I("day"));
        return (actor,new(mode,input.S("id"),campaign,actorKey,saved.S("nativeId"),day,awareness));
    }
}
