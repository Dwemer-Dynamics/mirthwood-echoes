using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using HarmonyLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using _Mirthwood.Scripts.Demo;
using _Mirthwood.Scripts.Demo.Chat;
using _Mirthwood.Scripts.Demo.Combat;
using _Mirthwood.Scripts.Demo.Menus;
using _Mirthwood.Scripts.Demo.Stories;
using _Mirthwood.Scripts.Demo.Triggers;
using _Mirthwood.Scripts.Demo.Weather;
using _Mirthwood.Scripts.Runtime.Demo.Menus.Views;
using _Mirthwood.Scripts.Runtime.Demo.Minigame;
using Object = UnityEngine.Object;

namespace Mirthwood.Echoes
{
    internal sealed partial class NativeConversation
    {
        // These independent observations use Mirthwood's live objects and rendered UI.
        // Keep the product's awareness builder out of the expected side of the comparison.
        private sealed class AwarenessPlaytest
        {
            readonly JObject plan;
            readonly string output;
            int sequence;
            Vector3 initialPosition;
            Vector3? recentVenue;
            string Snapshot => Path.Combine(IsolatedGameRun.Active.Root, "awareness-earlier-visit");

            AwarenessPlaytest(JObject plan, string output)
            {
                this.plan = plan;
                this.output = output;
                Directory.CreateDirectory(output);
            }

            public static AwarenessPlaytest Read()
            {
                var run = IsolatedGameRun.Active;
                if (run?.PrivateDiagnostics != true || !run.PathsMatch()) return null;
                string file = Path.Combine(run.Root, "world-awareness.json");
                if (!File.Exists(file)) return null;
                if (new FileInfo(file).Length > 65536) throw new InvalidDataException("Oversized scene plan.");
                var plan = JObject.Parse(File.ReadAllText(file));
                if (!(plan["turns"] is JArray turns) || turns.Count < 1 || turns.Count > 24)
                    throw new InvalidDataException("The private scene needs one to 24 turns.");
                return new AwarenessPlaytest(plan, Path.Combine(run.Root, "awareness-evidence"));
            }

            public int TurnCount => ((JArray)plan["turns"]).Count;
            public string QuestQuestion => (string)plan["questQuestion"];
            public bool DeclineQuest => (bool?)plan["declineQuest"] == true;
            public string QuestDecision => (string)plan["questDecision"] ??
                (DeclineQuest ? "No thank you. I won't take this job." : "Yes, I'll do the work on the original terms.");
            public bool RetryAddress => (bool?)plan["retryAddress"] == true;
            public string HearingQuestion => (string)plan["hearingQuestion"];

            public string Words(int index) => (string)plan["turns"][index]["words"];

            static float[] At(Vector3 point) => new[] { point.x, point.y, point.z };
            static string PathOf(Transform item) => item.parent == null ? item.name : PathOf(item.parent) + "/" + item.name;
            static object Volume(Collider collider) => new {
                type = collider.GetType().Name, enabled = collider.enabled,
                center = At(collider.bounds.center), extent = At(collider.bounds.extents)
            };

            static object Calendar()
            {
                var cycle = DayNightCycle.Instance;
                var clock = AccessTools.Field(typeof(DayNightCycle), "clockText").GetValue(cycle) as TMP_Text;
                var hud = Object.FindObjectOfType<HUDMenuController>();
                TMP_Text Label(string field) => hud ? AccessTools.Field(typeof(HUDMenuController), field).GetValue(hud) as TMP_Text : null;
                return new { cycle.Now, cycle.YearNumber, cycle.YearLength, cycle.DayNumber, cycle.DayOfSeason,
                    season = cycle.CurrentSeason.ToString(), cycle.SeasonLocalized, cycle.TimeOfDayString,
                    hudClock = clock ? clock.text : null, hudClockVisible = clock && clock.isActiveAndEnabled,
                    hudDay = Label("dayText")?.text, hudSeason = Label("seasonText")?.text,
                    playerHudLocation = Label("currentLocationText")?.text, playerHudRegion = Label("currentRegionText")?.text,
                    sunlight = cycle.SunlightEnabled };
            }

