using System;
using System.IO;
using System.Linq;
using System.Text;
using Lowscope.Saving;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using _Mirthwood.Scripts.Demo.Chat;
using _Mirthwood.Scripts.Demo.Combat;
using _Mirthwood.Scripts.Demo.ScriptableObjects;

namespace Mirthwood.Echoes
{
    // One unresolved turn per saved resident. The disk intent survives a lost HTTP response or a game reload.
    internal sealed class TalkExchange
    {
        readonly string folder,path,receipt;
        public TalkExchange(string campaign,string actor)
        {
            if(!Key(campaign)||!Key(actor))throw new InvalidDataException("Unrecognized saved resident.");
            var storage=IsolatedGameRun.Active?.Root??Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"MirthwoodEchoes");
            folder=Path.Combine(storage,"PendingTalk");Directory.CreateDirectory(folder);
            path=Path.Combine(folder,campaign+"-"+actor+".json");receipt="mirthwood.echoes.talk."+actor;
        }
        static bool Key(string value)=>value!=null&&value.Length==32&&value.All(Uri.IsHexDigit);
        FileStream Lock()=>new FileStream(Path.Combine(folder,"write.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
        JObject Read()
        {
            if(!File.Exists(path))return null;
            var data=JObject.Parse(File.ReadAllText(path));
            if(!Guid.TryParseExact((string)data["id"],"D",out _)||string.IsNullOrWhiteSpace((string)data["text"])||
               !new[]{"new","applying","scored"}.Contains((string)data["stage"])||
               (string)data["stage"]=="scored"&&!new[]{"warm","neutral","cool","unknown"}.Contains((string)data["reaction"]))
                throw new InvalidDataException("The pending exchange could not be read.");
            return data;
        }
        void Write(JObject data)
        {
            string tmp=path+".tmp";
            using(var stream=new FileStream(tmp,FileMode.Create,FileAccess.Write,FileShare.None)){
                var bytes=Encoding.UTF8.GetBytes(data.ToString(Formatting.None));stream.Write(bytes,0,bytes.Length);stream.Flush(true);
            }
            if(File.Exists(path))File.Replace(tmp,path,null);else File.Move(tmp,path);
        }
        JObject Current()
        {
            var data=Read();
            if(data?["context"] is JObject binding && (string)binding["session"]!=NativeCues.Session && SavedResult((string)data["id"])==null){
                File.Delete(path);return null;
            }
            return data;
        }
        public JObject Pending(){using(Lock())return Current();}
        public JObject Accept(string text,float previousRun=0,JObject context=null,NPCController resident=null)
        {
            using(Lock()){
                var data=Current();if(data!=null)return data;
                data=new JObject{["id"]=Guid.NewGuid().ToString(),["text"]=text,["stage"]="new",["previousRun"]=previousRun,["context"]=context};
                if(resident)data["returnCues"]=NativeSocialTurn.ReturnCues(resident,(string)data["id"]);
                if(resident && PlayerImpression.Observe(resident,LastClothing()) is JObject impression)
                    data["playerImpression"]=impression;
                Write(data);return data;
            }
        }
        public float Acknowledge(string id)
        {
            using(Lock()){
                var data=Read();if((string)data?["id"]!=id)return 0;
                float run=(string)data["stage"]=="scored"?TalkResult.ReadRun(data["positiveRun"]):SavedResult(id)?.PositiveRun??0;
                File.Delete(path);return run;
            }
        }
        internal string SavedMarker()=>SaveMaster.GetString(receipt);
        JObject LastClothing()
        {
            string marker=SavedMarker();
            int divider=marker?.IndexOf('|')??-1;
            if(divider<0||divider+1>=marker.Length||marker[divider+1]!='{')return null;
            try{return JObject.Parse(marker.Substring(divider+1))["visibleClothing"] as JObject;}
            catch(JsonException){return null;}
        }
        internal TalkResult SavedResult(string id)
        {
            string marker=SavedMarker();
            if(string.IsNullOrEmpty(marker)||!marker.StartsWith(id+"|",StringComparison.Ordinal))return null;
            string value=marker.Substring(id.Length+1);
            // Existing 0.3 saves contain only the branch. Their native write is already spent.
            return value.StartsWith("{",StringComparison.Ordinal)?TalkResult.Read(JObject.Parse(value)):new TalkResult(value);
        }
        public TalkResult Apply(string id,string topic,NPCController npc,int minimumCostPercent=100)
        {
            using(Lock()){
                var data=Read();
                if((string)data?["id"]!=id)throw new IOException("This exchange changed. Reopen Talk.");
                if((string)data["stage"]=="scored")return TalkResult.Read(data);
                var result=SavedResult(id);
                if(result==null&&(string)data["stage"]=="applying")
                    result=new TalkResult("unknown",new JObject{["kind"]="unknown"});
                if(result==null){
                    if(minimumCostPercent<10||minimumCostPercent>100)throw new InvalidDataException("Invalid social desire cost.");
                    var player=PlayerCharacterController.Instance?.GetComponent<PlayerChat>();
                    var info=npc?.CharacterInfo;
                    if(player==null||info?.relationships==null)throw new InvalidOperationException("This resident is no longer available.");
                    var brain=npc.GetComponent<NPCChatController>();
                    var action=brain?.dnaData?.chatActions?.SingleOrDefault(a=>a!=null&&a.category==DnaData.Dna.DnaCategories.Chat&&a.name==topic);
                    bool ordinary=new[]{"Small Talk","Leisure","Intellectual","Deep","Dumb"}.Contains(topic);
                    if(!ordinary && (data["context"] as JObject)?["cards"] is JArray cards && cards.Values<string>().Contains(topic))action=InteractionHooks.Cards(npc).SingleOrDefault(card=>card.name==topic);
                    if(topic!="None"&&action==null)
                        throw new InvalidDataException("The native Talk action is unavailable.");
                    if(topic!="None"&&(info.personality?.values?.Length??0)<6)
                        throw new InvalidDataException("The resident's native social desire has not been initialized.");
                    data["stage"]="applying";Write(data);
                    float previousRun=TalkResult.ReadRun(data["previousRun"]);
                    result=topic=="None"?new TalkResult("neutral",positiveRun:previousRun):NativeSocialTurn.Play(player,brain,action,previousRun,minimumCostPercent,data["returnCues"] as JObject);
                    if(data["context"] is JObject)NativeCues.Record("talk",new JArray(),id);
                    var saved=result.Record();
                    var clothes=LastClothing()??new JObject();
                    if(data["playerImpression"]?["visibleClothing"] is JObject visible)clothes.Merge(visible);
                    if(clothes.Count>0)saved["visibleClothing"]=clothes;
                    SaveMaster.SetString(receipt,id+"|"+saved.ToString(Formatting.None));
                }
                data["stage"]="scored";data["reaction"]=result.Reaction;data["handoff"]=result.Handoff;data["positiveRun"]=result.PositiveRun;Write(data);return result;
            }
        }
    }
}
