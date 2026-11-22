using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using BepInEx;
using HarmonyLib;
using Lowscope.Saving;
using Lowscope.Saving.Components;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.SceneManagement;
using _Mirthwood.Scripts.Demo.Chat;
using _Mirthwood.Scripts.Demo.Items;
using _Mirthwood.Scripts.Demo.Menus;
using _Mirthwood.Scripts.Demo.Combat;
using _Mirthwood.Scripts.Demo.ScriptableObjects;
using _Mirthwood.Scripts.Demo.Weather;

namespace Mirthwood.Echoes
{
    [BepInPlugin(Plugin.Id, ModIdentity.Name, ModIdentity.Version)]
    public sealed class Plugin : BaseUnityPlugin
    {
        internal const string Id = "mirthwood.echoes";
        private string ModuleApi => (isolated?.PrivateDiagnostics==true?isolated.TalkAddress:"http://127.0.0.1:17841/api/")+"module/";
        private readonly Queue<string> pending = new Queue<string>();
        private readonly Harmony hooks = new Harmony(Id);
        private static Plugin instance;
        internal static bool ReplacesTalk=>instance&&instance.isActiveAndEnabled&&!NativeConversation.UsingStockChat;
        private readonly string session = Guid.NewGuid().ToString();
        private int dropped;
        private int hookCount;
        private float nextBeat;
        private NativeConversation dialogue;
        BepInEx.Configuration.ConfigEntry<BepInEx.Configuration.KeyboardShortcut> groupKey;
        private ReactionVoices reactionVoices;
        internal static ReactionVoices Voices => instance && instance.isActiveAndEnabled ? instance.reactionVoices : null;
        private string probing,completedProbe,completedPacket;
        private IsolatedGameRun isolated;
        private JArray testObservations;
        internal static void AddressPlayer(NPCController npc,string card,string cue)
        {
            if(ReplacesTalk)instance.dialogue?.OfferNativeAddress(npc,card,cue);
        }
        internal static void OfferReply(NPCController npc,string id,string kind,string line,float delay=0)
        {
            if(ReplacesTalk&&id!=null)instance.dialogue?.OfferPassingLine(npc,id,kind,line,delay);
        }
        internal static void HeardGift(NPCController npc,string id,string line)
        {
            if(ReplacesTalk)instance.dialogue?.UseSpokenGift(npc,id,line);
        }
        internal static bool QuestFarewell(NPCController npc,string id,string line,ConversationSpeech speech) =>
            ReplacesTalk&&IsolatedGameRun.Active==null&&Voices?.Enabled==true&&instance.dialogue?.OfferPassingLine(npc,id,"quest",line,0,speech,false)==true;

        private void Awake()
        {
            instance = this;
            if(IsolatedGameRun.Requested){
                try{isolated=IsolatedGameRun.Attach(hooks);}
                catch(Exception e){Logger.LogError("Isolated test setup failed: "+e.GetBaseException().Message);enabled=false;Application.Quit();return;}
                StartCoroutine(isolated.Load());
            }
            dialogue = new NativeConversation(this);
            groupKey=Config.Bind("Controls","GroupTalk",new BepInEx.Configuration.KeyboardShortcut(KeyCode.T),"Gather nearby residents for a group conversation.");
            reactionVoices = new ReactionVoices(Config.Bind("Audio","OriginalVoices",true,"Play the Echoes cast for resident and player chat reactions."),Logger);
            hooks.CreateClassProcessor(typeof(ReactionVoices.NativeSound)).Patch();
            QuestPanel.Install(this,hooks);
            NativeCues.Start(this);
            InteractionHooks.Install(hooks,Logger);
            if(InteractionHooks.Compatible){
                hooks.CreateClassProcessor(typeof(GroupGathering.Routine)).Patch();
                hooks.CreateClassProcessor(typeof(GroupGathering.Movement)).Patch();
            }
            hooks.CreateClassProcessor(typeof(DraftPause)).Patch();
            hooks.CreateClassProcessor(typeof(WaitingResident)).Patch();
            hooks.CreateClassProcessor(typeof(TalkOption)).Patch();
            hooks.CreateClassProcessor(typeof(QuickChatPages)).Patch();
            hooks.CreateClassProcessor(typeof(NativeSocialTurn)).Patch();
            hooks.CreateClassProcessor(typeof(LoadHook)).Patch();
            SceneManager.activeSceneChanged += OnSceneChanged;
            foreach (var type in new[] { typeof(SocialHook), typeof(WorkHook) })
            {
                try { hooks.CreateClassProcessor(type).Patch(); hookCount++; }
                catch (Exception error) { Logger.LogWarning(type.Name + ": " + error.GetBaseException().Message); }
            }
            StartCoroutine(SendQueue());
            Logger.LogInfo(ModIdentity.Name + " " + ModIdentity.Version + " ready. Active observation hooks: " + hookCount);
        }