            static object Resident(NPCController npc)
            {
                var position = npc.transform.position;
                var brain = npc.GetComponent<NPCChatController>();
                var agent = npc.GetComponent<NavMeshAgent>();
                var task = AccessTools.Field(typeof(NPCController), "_activeTask").GetValue(npc);
                var tree = AccessTools.Field(typeof(NPCController), "_activeTreeRef").GetValue(npc);
                var routine = AccessTools.Field(typeof(NPCController), "_behaviorTree").GetValue(npc);
                var running = tree == null ? null : AccessTools.Property(tree.GetType(),"ActiveTasks")?.GetValue(tree) as IEnumerable;
                var jobs = running?.Cast<object>().Select(t => new {
                    name = AccessTools.Property(t.GetType(),"Name")?.GetValue(t) as string,
                    status = AccessTools.Property(t.GetType(),"LastStatus")?.GetValue(t)?.ToString(),
                    enabled = AccessTools.Property(t.GetType(),"Enabled")?.GetValue(t),
                    sameOwner = ReferenceEquals(AccessTools.Property(t.GetType(),"Owner")?.GetValue(t),npc.gameObject),
                    type = t.GetType().FullName
                }).ToArray();
                var regions = Object.FindObjectsOfType<Region>().Select(r => new { region = r, volume = r.GetComponent<Collider>() })
                    .Where(r => r.volume && r.volume.enabled && r.volume.bounds.Contains(position))
                    .Select(r => new { region = r.region.name, condition = r.region.CurrentWeatherCondition?.Type,
                        collider = Volume(r.volume) }).ToArray();
                var places = Object.FindObjectsOfType<PointOfInterestTrigger>()
                    .Where(t => t.isActiveAndEnabled && t.GetComponent<Collider>() is Collider c && c.enabled && c.bounds.Contains(position))
                    .Select(t => new { name = t.name, type = t.GetType().Name, path = PathOf(t.transform) }).ToArray();
                var company = Object.FindObjectsOfType<NPCController>()
                    .Where(p => p != npc && p.CharacterInfo != null && p.isActiveAndEnabled && (p.transform.position - position).sqrMagnitude <= 100f)
                    .OrderBy(p => (p.transform.position - position).sqrMagnitude)
                    .Select(p => new { name = p.CharacterInfo.name, nativeId = p.CharacterInfo.id, position = At(p.transform.position),
                        distance = Vector3.Distance(p.transform.position,position), clearSight = GroupGathering.ClearSight(position,p.transform.position) }).ToArray();
                var signs = AuthoredPosters().Select(p => new {
                    kind = PosterKind(p), site = PathOf(p.transform), position = At(p.transform.position),
                    offset = At(p.transform.position-position), active = p.isActiveAndEnabled
                }).ToArray();
                var ground = Physics.RaycastAll(position + Vector3.up*.7f,Vector3.down,2f,Physics.DefaultRaycastLayers,QueryTriggerInteraction.Ignore)
                    .Where(hit=>hit.collider && !hit.collider.GetComponentInParent<NPCController>())
                    .OrderBy(hit=>hit.distance).Take(4).Select(hit=>PathOf(hit.collider.transform)).ToArray();
                return new { name = npc.CharacterInfo.name, nativeId = npc.CharacterInfo.id, owner = Plugin.Owner(npc),
                    homeTown = npc.CharacterInfo.town.ToString(),
                    scene = npc.gameObject.scene.name, position = At(position), layer = npc.gameObject.layer,
                    chatEnabled = brain && brain.chatEnabled, scriptedSpeak = brain && brain.GetDialogueData() != null,
                    taskType = task?.GetType().FullName, lastTaskLabel = AccessTools.Field(typeof(NPCController), "_lastTask").GetValue(npc) as string,
                    velocity = agent && agent.isActiveAndEnabled && agent.isOnNavMesh ? At(agent.velocity) : null,
                    stopped = agent && agent.isActiveAndEnabled && agent.isOnNavMesh ? (bool?)agent.isStopped : null,
                    destination = agent && agent.isActiveAndEnabled && agent.isOnNavMesh && agent.hasPath ? At(agent.destination) : null,
                    places, weatherVolumes = regions, company, signs, ground, routineTree = tree != null && ReferenceEquals(routine,tree), jobs };
            }

            public void Record(string stage, int turn, NPCController npc, object extra = null)
            {
                var player = PlayerCharacterController.Instance;
                var weather = Object.FindObjectOfType<WeatherManager>();
                var teller = Object.FindObjectOfType<Storyteller>();
                var stories = teller?.GetAllActiveStories().Where(s => s?.story?.eventData != null)
                    .Select(s => new { title = s.story.eventData.title, s.startTime }).ToArray();
                var entry = new { sequence = ++sequence, stage, turn, utc = DateTime.UtcNow, unityFrame = Time.frameCount,
                    scene = SceneManager.GetActiveScene().name, world = Plugin.World(), calendar = Calendar(),
                    player = new { position = At(player.transform.position), scene = player.gameObject.scene.name, layer = player.gameObject.layer },
                    playerWeather = new { region = weather?.CurrentRegion?.name, condition = weather?.CurrentWeatherCondition?.Type },
                    resident = Resident(npc), activeStories = stories, extra };
                File.AppendAllText(Path.Combine(output, "observations.jsonl"), JsonConvert.SerializeObject(entry) + Environment.NewLine);
            }

            static JArray Sites<T>() where T : Component => JArray.FromObject(Object.FindObjectsOfType<T>()
                .Where(x => x.gameObject.scene.IsValid()).OrderBy(x => x.name)
                .Select(x => new { name = x.name, path = PathOf(x.transform), position = At(x.transform.position),
                    scene = x.gameObject.scene.name, volume = x.TryGetComponent<Collider>(out var c) ? Volume(c) : null }));

