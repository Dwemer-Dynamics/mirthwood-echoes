using System;
using System.Collections;
using System.IO;
using System.Linq;
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
                    hudDay = Label("dayText")?.text, hudSeason = Label("seasonText")?.text, sunlight = cycle.SunlightEnabled };
            }

            static object Resident(NPCController npc)
            {
                var position = npc.transform.position;
                var brain = npc.GetComponent<NPCChatController>();
                var agent = npc.GetComponent<NavMeshAgent>();
                var task = AccessTools.Field(typeof(NPCController), "_activeTask").GetValue(npc);
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
                return new { name = npc.CharacterInfo.name, nativeId = npc.CharacterInfo.id, owner = Plugin.Owner(npc),
                    scene = npc.gameObject.scene.name, position = At(position), layer = npc.gameObject.layer,
                    chatEnabled = brain && brain.chatEnabled, scriptedSpeak = brain && brain.GetDialogueData() != null,
                    taskType = task?.GetType().FullName, lastTaskLabel = AccessTools.Field(typeof(NPCController), "_lastTask").GetValue(npc) as string,
                    velocity = agent && agent.isActiveAndEnabled && agent.isOnNavMesh ? At(agent.velocity) : null,
                    destination = agent && agent.isActiveAndEnabled && agent.isOnNavMesh && agent.hasPath ? At(agent.destination) : null,
                    places, weatherVolumes = regions, company };
            }

            public void Record(string stage, int turn, NPCController npc, object extra = null)
            {
                var player = PlayerCharacterController.Instance;
                var weather = Object.FindObjectOfType<WeatherManager>();
                var stories = Storyteller.Instance.GetAllActiveStories().Where(s => s?.story?.eventData != null)
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

            IEnumerator Screenshot(string name)
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

            public bool ChangesVisit(int index)
            {
                var step = plan["turns"][index];
                return step["calendar"] != null || (bool?)step["newVisit"] == true;
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
                    if(target<1)throw new InvalidDataException("The rollover scene needs a preceding game day.");
                    var state = JObject.Parse(cycle.OnSave());
                    if(Mathf.FloorToInt(cycle.Now)!=target){
                        // Let the copied native game dispatch DAY_CHANGED and its season, HUD and story updates.
                        state["yearNumber"]=(target-1)/cycle.YearLength;
                        state["dayNumber"]=(target-1)%cycle.YearLength;
                        state["timeOfDay"]=0.999999f;
                        cycle.OnLoad(state.ToString(Formatting.None));
                        GameState.UnPause();
                        yield return Await(()=>Mathf.FloorToInt(cycle.Now)==target,"The native day did not roll over.",12);
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
                Record("before-visit", index, npc, step);
            }

            public IEnumerator OpenNativeMenu(NPCController npc, int index)
            {
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
        }
    }
}