        internal static string Owner(Character character)
        {
            var saveable = character.GetComponentInParent<Saveable>();
            return saveable == null ? "" : saveable.SaveIdentification;
        }

        internal static object World()
        {
            var player = PlayerCharacterController.Instance;
            if (!SaveMaster.IsSlotLoaded() || player == null || player.CharacterInfo == null || DayNightCycle.Instance == null) return null;
            return new {
                slot = IsolatedGameRun.Active?.SourceSlot ?? SaveMaster.GetActiveSlot(),
                created = SaveMaster.GetSaveCreationTime().ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture),
                player = player.CharacterInfo.id, memorySession=NativeCues.Session, cueHead=string.IsNullOrEmpty(NativeCues.Head)?null:NativeCues.Head, day = Mathf.FloorToInt(DayNightCycle.Instance.Now), yearLength = DayNightCycle.Instance.YearLength
            };
        }

        private void Update()
        {
            reactionVoices?.Collect();
            QuestPanel.Tick();
            dialogue?.GroupKeys(groupKey.Value);
            if(dialogue?.GroupActive!=true)dialogue?.TickPassingLine();
            if (Time.realtimeSinceStartup >= nextBeat)
            {
                nextBeat = Time.realtimeSinceStartup + (isolated==null?12:3);
                isolated?.CheckQuit();
                var currentWorld=World();
                if(isolated?.TalkAddress!=null && currentWorld!=null)StartCoroutine(TestClock(currentWorld));
                StartCoroutine(Post("heartbeat", JsonConvert.SerializeObject(new {
                    session, version = ModIdentity.Version, hooks = hookCount+InteractionHooks.Installed.Count, dropped=dropped+NativeCues.Rejected, world = isolated==null?currentWorld:null,
                    probeWorld = isolated!=null && _Mirthwood.Scripts.Demo.GameState.GetCurrentState(1)==_Mirthwood.Scripts.Demo.GameState.State.Gameplay?currentWorld:null,
                    isolatedRun=isolated?.Id
                }), null, OnHeartbeat));
            }
            if (dialogue.Visible&&!dialogue.GroupActive)dialogue.HandleKeys();
        }

        // NPCController uses IsChatting to stop its agent and idle its walk animation.
        [HarmonyPatch(typeof(ChatController),nameof(ChatController.IsChatting),MethodType.Getter)]
        static class WaitingResident
        {
            static void Postfix(ChatController __instance,ref bool __result)
            {
                if(!__result && (instance?.dialogue?.WaitingFor(__instance)==true||GroupGathering.Holds(__instance)))__result=true;
            }
        }

        [HarmonyPatch(typeof(PlayerChat),"GenerateOptions")]
        static class TalkOption
        {
            static void Postfix(PlayerChat __instance,ChatController ___SelectedNPC,List<IContextMenu.Option> __result)
            {
                var chat=___SelectedNPC as NPCChatController;
                var npc=chat?.Character as NPCController;
                if(!ReplacesTalk||npc==null||__result==null)return;
                int index=__result.FindIndex(o=>o.Name=="Talk"||o.Name=="Greet"||o.Name=="Jeer");
                if(index<0)return; // Authored Speak and disabled or unavailable residents retain their native options.
                __result.RemoveAll(o=>o.Name=="Talk");
                __result.Insert(index,new IContextMenu.Option("Talk",playerIndex=>{
                    if(playerIndex!=1||!ReplacesTalk||!npc||!npc.isActiveAndEnabled||!chat.chatEnabled||instance.dialogue.Visible||instance.dialogue.GroupActive||
                       !ReferenceEquals(Selected.GetValue(__instance),chat)||chat.GetDialogueData()!=null)return;
                    var world=World();string owner=Owner(npc);
                    if(world!=null&&owner!=""&&npc.CharacterInfo!=null)
                        instance.dialogue.Open(npc,new {world,owner,nativeId=npc.CharacterInfo.id});
                }));
            }
        }
        [HarmonyPatch(typeof(ContextMenuUI), nameof(ContextMenuUI.SetOptions))]
        static class QuickChatPages
        {
            // MainControls binds Interact through Interact4 on both keyboard and controller.
            const int BoundInputs = 4;
            const int ActionsPerPage = BoundInputs - 1;
            static readonly System.Reflection.MethodInfo Generate = AccessTools.Method(typeof(PlayerChat), "GenerateOptions");