            public IEnumerator Survey(NPCController npc)
            {
                initialPosition=npc.transform.position;
                var loaded = AppDomain.CurrentDomain.GetAssemblies().Where(a=>!a.IsDynamic &&
                    (a==typeof(Plugin).Assembly || a.GetName().Name=="BepInEx.Preloader" || a.GetName().Name=="BepInEx"))
                    .Select(a=>{
                        string path=a.Location;
                        string source="managed assembly location";
                        if(string.IsNullOrEmpty(path) && a.GetName().Name=="BepInEx.Preloader"){
                            var env=a.GetType("BepInEx.Preloader.EnvVars");
                            path=env?.GetProperty("DOORSTOP_INVOKE_DLL_PATH")?.GetValue(null) as string;
                            source="preloader's recorded Doorstop invocation path";
                        }
                        if(string.IsNullOrEmpty(path) && a==typeof(Plugin).Assembly &&
                            BepInEx.Bootstrap.Chainloader.PluginInfos.TryGetValue(Plugin.Id,out var loadedPlugin)){
                            path=loadedPlugin.Location;
                            source="loaded plugin registration";
                        }
                        string digest=null;
                        if(!string.IsNullOrEmpty(path) && File.Exists(path)){
                            using(var file=File.OpenRead(path))
                            using(var hash=SHA256.Create())
                                digest=BitConverter.ToString(hash.ComputeHash(file)).Replace("-","").ToLowerInvariant();
                        }
                        return new {name=a.GetName().Name,path,sha256=digest,source};
                    }).ToArray();
                File.WriteAllText(Path.Combine(output,"loaded-module.json"),JsonConvert.SerializeObject(loaded));
                var sites = new JObject {
                    ["scene"] = SceneManager.GetActiveScene().name,
                    ["loadedScenes"] = JArray.FromObject(Enumerable.Range(0,SceneManager.sceneCount).Select(i=>SceneManager.GetSceneAt(i).name)),
                    ["availableScenes"] = JArray.FromObject(Enumerable.Range(0,SceneManager.sceneCountInBuildSettings).Select(SceneUtility.GetScenePathByBuildIndex)),
                    ["streamingTriggers"] = JArray.FromObject(Object.FindObjectsOfType<SceneTrigger>().Select(t=>new {
                        path=PathOf(t.transform),position=At(t.transform.position),
                        load=(bool)AccessTools.Field(typeof(SceneTrigger),"loadScene").GetValue(t),
                        target=SceneUtility.GetScenePathByBuildIndex((int)AccessTools.Field(typeof(SceneTrigger),"loadSceneID").GetValue(t)),
                        volume=t.TryGetComponent<Collider>(out var c)?Volume(c):null })),
                    ["posters"] = Sites<CalendarContextMenu>(), ["places"] = Sites<PointOfInterestTrigger>(),
                    ["doors"] = Sites<DoorTrigger>(), ["teleports"] = Sites<TeleportPoint>(),
                    ["weatherRegions"] = Sites<Region>(),
                    ["residents"] = JArray.FromObject(Object.FindObjectsOfType<NPCController>()
                        .Where(n => n.CharacterInfo != null && n.isActiveAndEnabled && Plugin.Owner(n) != "")
                        .OrderBy(n => n.CharacterInfo.name).Select(Resident))
                };
                File.WriteAllText(Path.Combine(output, "scene.json"), sites.ToString());
                Record("before-approach", 0, npc);
                if ((bool?)plan["readPosters"] == true) yield return ReadPosters(npc);
            }

            public IEnumerator Screenshot(string name)
            {
                yield return new WaitForEndOfFrame();
                string path = Path.Combine(output, name + ".png");
                ScreenCapture.CaptureScreenshot(path);
                yield return Await(() => File.Exists(path) && new FileInfo(path).Length >= 256, "Missing native capture " + name, 8);
            }

            static CalendarContextMenu[] AuthoredPosters() => Resources.FindObjectsOfTypeAll<CalendarContextMenu>()
                .Where(p => p.gameObject.scene.IsValid() && p.gameObject.scene.isLoaded)
                .OrderByDescending(p => p.isActiveAndEnabled).ThenBy(p => PathOf(p.transform)).ToArray();

            static string PosterKind(CalendarContextMenu p) =>
                AccessTools.Field(typeof(CalendarContextMenu), "option").GetValue(p).ToString();

            static string[] DisabledParents(Transform item)
            {
                var result = new System.Collections.Generic.List<string>();
                for (var node=item;node;node=node.parent)
                    if(!node.gameObject.activeSelf)result.Add(PathOf(node));
                return result.ToArray();
            }

