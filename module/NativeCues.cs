using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Lowscope.Saving;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using _Mirthwood.Scripts.Demo.Chat;

namespace Mirthwood.Echoes
{
    internal static class NativeCues
    {
        internal const string SaveKey="mirthwood.echoes.history";
        static string session=Guid.NewGuid().ToString(),folder;
        static readonly Queue<string> pending=new Queue<string>();
        sealed class RecentInteraction
        {
            public string Event;
            public JObject Remark;
            public float EventUntil,RemarkUntil;
        }
        static readonly Dictionary<NPCController,RecentInteraction> recent=new Dictionary<NPCController,RecentInteraction>();
        internal static JArray Trace;
        static bool sending;
        internal static int Rejected {get;private set;}
        internal static int Pending=>pending.Count+(sending?1:0);
        internal static string Session=>session;
        internal static string Head=>SaveMaster.IsSlotLoaded()?SaveMaster.GetString(SaveKey):"";
        internal static void Load(){session=Guid.NewGuid().ToString();Clear();}
        internal static void Clear()=>recent.Clear();
        internal static void Clear(NPCController npc){if(npc)recent.Remove(npc);}
        internal static JObject Person(_Mirthwood.Scripts.Demo.Combat.Character n,string description,string action=null)
        {
            if(!n||n.CharacterInfo==null||string.IsNullOrEmpty(n.CharacterInfo.id)||string.IsNullOrEmpty(Plugin.Owner(n)))return null;
            return new JObject{["owner"]=Plugin.Owner(n),["nativeId"]=n.CharacterInfo.id,["description"]=Clip(description,500),["action"]=action};
        }
        static string Clip(string s,int limit)=>string.IsNullOrWhiteSpace(s)?"":s.Length<=limit?s:s.Substring(0,limit);
        internal static string ItemName(_Mirthwood.Scripts.Demo.Items.ItemDataBase item)=>item==null?"":Clip(item.ItemNameLocalized??item.name,100);
        internal static string Fact(NPCController n,string kind,string description,bool fresh=true,JObject details=null)
        {
            var person=Person(n,description);if(person==null)return null;
            if(details!=null)person["details"]=details;
            string id=Record(kind,new JArray(person));
            if(id!=null&&fresh)Recent(n,id,null,null);
            return id;
        }
        internal static string Record(string kind,JArray people,string fixedId=null)
        {
            try{
                if(folder==null||Plugin.World()==null||pending.Count>=512)return null;
                var id=fixedId??Guid.NewGuid().ToString();
                string parent=Head;
                if(parent==id)return id;
                var packet=new JObject{["id"]=id,["parent"]=string.IsNullOrEmpty(parent)?null:parent,
                    ["kind"]=kind,["people"]=people,["world"]=JObject.FromObject(Plugin.World())};
                var file=Path.Combine(folder,id+".json");
                if(File.Exists(file))return null;
                string tmp=file+".tmp";
                File.WriteAllText(tmp,packet.ToString(Formatting.None),new UTF8Encoding(false));
                File.Move(tmp,file);pending.Enqueue(file);
                SaveMaster.SetString(SaveKey,id);
                Trace?.Add(packet.DeepClone());
                return id;
            }catch(Exception e){Debug.LogWarning("Echoes could not record a native observation. "+e.GetBaseException().Message);return null;}
        }
        internal static void Recent(NPCController npc,string id,string kind,string text)
        {
            if(!npc||npc.CharacterInfo==null||string.IsNullOrEmpty(Plugin.Owner(npc)))return;
            float now=Time.time;
            foreach(var pair in recent.Where(p=>!p.Key || Math.Max(p.Value.EventUntil,p.Value.RemarkUntil)<now).ToArray())
                recent.Remove(pair.Key);
            if(!recent.TryGetValue(npc,out var cue))recent[npc]=cue=new RecentInteraction();
            if(!string.IsNullOrEmpty(id)){
                if(cue.Event!=id){cue.Remark=null;cue.RemarkUntil=0;}
                cue.Event=id;cue.EventUntil=now+30;
            }
            if(!string.IsNullOrWhiteSpace(text)){
                cue.Remark=new JObject{["owner"]=Plugin.Owner(npc),["nativeId"]=npc.CharacterInfo.id,["kind"]=kind,["text"]=Clip(text,460)};
                cue.RemarkUntil=now+30;
            }
        }
        internal static JObject Take(NPCController npc)
        {
            if(!npc)return null;
            InteractionHooks.DismissGiftRemark(npc);
            if(!recent.TryGetValue(npc,out var cue))return null;
            recent.Remove(npc);
            var value=new JObject();
            if(cue.Event!=null&&Time.time<=cue.EventUntil)value["event"]=cue.Event;
            if(cue.Remark!=null&&Time.time<=cue.RemarkUntil)value["remark"]=cue.Remark;
            return value.HasValues?value:null;
        }
        internal static JObject Binding(JObject fresh,NPCController resident=null)
        {
            if(!InteractionHooks.Compatible)return null;
            var b=fresh==null?new JObject():(JObject)fresh.DeepClone();
            if(resident)b["cards"]=new JArray(InteractionHooks.Cards(resident).Select(x=>x.name).Distinct().OrderBy(s=>s,StringComparer.Ordinal));
            b["session"]=session;string h=Head;b["head"]=string.IsNullOrEmpty(h)?null:h;return b;
        }
        internal static void Start(Plugin plugin)
        {
            if(IsolatedGameRun.Active!=null&&IsolatedGameRun.Active.TalkAddress==null)return;
            var root=IsolatedGameRun.Active?.Root??Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"MirthwoodEchoes");
            folder=Path.Combine(root,"NativeEvents");Directory.CreateDirectory(folder);
            foreach(var f in new DirectoryInfo(folder).EnumerateFiles("*.json").Where(f=>Guid.TryParseExact(Path.GetFileNameWithoutExtension(f.Name),"D",out _)).OrderBy(f=>f.CreationTimeUtc).Take(512))pending.Enqueue(f.FullName);
            plugin.StartCoroutine(Deliver());
        }
        static IEnumerator Deliver()
        {
            string address=(IsolatedGameRun.Active?.TalkAddress??"http://127.0.0.1:17841/api/")+"module/cue";
            while(true){
                if(pending.Count==0){yield return new WaitForSecondsRealtime(.15f);continue;}
                string file=pending.Dequeue();JObject packet=null;
                try{packet=JObject.Parse(File.ReadAllText(file));}catch(Exception e){Debug.LogWarning("Echoes could not read an observation. "+e.GetBaseException().Message);}
                if(packet==null)continue;
                if(packet["parent"]?.Type==JTokenType.String&&Guid.TryParseExact((string)packet["parent"],"D",out var parentId)&&
                   !File.Exists(Path.Combine(folder,parentId+".json")))packet["parentNotQueued"]=true;
                long status;sending=true;
                using(var req=NativeConversation.Post(address,packet)){
                    req.timeout=4;yield return req.SendWebRequest();status=req.responseCode;
                }
                sending=false;
                if(status==200){
                    try{File.Delete(file);}catch(IOException){pending.Enqueue(file);}
                }else if(status==400||status==409||status==422){
                    Rejected++;Debug.LogWarning("Echoes rejected a native receipt ("+status+"): "+Path.GetFileNameWithoutExtension(file));
                    try{File.Delete(file);}catch(IOException){}
                }else{
                    pending.Enqueue(file);
                    yield return new WaitForSecondsRealtime(status==425?.3f:2f);
                }
            }
        }
    }
}