            static void Prefix(ContextMenuUI __instance, ref IContextMenu.Option[] __0)
            {
                if (!ReplacesTalk || __0 == null || __0.Length <= BoundInputs) return;
                var player = PlayerCharacterController.Instance;
                var chat = player ? player.GetComponent<PlayerChat>() : null;
                if (!chat || chat.ContextMenuUI != __instance || !(Selected.GetValue(chat) is NPCChatController resident)) return;
                __0 = Page(chat, resident, __0, 0);
            }

            static IContextMenu.Option[] Page(PlayerChat chat, NPCChatController resident, IList<IContextMenu.Option> options, int start)
            {
                if (options.Count <= BoundInputs) return options.ToArray();
                if (start >= options.Count) start = 0;
                var page = options.Skip(start).Take(ActionsPerPage).ToList();
                int next = start + page.Count < options.Count ? start + page.Count : 0;
                page.Add(new IContextMenu.Option(next == 0 ? "Back" : "Next", playerIndex => {
                    if (playerIndex != 1 || !ReplacesTalk || !resident || !ReferenceEquals(Selected.GetValue(chat), resident) ||
                        !chat.ContextMenuUI.IsVisible || instance.dialogue.Visible) return;
                    // A job can finish or a greeting can expire while the menu is on screen.
                    var current = (List<IContextMenu.Option>)Generate.Invoke(chat, null);
                    chat.ContextMenuUI.SetOptions(Page(chat, resident, current, next));
                }));
                return page.ToArray();
            }
        }
        internal static readonly System.Reflection.FieldInfo Selected=AccessTools.Field(typeof(PlayerChat),"SelectedNPC");
        [HarmonyPatch(typeof(SaveMaster),nameof(SaveMaster.SyncLoad))]
        static class LoadHook {static void Prefix(){NativeCues.Load();InteractionHooks.Reset();QuestPanel.Close();instance?.dialogue?.Close();if(instance!=null)instance.nextBeat=0;}}