            IEnumerator ReadPosters(NPCController npc)
            {
                var posters = AuthoredPosters();
                Record("authored-posters", 0, npc, posters.Select(p => new {
                    kind=PosterKind(p), location=PathOf(p.transform), position=At(p.transform.position),
                    scene=p.gameObject.scene.name, enabled=p.isActiveAndEnabled, disabledParents=DisabledParents(p.transform)
                }).ToArray());
                var seen = new System.Collections.Generic.HashSet<string>();
                foreach (var poster in posters)
                {
                    if (!poster) continue;
                    string option = PosterKind(poster);
                    if (seen.Contains(option)) continue;
                    yield return Approach(poster.transform.position);
                    float until = Time.realtimeSinceStartup + 4;
                    while (poster && !poster.isActiveAndEnabled && Time.realtimeSinceStartup < until) yield return null;
                    if (!poster || !poster.isActiveAndEnabled) continue;
                    var type = typeof(CalendarContextMenu);
                    if (!(bool)AccessTools.Method(type, "CanInteract").Invoke(poster, null)) continue;
                    // The same Read callback registered with the native context menu.
                    AccessTools.Method(type, "OnUse").Invoke(poster, new object[] { 1 });
                    yield return new WaitForEndOfFrame();
                    Component menu = option=="Events" ? (Component)Object.FindObjectOfType<CalendarUIWindow>() : Object.FindObjectOfType<CalendarJoustingUIWindow>();
                    if (!menu || !menu.gameObject.activeInHierarchy) throw new InvalidOperationException("Poster did not open: "+option);
                    try {
                        var labels = menu.GetComponentsInChildren<TMP_Text>().Where(x => x.isActiveAndEnabled).Select(x => x.text).ToArray();
                        var images = menu.GetComponentsInChildren<Image>().Where(x => x.isActiveAndEnabled && x.sprite).Select(x => x.sprite.name).ToArray();
                        Record("read-poster", 0, npc, new { option, location=PathOf(poster.transform), labels, images });
                        yield return Screenshot("poster-"+option);
                        seen.Add(option);
                    } finally {
                        if (option == "Events") SimpleMenu<CalendarUIWindow>.Hide();
                        else SimpleMenu<CalendarJoustingUIWindow>.Hide();
                    }
                    yield return null;
                }
                Record("poster-survey",0,npc,new { opened=seen.OrderBy(x=>x).ToArray() });
            }

            public static IEnumerator Approach(Vector3 position)
            {
                var player = PlayerCharacterController.Instance;
                if ((player.transform.position - position).sqrMagnitude <= 2f) yield break;
                var desired = position + Vector3.right * .8f;
                Vector3 landing = NavMesh.SamplePosition(desired, out var hit, 1.5f, NavMesh.AllAreas) ? hit.position : desired;
                yield return player.CoTeleport(landing, 0, 0, false, false);
                yield return null;
            }

            public IEnumerator AllowScheduledStart(NPCController npc)
            {
                // Mirthwood evaluates its scheduled stories ten game seconds after a day change.
                // Let the story presentation finish before starting a social interaction.
                if (plan["turns"][0]["calendar"] == null) yield break;
                float from = Time.time;
                float deadline = Time.realtimeSinceStartup + 45;
                while (Time.time - from < 11f && Time.realtimeSinceStartup < deadline)
                {
                    if (StoryCardOpen)
                    {
                        Record("scheduled-story-card", 0, npc);
                        yield return Screenshot("scheduled-story-card-" + sequence);
                        yield return DismissGroupTestCards();
                    }
                    yield return null;
                }
                yield return DismissGroupTestCards();
                Record("after-scheduled-check", 0, npc);
            }

            public bool ChangesVisit(int index)
            {
                var step = plan["turns"][index];
                return step["calendar"] != null || step["doorway"] != null || step["venue"] != null || step["returnFromVenue"] != null ||
                    (bool?)step["newVisit"] == true || Reloads(index);
            }

            public bool Reloads(int index) => (bool?)plan["turns"][index]["rewind"] == true;

            public IEnumerator Reload(NPCController npc)
            {
                Record("before-rewind", 0, npc);
                string owner = Plugin.Owner(npc);
                yield return LoadSnapshot(Snapshot, owner);
                Record("after-rewind", 0, FindResident(owner));
            }

            public void Cleanup() => RemoveSnapshot(IsolatedGameRun.Active.Root, Snapshot);

            public IEnumerator ApproachSpeaker(int index, NPCController npc)
            {
                if ((bool?)plan["turns"][index]["watchResidentWalk"] == true) {
                    // Stream the resident into the active simulation before trying to interrupt them.
                    // Never start an NPC task or supply its destination for this observation.
                    var player = PlayerCharacterController.Instance;
                    var waitPoint = StandBackFrom(npc.transform.position, false);
                    if (!waitPoint.HasValue) throw new InvalidOperationException("No walking surface near the resident's routine.");
                    yield return player.CoTeleport(waitPoint.Value, 0, 0, false, false);
                    GameState.UnPause();
                    yield return Await(() => StandBackFrom(npc.transform.position, true).HasValue,
                        "No solid ground streamed beside the moving resident.", 8);
                    var actual = StandBackFrom(npc.transform.position, true);
                    if (!actual.HasValue) throw new InvalidOperationException("The path alongside the resident was unloaded.");
                    yield return player.CoTeleport(actual.Value, 0, 0, false, false);
                    var start=npc.transform.position;
                    var agent=npc.GetComponent<NavMeshAgent>();
                    Record("waiting-for-routine",index,npc,new {groundedPoint=At(actual.Value)});
                    yield return Await(()=>agent && agent.isActiveAndEnabled && agent.isOnNavMesh &&
                        agent.hasPath && !agent.isStopped && agent.velocity.sqrMagnitude>.04f &&
                        (npc.transform.position-start).sqrMagnitude>.16f,"The resident did not walk under their own routine.",28);
                    Record("walking-before-talk",index,npc,new {start=At(start),moved=Vector3.Distance(start,npc.transform.position)});
                }
                if (plan["turns"][index]["doorway"] == null && plan["turns"][index]["venue"] == null)
                    yield return Approach(npc.transform.position);
            }

            static Vector3? StandBackFrom(Vector3 resident, bool requireGround)
            {
                for (int i=0; i<16; i++) {
                    var sample=resident+Quaternion.Euler(0,i*22.5f,0)*Vector3.right*5.5f;
                    if (!NavMesh.SamplePosition(sample,out var hit,1.5f,NavMesh.AllAreas)) continue;
                    float distance=(hit.position-resident).sqrMagnitude;
                    if (distance<16f || distance>49f) continue;
                    if (requireGround && !Physics.RaycastAll(hit.position+Vector3.up*.65f,Vector3.down,1.3f,
                        Physics.DefaultRaycastLayers,QueryTriggerInteraction.Ignore).Any(p=>
                        p.normal.y>.4f && !p.collider.GetComponentInParent<Character>() && Mathf.Abs(p.point.y-hit.position.y)<.45f)) continue;
                    return hit.position;
                }
                return null;
            }

            IEnumerator Doorway(int index, NPCController npc, JObject spec)
            {
                string building = (string)spec["building"];
                var doorName=(string)spec["entrance"];
                var doors = Resources.FindObjectsOfTypeAll<DoorTrigger>()
                    .Where(d => d.gameObject.scene.isLoaded && PathOf(d.transform).IndexOf(building,StringComparison.OrdinalIgnoreCase)>=0 &&
                        (doorName==null||PathOf(d.transform).EndsWith("/"+doorName,StringComparison.OrdinalIgnoreCase)))
                    .OrderByDescending(d => PathOf(d.transform).IndexOf("Exterior",StringComparison.OrdinalIgnoreCase)>=0)
                    .ThenBy(d => (d.transform.position-npc.transform.position).sqrMagnitude).ToArray();
                Record("authored-doors",index,npc,doors.Select(d=>new {path=PathOf(d.transform),position=At(d.transform.position),forward=At(d.transform.forward)}).ToArray());
                DoorTrigger door=null;
                Vector3 center=default, inward=default, inside=default, outside=default;
                foreach(var candidate in doors)
                {
                    center=candidate.transform.position;
                    yield return Approach(center);
                    float streamUntil=Time.realtimeSinceStartup+3f;
                    while(candidate&&!candidate.isActiveAndEnabled&&Time.realtimeSinceStartup<streamUntil)yield return null;
                    if(!candidate || !candidate.isActiveAndEnabled)continue;
                    inward=Vector3.ProjectOnPlane(candidate.transform.forward,Vector3.up).normalized;
                    Vector3 lateral=Vector3.Cross(inward,Vector3.up);
                    var samples=new System.Collections.Generic.List<Vector3>();
                    foreach(float depth in new[]{-.8f,.8f,-1.4f,1.4f,-2f,2f})
                        foreach(float width in new[]{0f,.6f,-.6f,1.2f,-1.2f})
                            if(NavMesh.SamplePosition(center+depth*inward+width*lateral,out var hit,3f,NavMesh.AllAreas))
                                samples.Add(hit.position);
                    var pairs=from a in samples where Vector3.Dot(a-center,inward)>.55f
                              from z in samples where Vector3.Dot(z-center,inward)<-.55f && (a-z).sqrMagnitude<13f
                              orderby (a-center).sqrMagnitude+(z-center).sqrMagnitude
                              select new {inside=a,outside=z};
                    var pair=pairs.FirstOrDefault();
                    Record("door-navmesh",index,npc,new {path=PathOf(candidate.transform), center=At(center),forward=At(inward),
                        samples=samples.Distinct().Select(At).ToArray(),found=pair!=null});
                    if(pair==null)continue;
                    door=candidate;inside=pair.inside;outside=pair.outside;break;
                }
                if(!door)throw new InvalidOperationException("No pair of walkable points on opposing sides of "+building);
                Vector3 Landing(string side) => side=="inside"?inside:side=="outside"?outside:throw new InvalidDataException("Unknown side of doorway.");
                var player = PlayerCharacterController.Instance;
                yield return player.CoTeleport(Landing("outside"),0,0,false,false);
                yield return new WaitForFixedUpdate();
                yield return player.CoTeleport(center,0,0,false,false);
                yield return new WaitForFixedUpdate();
                yield return player.CoTeleport(Landing((string)spec["playerSide"]),0,0,false,false);
                StageGuest(npc,Landing((string)spec["speakerSide"]));
                yield return new WaitForFixedUpdate();
                if(StoryCardOpen)yield return DismissGroupTestCards();
                var nativeInside=AccessTools.Method(typeof(DoorTrigger),"IsOtherInside");
                var fading=AccessTools.Field(typeof(DoorTrigger),"fadeOutOnEnterSprites").GetValue(door) as System.Collections.Generic.List<SpriteRenderer>;
                Record("doorway",index,npc,new { path=PathOf(door.transform), center=At(center),forward=At(inward),
                    playerSide=Vector3.Dot(player.transform.position-center,inward), residentSide=Vector3.Dot(npc.transform.position-center,inward),
                    nativeSpeakerSide=nativeInside.Invoke(door,new object[]{npc.transform.position}),
                    nativePlayerSide=nativeInside.Invoke(door,new object[]{player.transform.position}),
                    exteriorSprites=fading?.Where(sprite=>sprite).Select(sprite=>new {name=sprite.name,alpha=sprite.color.a,visible=sprite.isVisible}).ToArray(),
                    nativePlayerInside=AccessTools.Field(typeof(DoorTrigger),"isPlayer1Inside").GetValue(door),
                    sight=GroupGathering.ClearSight(npc.transform.position,player.transform.position) });
                yield return Screenshot("door-" + (index+1));
            }