        private void LateUpdate(){dialogue?.Follow();}
        private void OnSceneChanged(Scene oldScene, Scene newScene) { NativeCues.Clear();InteractionHooks.Reset();QuestPanel.Close();dialogue?.Close(); }
        private void OnHeartbeat(string body)
        {
            if (probing != null || string.IsNullOrEmpty(body)) return;
            try {
                var job = JObject.Parse(body)["probe"] as JObject;
                if (job != null && Guid.TryParse((string)job["id"], out var id))
                    StartCoroutine(RunProbe(id.ToString(), (string)job["mode"]));
            } catch (JsonException error) { Logger.LogWarning(error.Message); }
        }
        IEnumerator TestClock(object world)
        {
            using(var r=NativeConversation.Post(isolated.TalkAddress+"module/heartbeat",new {session,version=ModIdentity.Version,world})){
                r.timeout=3;yield return r.SendWebRequest();
            }
        }
        private static bool OwnsHook(Type type,string method)
        {
            var original=AccessTools.Method(type,method);
            return original!=null && Harmony.GetPatchInfo(original)?.Owners.Contains(Id)==true;
        }
        private IEnumerator RunProbe(string id,string mode)
        {
            probing=id;
            try {
                if(completedProbe==id&&completedPacket!=null){
                    yield return Post("probes/"+id,completedPacket);yield break;
                }
                var report=JObject.FromObject(new {session,version=ModIdentity.Version,unity=Application.unityVersion,scene=SceneManager.GetActiveScene().name,
                    socialHook=OwnsHook(typeof(NPCChatController),"ReceiveResponse"),workHook=OwnsHook(typeof(NPCController),"AddToHiringItemList")});
                if(mode=="world"){
                    var world=World();var player=PlayerCharacterController.Instance;
                    var npcs=FindObjectsOfType<NPCController>().Where(n=>n!=null&&n.CharacterInfo!=null&&n.isActiveAndEnabled&&Owner(n)!="").ToList();
                    report["npcCount"]=npcs.Count;
                    var nearest=player==null?null:npcs.OrderBy(n=>(n.transform.position-player.transform.position).sqrMagnitude).FirstOrDefault();
                    if(world!=null&&nearest!=null){
                        report["npc"]=nearest.CharacterInfo.name;
                        report["target"]=JObject.FromObject(new{world,owner=Owner(nearest),nativeId=nearest.CharacterInfo.id});
                        if(isolated!=null){
                            report["isolated"]=isolated.PathsMatch();report["testSlot"]=SaveMaster.GetActiveSlot();
                            dialogue.Open(nearest,report["target"]);float until=Time.realtimeSinceStartup+10;
                            while(dialogue.Visible&&!dialogue.Ready&&Time.realtimeSinceStartup<until)yield return null;
                            report["resolvedInPanel"]=dialogue.Ready;dialogue.Close();
                            report["baselineTopics"]=NativeConversation.TopicFit(nearest,false);
                            report["liveTopics"]=NativeConversation.TopicFit(nearest,true);
                            if(isolated.PathsMatch())ExerciseHooks(nearest,npcs.FirstOrDefault(n=>n!=nearest && n.GetComponent<NPCChatController>()!=null),report);
                        }
                    }
                }
                if(mode=="interface" || (mode=="provider" && isolated!=null && isolated.PathsMatch())){
                    bool live=mode=="provider";
                    var preview=dialogue.Preview(ModuleApi+"probes/"+id+(live?"/talk":"/stream")+"?session="+session,report,live);
                    while(true){
                        bool next;
                        try{next=preview.MoveNext();}catch(Exception e){Logger.LogWarning(e.GetBaseException().Message);dialogue.Close();break;}
                        if(!next)break;yield return preview.Current;
                    }
                }
                if(mode=="relationships"&&isolated?.TalkAddress!=null&&isolated.PathsMatch())
                    yield return dialogue.RunRelationships(report);
                if(mode=="grouptalk"&&isolated?.PrivateDiagnostics==true&&isolated.PathsMatch())
                    yield return dialogue.RunGroupHarness(report);
                if(mode=="interactions"&&isolated?.TalkAddress!=null&&isolated.PathsMatch())
                    yield return dialogue.RunInteractions(report);
                if((mode=="residentdialogue"||mode=="dialogueflow"||mode=="roleplay")&&isolated?.PrivateDiagnostics==true&&isolated.PathsMatch())
                    yield return dialogue.RunResidentDialogue(report,mode);
                if(mode=="quests"&&isolated?.TalkAddress!=null&&isolated.PathsMatch())
                    yield return dialogue.RunQuests(report);
                if(mode=="socialbalance"&&isolated?.PathsMatch()==true)
                    yield return dialogue.RunRelationships(report,true);
                if(mode=="voices"&&isolated?.PathsMatch()==true)
                    yield return VoiceHarness.Run(report,dialogue);
                var packet=report.ToString(Formatting.None);
                var bytes=Encoding.UTF8.GetBytes(packet);
                if(isolated!=null&&bytes.Length>24000){
                    using(var stream=new System.IO.MemoryStream()){
                        using(var gzip=new System.IO.Compression.GZipStream(stream,System.IO.Compression.CompressionMode.Compress,true))
                            gzip.Write(bytes,0,bytes.Length);
                        packet=JsonConvert.SerializeObject(new{session,compressed=Convert.ToBase64String(stream.ToArray())});
                    }
                }
                completedProbe=id;completedPacket=packet;
                yield return Post("probes/"+id,packet);
            } finally {probing=null;}
        }

        private void ExerciseHooks(NPCController npc,NPCController partner,JObject report)
        {
            if(isolated==null||!isolated.PathsMatch())return;
            testObservations=new JArray();
            try{
                var speaker=npc.GetComponent<NPCChatController>();var listener=partner?.GetComponent<NPCChatController>();
                var topic=speaker?.dnaData?.chatActions?.FirstOrDefault(x=>x?.name=="Small Talk");
                if(topic!=null&&listener!=null){
                    report["listenerOwner"]=Owner(partner);
                    AccessTools.Method(typeof(NPCChatController),"ReceiveResponse").Invoke(speaker,new object[]{topic,null,listener});
                }
                npc.AddToHiringItemList("Flour",2);
            }catch(Exception e){report["exerciseError"]=e.GetBaseException().Message;}
            finally{report["exercised"]=testObservations;testObservations=null;}
        }

        private static void Record(Character actor, string kind, string description, string chosenAction = null)
        {
            if (instance == null || actor == null || actor.CharacterInfo == null) return;
            if(instance.isolated!=null){
                instance.testObservations?.Add(JObject.FromObject(new {owner=Owner(actor),kind,description,chosenAction}));return;
            }
            var world = World();
            if (world == null) return;
            if (instance.pending.Count >= 256) { instance.dropped++; return; }
            instance.pending.Enqueue(JsonConvert.SerializeObject(new {
                id = Guid.NewGuid(), world, owner = Owner(actor), nativeId = actor.CharacterInfo.id, kind, description, chosenAction
            }));
        }