            IEnumerator Venue(int index, NPCController npc, JObject spec)
            {
                var teller = Object.FindObjectOfType<Storyteller>();
                string title = (string)spec["title"];
                if (!teller || string.IsNullOrEmpty(title)) throw new InvalidDataException("No native story source.");
                Record("venue-before",index,npc,spec);
                if (!teller.TryGetActiveStory(title,out var story)) {
                    var schedule = teller.scheduledStories.FirstOrDefault(s => s.story?.eventData?.title == title &&
                        s.season.HasFlag(DayNightCycle.Instance.CurrentSeason));
                    if(schedule!=null && schedule.story.CanBegin())teller.StartNewScheduledStory(schedule);
                    else{
                        var random=teller.stories.SingleOrDefault(s=>s?.eventData?.title==title);
                        if(random==null)throw new InvalidOperationException("The copied native event cannot start: " + title);
                        teller.ForceStartStory(random);
                    }
                    Record("fixture-started-native-story",index,npc,new {title,scheduled=schedule!=null});
                    yield return Await(() => teller.HasActiveStory(title),"The native event did not start.");
                    teller.TryGetActiveStory(title,out story);
                }
                var field = AccessTools.Field(story.story.GetType(), "_spawnedObject");
                var root = field?.GetValue(story.story) as GameObject;
                if (!root || !root.activeInHierarchy) throw new InvalidOperationException("The native event has no active venue.");
                if (title=="Grand Joust") yield return VisitJoustHerald(index,npc,root);
                string target = (string)spec["landmark"];
                var marks=root.GetComponentsInChildren<Renderer>(true)
                    .Where(r=>r.name.StartsWith(target,StringComparison.OrdinalIgnoreCase))
                    .OrderBy(r=>(r.transform.position-npc.transform.position).sqrMagnitude).ToArray();
                Record("venue-landmarks",index,npc,marks.Select(r=>new {path=PathOf(r.transform),position=At(r.transform.position)}));
                if(marks.Length==0)throw new InvalidOperationException("No actual venue landmark named "+target);
                yield return Approach(marks[0].transform.position);
                yield return DismissGroupTestCards();
                yield return Await(()=>marks.Any(r=>r&&r.enabled&&r.gameObject.activeInHierarchy),"No venue landmark became visible.",8);
                Renderer mark=null;
                Vector3 landing=default;
                foreach(var candidate in marks.Where(r=>r&&r.enabled&&r.gameObject.activeInHierarchy)){
                    int walkable=0;
                    string blockedBy=null;
                    foreach(float radius in new[]{1.4f,2f,2.7f,3.2f,4.5f,6f,7.5f,8.8f}){
                        for(int i=0;i<16;i++){
                            var point=candidate.transform.position+Quaternion.Euler(0,i*22.5f,0)*Vector3.forward*radius;
                            if(!NavMesh.SamplePosition(point,out var hit,3f,NavMesh.AllAreas)||
                                (hit.position-candidate.transform.position).sqrMagnitude>81f)continue;
                            walkable++;
                            var eye=hit.position+Vector3.up*.85f;
                            var direction=candidate.bounds.ClosestPoint(eye)-eye;
                            var obstruction=Physics.RaycastAll(eye,direction.normalized,direction.magnitude,Physics.DefaultRaycastLayers,
                                QueryTriggerInteraction.Ignore).OrderBy(v=>v.distance).FirstOrDefault(v=>!v.collider.GetComponentInParent<Character>());
                            if(obstruction.collider && obstruction.transform!=candidate.transform &&
                                !obstruction.transform.IsChildOf(candidate.transform) &&
                                (candidate.bounds.ClosestPoint(obstruction.point)-obstruction.point).sqrMagnitude>.01f){
                                blockedBy=PathOf(obstruction.transform);
                                continue;
                            }
                            landing=hit.position;mark=candidate;break;
                        }
                        if(mark)break;
                    }
                    Record("venue-view-search",index,npc,new {path=PathOf(candidate.transform),walkable,blockedBy,found=mark!=null});
                    if(mark)break;
                }
                if(!mark)throw new InvalidOperationException("No walkable view of the active venue.");
                StageGuest(npc,landing);
                var spot=mark.transform.position;
                var toward=Vector3.ProjectOnPlane(spot-landing,Vector3.up).normalized;
                if(!NavMesh.SamplePosition(landing+toward*.8f,out var playerPoint,1f,NavMesh.AllAreas))
                    throw new InvalidOperationException("No walkable spot beside the witness.");
                var observer=PlayerCharacterController.Instance;
                yield return observer.CoTeleport(playerPoint.position,0,0,false,false);
                GroupGathering.Face(npc,spot);
                recentVenue=spot;
                Record("at-venue",index,npc,new {title,root=root.name,landmark=PathOf(mark.transform),position=At(spot),
                    speakerDistance=Vector3.Distance(npc.transform.position,spot),playerDistance=Vector3.Distance(observer.transform.position,spot),
                    spawnedInstance=root.GetInstanceID(),landmarkInstance=mark.GetInstanceID(),
                    boundsCenter=At(mark.bounds.center),render=mark.isVisible,live=teller.HasActiveStory(title)});
                yield return Screenshot("venue-" + (index+1));
                yield return new WaitForSecondsRealtime(3);
                if ((bool?)spec["end"] == true) {
                    teller.EndStory(title);
                    yield return Await(() => !teller.HasActiveStory(title),"The native event did not end.",8);
                    Record("after-native-event-end",index,npc,new {title});
                    yield return DismissGroupTestCards();
                }
            }

            IEnumerator VisitJoustHerald(int index, NPCController witness, GameObject root)
            {
                var joust=root.GetComponentInChildren<JoustingMiniGame>(true);
                var herald=joust ? AccessTools.Field(typeof(JoustingMiniGame),"npcEventStarter")?.GetValue(joust) as NPCChatController : null;
                if(!herald || !herald.transform.IsChildOf(root.transform)) throw new InvalidOperationException("The active joust has no public starter.");
                yield return Approach(herald.transform.position);
                yield return Await(()=>herald.isActiveAndEnabled && herald.GetDialogueData()!=null,
                    "No public tournament dialogue at the native herald.",10);
                yield return DismissGroupTestCards();
                var player=PlayerCharacterController.Instance.GetComponent<PlayerChat>();
                var selected=Plugin.Selected.GetValue(player);
                var target=player.chatTarget;
                var offer=herald.GetDialogueData();
                DialogueView stock=null;
                try {
                    stock=ChooseSpeak(herald.GetComponent<NPCController>());
                    yield return null;
                    Record("joust-public-starter",index,witness,new {path=PathOf(herald.transform),position=At(herald.transform.position),
                        nativeDialogue=offer?.Dialogue,choices=offer?.Choices?.Select(c=>c.choiceText).ToArray(),spawnedInstance=root.GetInstanceID(),
                        loadedScenes=Enumerable.Range(0,SceneManager.sceneCount).Select(i=>SceneManager.GetSceneAt(i).name).ToArray()});
                    yield return Screenshot("joust-herald-"+index);
                } finally {
                    if(stock) stock.HideDialogue();
                    player.chatTarget=target;
                    Plugin.Selected.SetValue(player,selected);
                }
            }

            public IEnumerator SetScene(int index, NPCController npc)
            {
                var step = plan["turns"][index] as JObject;
                if (step?["calendar"] is JObject date)
                {
                    var cycle = DayNightCycle.Instance;
                    string[] seasons = { "Spring", "Summer", "Fall", "Winter" };
                    int season = Array.IndexOf(seasons, (string)date["season"]);
                    int day = (int)date["day"], seasonLength = cycle.YearLength / 4;
                    if (season < 0 || day < 1 || day > seasonLength) throw new InvalidDataException("Invalid copied date.");
                    int year=(int?)date["year"]??cycle.YearNumber;
                    int target=year*cycle.YearLength+season*seasonLength+day-1;
                    int absoluteDay=cycle.YearNumber*cycle.YearLength+cycle.DayNumber;
                    if (date["year"] == null && target < absoluteDay) {
                        year++;
                        target += cycle.YearLength;
                    }
                    if(target<1)throw new InvalidDataException("The rollover scene needs a preceding game day.");
                    var state = JObject.Parse(cycle.OnSave());
                    if(absoluteDay!=target){
                        // Let the copied native game dispatch DAY_CHANGED and its season, HUD and story updates.
                        state["yearNumber"]=(target-1)/cycle.YearLength;
                        state["dayNumber"]=(target-1)%cycle.YearLength;
                        state["timeOfDay"]=0.999999f;
                        cycle.OnLoad(state.ToString(Formatting.None));
                        GameState.UnPause();
                        yield return Await(()=>cycle.YearNumber*cycle.YearLength+cycle.DayNumber==target,"The native day did not roll over.",12);
                        state=JObject.Parse(cycle.OnSave());
                    }
                    if (date["time"] != null) {
                        float fraction = (float)date["time"];
                        if (fraction < 0 || fraction >= 1) throw new InvalidDataException("Invalid time of day.");
                        state["timeOfDay"] = fraction;
                    }
                    cycle.OnLoad(state.ToString(Formatting.None));
                    yield return null;
                    yield return null;
                    if (cycle.DayOfSeason != day || cycle.YearNumber!=year) throw new InvalidOperationException("Copied calendar did not reach the requested date.");
                    using (var req = Post(IsolatedGameRun.Active.TalkAddress + "module/heartbeat", new { version=ModIdentity.Version, world=Plugin.World() })) {
                        yield return req.SendWebRequest();
                        if (req.responseCode != 200 && req.responseCode != 204) throw new IOException("Private clock heartbeat failed.");
                    }
                }
                if ((bool?)step?["snapshot"] == true) {
                    CaptureSlot(Snapshot);
                    Record("branch-saved", index, npc);
                }
                if (step?["doorway"] is JObject door) yield return Doorway(index,npc,door);
                if (step?["venue"] is JObject venue) yield return Venue(index,npc,venue);
                if (step?["returnFromVenue"]?.Type==JTokenType.String) {
                    string eventName=(string)step["returnFromVenue"];
                    yield return Approach(initialPosition);
                    StageGuest(npc,initialPosition);
                    float distance=recentVenue.HasValue?Vector3.Distance(npc.transform.position,recentVenue.Value):0;
                    if(distance<20)throw new InvalidOperationException("The subject is still within sight of the event.");
                    Record("left-venue",index,npc,new {eventName,distance});
                    var teller=Object.FindObjectOfType<Storyteller>();
                    if(!teller || !teller.HasActiveStory(eventName))throw new InvalidOperationException("The remote event is already absent.");
                    teller.EndStory(eventName);
                    yield return Await(()=>!teller.HasActiveStory(eventName),"The remote event did not end.",8);
                    Record("remote-event-ended",index,npc,new {eventName});
                }
                Record("before-visit", index, npc, step);
            }