        private IEnumerator Post(string route, string data, Action<long> result = null, Action<string> success = null)
        {
            using (var req = new UnityWebRequest(ModuleApi+route, "POST"))
            {
                req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(data));
                req.downloadHandler = new DownloadHandlerBuffer();
                req.SetRequestHeader("Content-Type", "application/json");
                req.timeout = 4;
                yield return req.SendWebRequest();
                result?.Invoke(req.responseCode);
                if (req.responseCode == 200) success?.Invoke(req.downloadHandler.text);
            }
        }

        private IEnumerator SendQueue()
        {
            while (true)
            {
                if (pending.Count == 0) { yield return new WaitForSecondsRealtime(.25f); continue; }
                long status = 0;
                yield return Post("event", pending.Peek(), code => status = code);
                if (status == 200) pending.Dequeue();
                else if (status == 400 || status == 422) { pending.Dequeue(); dropped++; }
                else
                {
                    // A newly spawned NPC may not have a disk record until the next save.
                    // Let other already-indexed characters continue sending observations.
                    if (status == 409) pending.Enqueue(pending.Dequeue());
                    yield return new WaitForSecondsRealtime(3f);
                }
            }
        }

        static void RefreshSelectedOptions(bool closeCards)
        {
            if(!SaveMaster.IsSlotLoaded())return;
            var player=PlayerCharacterController.Instance;var chat=player?player.GetComponent<PlayerChat>():null;
            if(!chat||!chat.ContextMenuUI||DayNightCycle.Instance==null)return;
            bool hadCards=closeCards&&_Mirthwood.Scripts.Demo.Menus.ChatMenuController.IsOpen();
            if(hadCards)AccessTools.Method(typeof(PlayerChat),"CloseChatMenuOnDisable").Invoke(chat,null);
            if((hadCards||chat.ContextMenuUI.gameObject.activeSelf)&&Selected.GetValue(chat) is NPCChatController)
                AccessTools.Method(typeof(PlayerChat),"OpenQuickChat").Invoke(chat,null);
        }
        private void OnEnable(){if(instance==this&&ReplacesTalk)RefreshSelectedOptions(true);}
        private void OnDisable(){dialogue?.Close();QuestPanel.Close();if(!enabled&&instance==this)RefreshSelectedOptions(false);}
        private void OnDestroy() { SceneManager.activeSceneChanged -= OnSceneChanged; QuestPanel.Close(); dialogue?.Close(); reactionVoices?.Dispose(); hooks.UnpatchSelf(); instance = null; }

        [HarmonyPatch(typeof(NPCChatController), "ReceiveResponse")]
        private static class SocialHook
        {
            private static void Postfix(NPCChatController __instance, DnaData.Dna action, DnaData.Dna response, ChatController from)
            {
                if (from == null || __instance.Character == null || from.Character == null || action == null) return;
                var a = __instance.Character; var b = from.Character;
                if (a.CharacterInfo == null || b.CharacterInfo == null) return;
                var reaction = response == null ? "" : " The response was " + response.name + ".";
                if(instance.testObservations==null&&InteractionHooks.Compatible){
                    if(a is NPCController receiver && b is NPCController sender){
                        var first=NativeCues.Person(receiver,"I brought up "+action.name+" with "+sender.CharacterInfo.name+"."+reaction,action.name);
                        var other=NativeCues.Person(sender,receiver.CharacterInfo.name+" brought up "+action.name+" with me."+reaction);
                        if(first!=null&&other!=null)NativeCues.Record("social",new JArray(first,other));
                    }
                    return;
                }
                Record(a, "social", "Chose " + action.name + " with " + b.CharacterInfo.name + "." + reaction, action.name);
                if (b is NPCController) Record(b, "social", a.CharacterInfo.name + " chose " + action.name + " with them." + reaction);
            }
        }

        [HarmonyPatch(typeof(NPCController), "AddToHiringItemList")]
        private static class WorkHook
        {
            private static void Postfix(NPCController __instance, string itemName, int amount)
            {
                if(instance.testObservations==null&&InteractionHooks.Compatible){if(amount>0)NativeCues.Fact(__instance,"work","Added "+amount+" "+itemName+" to my hired-work collection.",false);return;}
                Record(__instance, "work", "Added " + amount + " " + itemName + " to the hired-work collection.");
            }
        }
    }
}