            public IEnumerator OpenNativeMenu(NPCController npc, int index)
            {
                if(StoryCardOpen)yield return DismissGroupTestCards();
                Record("before-native-menu",index,npc);
                var menu = ShowQuickMenu(npc);
                var labels = QuickLabels(menu);
                Record("native-menu", index, npc, new { labels });
                if (!labels.Contains("Talk")) throw new InvalidOperationException("Native menu has no Talk option: " + string.Join(", ", labels));
                SelectQuickAction(menu, "Talk");
                yield return null;
            }

            public IEnumerator Submitted(int index, NPCController npc, JObject intent)
            {
                Record("accepted", index, npc, new { turn = (string)intent["id"], intent = intent.DeepClone() });
                yield break;
            }

            public IEnumerator Delivered(int index, NPCController npc, JObject row)
            {
                Record("bubble", index, npc, row);
                if ((bool?)plan["turns"][index]["capture"] == true) yield return Screenshot("turn-" + (index+1));
            }

            public IEnumerator ObserveCircle(NativeConversation talk, JArray cues)
            {
                int revision = -1;
                string previousBubble = null;
                float started = 0;
                while (true)
                {
                    yield return new WaitForEndOfFrame();
                    if (!talk.GroupActive || !talk.resident) continue;
                    if (talk.busy && talk.phase == Phase.Waiting && revision != talk.groupRevision)
                    {
                        revision = talk.groupRevision;
                        started = Time.realtimeSinceStartup;
                        Record("group-dispatch", talk.groupSequence, talk.resident, new {
                            session = talk.gathering.Session, head = NativeCues.Head,
                            previous = talk.groupLastId, revision,
                            audience = talk.Participants().Select(g => g.Actor).ToArray()
                        });
                    }
                    if (talk.phase != Phase.Reading || talk.groupLastId == previousBubble || talk.npcBubble == null) continue;
                    var entry = cues.LastOrDefault(c => (string)c["id"] == talk.groupLastId && (string)c["kind"] == "group");
                    string words = (string)entry?["line"]?["words"];
                    if (words == null || (string)entry["line"]["speaker"] == "player" || !talk.npcBubble.ShowsCompleteMessage(words)) continue;
                    previousBubble = talk.groupLastId;
                    Record("group-bubble", talk.groupSequence, talk.resident, new {
                        id = previousBubble, revision, line = entry["line"], rendered = talk.npcBubble.ReadRenderedText(),
                        ink = talk.npcBubble.ReadVisibleInk(), elapsedMs = Mathf.RoundToInt((Time.realtimeSinceStartup-started)*1000)
                    });
                }
            }

            public IEnumerator DisconnectAddress(NativeConversation talk, NPCController npc)
            {
                yield return Await(()=>talk.pendingAddress!=null,"The native passing remark never dispatched.",8);
                var request=talk.pendingAddress;
                var body=JObject.Parse(System.Text.Encoding.UTF8.GetString(request.uploadHandler.data));
                string id=(string)body["id"];
                if(!Guid.TryParse(id,out _))throw new InvalidDataException("No replay identity on the native passing request.");
                string marker=Path.Combine(IsolatedGameRun.Active.Root,"address-retry.json");
                string ready=Path.Combine(IsolatedGameRun.Active.Root,"address-dispatched");
                File.WriteAllText(marker+".tmp",body.ToString());
                File.Move(marker+".tmp",marker);
                yield return Await(()=>File.Exists(ready),"No matching private provider dispatch appeared.",22);
                if(File.ReadAllText(ready)!=id || request.isDone)throw new IOException("Could not interrupt the original request in flight.");
                Record("address-disconnect",0,npc,new {id,body});
                request.Abort();
            }
        }
    }
}
