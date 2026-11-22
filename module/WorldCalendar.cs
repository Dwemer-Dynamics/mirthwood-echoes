using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using HarmonyLib;
using Lowscope.Saving;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;
using _Mirthwood.Scripts.Demo.Chat;
using _Mirthwood.Scripts.Demo.Combat;
using _Mirthwood.Scripts.Demo.Menus;
using _Mirthwood.Scripts.Demo.Stories;
using _Mirthwood.Scripts.Demo.Utils;
using _Mirthwood.Scripts.Demo.Weather;
using _Mirthwood.Scripts.Localization;
using _Mirthwood.Scripts.Runtime.Demo.Minigame;
using _Mirthwood.Scripts.Runtime.Monobehaviors;

namespace Mirthwood.Echoes
{
    internal static class WorldCalendar
    {
        // These are the public CalendarContextMenu posters in Mirthwood West and Brightoak. The dates
        // come from their readable artwork. The native scheduled-story list also contains
        // unadvertised incidents and player-only conditions, so it cannot supply a notice.
        static readonly Season[] Seasons = { Season.Spring, Season.Summer, Season.Fall, Season.Winter };
        static readonly Regex PosterDate = new Regex(@"^(\d{1,2})(?:st|nd|rd|th) of (Spring|Summer|Fall|Winter)$",
            RegexOptions.CultureInvariant);
        static readonly HashSet<string> Festivals = new HashSet<string>(StringComparer.Ordinal)
            { "Festival of Blossoms", "Summer Solstice", "All Hallows Eve", "Yuletide" };
        const string Joust = "Grand Joust";
        const string SaveKey = "mirthwood.echoes.worldEventsToday";

        sealed class Notice
        {
            internal string Title;
            internal int Season, Day;
            internal bool Jousting;

            internal JObject Date(int year, bool local) => Event(Title, "posted", "public notice", Season, Day, year,
                local ? (Title == Joust ? "Jousting Arena" : "Brightoak") : null);
        }

        static List<Notice> notices;
        static int noticeScene, noticeDay;
        static string noticeLoad;
        static float retryPosters;
        static bool readBothPosters;

        // The inactive menu prefabs retain the source-language text even when the open
        // menu instances have been translated. Reading a prefab does not open either menu.
        static TextMeshProUGUI[] Labels<T>() where T : Component
        {
            var board = Resources.FindObjectsOfTypeAll<T>()
                .OrderBy(x => x.gameObject.scene.IsValid()).FirstOrDefault();
            return board ? board.GetComponentsInChildren<TextMeshProUGUI>(true) : Array.Empty<TextMeshProUGUI>();
        }

        static List<Notice> ReadNotices()
        {
            int scene = SceneManager.GetActiveScene().handle;
            int day = Mathf.FloorToInt(DayNightCycle.Instance.Now);
            if (notices != null && noticeScene == scene && noticeDay == day && noticeLoad == NativeCues.Session &&
                (readBothPosters || Time.realtimeSinceStartup < retryPosters)) return notices;
            notices = new List<Notice>();
            noticeScene = scene;
            noticeDay = day;
            noticeLoad = NativeCues.Session;
            retryPosters = Time.realtimeSinceStartup + 15f;
            bool readFestivals = ReadBoard(Labels<CalendarUIWindow>(), false);
            bool readJoust = ReadBoard(Labels<CalendarJoustingUIWindow>(), true);
            readBothPosters = readFestivals && readJoust;
            return notices;
        }

        static bool ReadBoard(TextMeshProUGUI[] labels, bool jousting)
        {
            if (jousting && !labels.Any(x => x.name == "Header" && x.text == Joust)) return false;
            var names = labels.Where(x => x.name == "Festival Name" && !string.IsNullOrWhiteSpace(x.text) &&
                x.text.Length <= 120 && !x.text.Any(char.IsControl)).ToArray();
            int count = notices.Count;
            foreach (var date in labels.Where(x => x.name.StartsWith("Festival Date", StringComparison.Ordinal)))
            {
                var match = PosterDate.Match(date.text ?? "");
                if (!match.Success || !Enum.TryParse(match.Groups[2].Value, out Season season)) continue;
                string title = Joust;
                if (!jousting)
                {
                    // Names and dates are in separate columns. Match on the rendered row.
                    float y = date.rectTransform.anchoredPosition.y;
                    var row = names.Where(x => Math.Abs(x.rectTransform.anchoredPosition.y - y) < 12f).ToArray();
                    if (row.Length != 1) continue;
                    title = row[0].text;
                }
                int day = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
                int index = Array.IndexOf(Seasons, season);
                if (index >= 0 && day > 0 && !notices.Any(n => n.Jousting == jousting && n.Title == title && n.Season == index && n.Day == day))
                    notices.Add(new Notice { Title = title, Season = index, Day = day, Jousting = jousting });
            }
            return notices.Count > count;
        }

        static JObject Event(string title, string status, string source, int season, int day, int year, string place = null)
        {
            var result = new JObject
            {
                ["title"] = title, ["status"] = status, ["source"] = source,
                ["season"] = LocalizationManager.GetLocalizedStringUI(Seasons[season].ToString()),
                ["seasonIndex"] = season, ["dayOfSeason"] = day, ["year"] = year
            };
            if (!string.IsNullOrWhiteSpace(place)) result["place"] = place;
            return result;
        }

        static void AddNotices(JObject snapshot, IDictionary<string, JObject> today, DayNightCycle clock, int season,
            bool local, bool festivalBoard, bool joustBoard)
        {
            int seasonLength = clock.YearLength / 4;
            int position = season * seasonLength + clock.DayOfSeason;
            int Offset(Notice n) => n.Season * seasonLength + n.Day - position;
            int Until(Notice n) => Offset(n) > 0 ? Offset(n) : Offset(n) + clock.YearLength;
            var known = ReadNotices().Where(n => n.Day <= seasonLength && (n.Jousting ? joustBoard : festivalBoard)).OrderBy(Until);
            var nextByKind = new JObject();
            int previous = int.MinValue;
            foreach (var notice in known)
            {
                int offset = Offset(notice);
                if (offset == 0) today[notice.Title] = notice.Date(clock.YearNumber, local);
                string kind = notice.Jousting ? "joust" : "festival";
                if (nextByKind[kind] == null)
                {
                    var posted = notice.Date(clock.YearNumber + (offset > 0 ? 0 : 1), local);
                    if (!nextByKind.HasValues) snapshot["nextNotice"] = posted.DeepClone();
                    nextByKind[kind] = posted;
                }
                int before = offset < 0 ? offset : offset - clock.YearLength;
                int year = clock.YearNumber - (offset < 0 ? 0 : 1);
                if (year >= 0 && before > previous)
                {
                    previous = before;
                    snapshot["previousNotice"] = notice.Date(year, local);
                }
            }
            if (nextByKind.HasValues) snapshot["nextByKind"] = nextByKind;
        }

        // Each venue belongs to an actual instance created by a running public story.
        // Renderers on an inactive prefab, a spawn point or a map marker cannot witness it.
        static readonly FieldInfo Spawned = AccessTools.Field(typeof(SpawnPrefabStory), "_spawnedObject");
        static readonly FieldInfo JoustStarter = AccessTools.Field(typeof(JoustingMiniGame), "npcEventStarter");
        static readonly FieldInfo RaidActive = AccessTools.Field(typeof(BanditRaidController), "_questActive");
        static readonly FieldInfo PlagueActive = AccessTools.Field(typeof(VerminPlagueController), "_questActive");
        // LazyLoadSingleton.Instance instantiates the Storyteller when absent. Dialogue must
        // not start a new instance or any of its native story timers merely by observing.
        static readonly FieldInfo TellerInstance = AccessTools.Field(typeof(Storyteller).BaseType, "_instance");
        static Storyteller Teller => TellerInstance?.GetValue(null) as Storyteller;
        static readonly Dictionary<GameObject, Venue> venues = new Dictionary<GameObject, Venue>();
        static readonly HashSet<Storyteller.ActiveStory> previousTimeline = new HashSet<Storyteller.ActiveStory>();

        sealed class Venue
        {
            const float SightRangeSquared = 9f * 9f;
            internal readonly string Title;
            readonly Transform[] landmarks;
            readonly JoustingMiniGame tournament;
            readonly NPCChatController starter;
            internal readonly Component Incident;
            readonly FieldInfo active;

            internal Venue(GameObject root, string title, Component incident, FieldInfo active)
            {
                Title = title;
                Incident = incident;
                this.active = active;
                Transform publicJugglers = null;
                if (title == Joust)
                {
                    publicJugglers = root.transform.Find("JOUSTING ARENA/Brightoak Decor/Jugglers");
                    tournament = root.GetComponent<JoustingMiniGame>();
                    if (tournament) starter = JoustStarter?.GetValue(tournament) as NPCChatController;
                }
                landmarks = root.GetComponentsInChildren<Renderer>(true).Select(r => r.transform)
                    .Where(t => IsEventLandmark(t, title, publicJugglers)).Distinct().ToArray();
            }

            internal bool Underway => active == null || (Incident && (bool)active.GetValue(Incident));

            internal Transform Visible(NPCController resident)
            {
                var origin = resident.transform.position;
                Transform nearest = VisibleStarter(resident);
                float limit = nearest ? (nearest.position - origin).sqrMagnitude : SightRangeSquared;
                foreach (var spot in landmarks)
                {
                    if (!spot) continue;
                    float distance = (spot.position - origin).sqrMagnitude;
                    if (distance <= limit && SpeakerPerception.CanSee(resident, spot))
                    {
                        nearest = spot;
                        limit = distance;
                    }
                }
                return nearest;
            }

            Transform VisibleStarter(NPCController resident)
            {
                if (!tournament || !tournament.isActiveAndEnabled || !starter || !starter.isActiveAndEnabled ||
                    !(starter.Character is NPCController herald) || herald == resident) return null;
                // Stock Mirthwood returns the player beside the starter at the public entrance.
                // Check that the herald is still there whenever recording attendance.
                var spot = herald.transform;
                if (spot != starter.transform || spot.parent != tournament.transform ||
                    (spot.position - tournament.transform.position).sqrMagnitude > SightRangeSquared ||
                    (spot.position - resident.transform.position).sqrMagnitude > SightRangeSquared) return null;
                return SpeakerPerception.CanSee(resident, spot) ? spot : null;
            }
        }

        static bool IsEventLandmark(Transform spot, string title, Transform publicJugglers)
        {
            string name = spot.name;
            switch (title)
            {
                case Joust:
                    // The mounted track is separate from the two performers at the public Brightoak grounds.
                    return name == "NPC Jouster" || name.StartsWith("fenceJousting", StringComparison.Ordinal) ||
                           name.Contains("Joust Dummy") || (publicJugglers && spot.parent == publicJugglers &&
                           (name == "Dummy Juggler" || name == "Dummy Juggler (2)"));
                case "Traveling Merchant": return name == "Shop Character";
                case "Trade Ship": return name.StartsWith("Dummy Trader", StringComparison.Ordinal);
                case "Town Raid": return name == "banditFlag" || name == "Bandit" || name == "Bandit 2";
                case "Vermin Plague": return name == "ratNest";
                case "Werewolf sighting": return name == "Werewolf";
                case "Yuletide": return name.IndexOf("yuletide", StringComparison.OrdinalIgnoreCase) >= 0;
                case "All Hallows Eve":
                    if (name.StartsWith("scarecrowPumpkin", StringComparison.Ordinal) || name.StartsWith("ghost", StringComparison.Ordinal))
                        return true;
                    break;
            }
            // These are the native festival-specific performers and exhibitors. Each
            // celebration can have simultaneous decorations in several settlements.
            return Festivals.Contains(title) && (name.StartsWith("Dummy Band", StringComparison.Ordinal) ||
                name.StartsWith("Dummy Juggler", StringComparison.Ordinal) || name == "Dummy Vendor" ||
                name.StartsWith("flagYellow", StringComparison.Ordinal) || name.StartsWith("flowerPot", StringComparison.Ordinal));
        }

        static Venue VenueFor(Story story)
        {
            if (!(story is SpawnPrefabStory) || !story.eventData) return null;
            var root = Spawned?.GetValue(story) as GameObject;
            if (!root || !root.activeInHierarchy || !root.scene.IsValid()) return null;
            if (venues.TryGetValue(root, out var venue)) return venue;
            string title = story.eventData.title;
            if (Festivals.Contains(title) || title == Joust || title == "Trade Ship" || title == "Traveling Merchant")
                venue = new Venue(root, title, null, null);
            else if (title == "Full Moon" && story.eventData.name == "Event Full Moon")
                venue = new Venue(root, "Werewolf sighting", null, null);
            // These stories have an untitled wrapper. The public incident is active only
            // after the spawned controller starts it and until that controller ends it.
            else if (string.IsNullOrEmpty(title))
            {
                var raid = root.GetComponentInChildren<BanditRaidController>();
                var plague = root.GetComponentInChildren<VerminPlagueController>();
                if (raid && RaidActive != null) venue = new Venue(root, "Town Raid", raid, RaidActive);
                else if (plague && PlagueActive != null) venue = new Venue(root, "Vermin Plague", plague, PlagueActive);
            }
            venues[root] = venue;
            return venue;
        }

        static List<Venue> ActiveVenues()
        {
            foreach (var root in venues.Keys.Where(x => !x).ToArray()) venues.Remove(root);
            var teller = Teller;
            var result = new List<Venue>();
            if (!teller) return result;
            foreach (var active in teller.GetAllActiveStories())
            {
                if (active?.story == null || previousTimeline.Contains(active) || active.startTime > DayNightCycle.Instance.Now) continue;
                var venue = VenueFor(active.story);
                if (venue != null && venue.Underway && !result.Contains(venue)) result.Add(venue);
            }
            return result;
        }

        const string BoardsKey = "mirthwood.echoes.readablePosters";
        static readonly FieldInfo BoardOption = AccessTools.Field(typeof(CalendarContextMenu), "option");

        // The small index is part of the native save. Reloading an earlier or different
        // slot restores its own witnesses, its own posters and its own native cue ancestry.
        static JObject ReadIndex(string key)
        {
            string raw = SaveMaster.GetString(key);
            if (!string.IsNullOrEmpty(raw))
            {
                try { return JObject.Parse(raw); }
                catch (JsonException) { }
            }
            return new JObject();
        }

        static JObject TodayIndex(int day)
        {
            var index = ReadIndex(SaveKey);
            return index["day"]?.Type == JTokenType.Integer && (long)index["day"] == day && index["people"] is JObject ? index :
                new JObject { ["day"] = day, ["people"] = new JObject() };
        }

        static string Actor(NPCController speaker)
        {
            if (!speaker || !speaker.isActiveAndEnabled || speaker.IsDead || string.IsNullOrEmpty(speaker.CharacterInfo?.id))
                return null;
            var owner = Plugin.Owner(speaker);
            return string.IsNullOrEmpty(owner) ? null : owner + "\n" + speaker.CharacterInfo.id;
        }

        static JObject Child(JObject parent, string key)
        {
            if (!(parent[key] is JObject child)) parent[key] = child = new JObject();
            return child;
        }

        static bool Reached(JToken value, float now) =>
            value != null && (value.Type == JTokenType.Float || value.Type == JTokenType.Integer) && (float)value <= now;

        static bool SameDay(JObject entry, DayNightCycle clock) =>
            Reached(entry["at"], clock.Now) && Mathf.FloorToInt((float)entry["at"]) == Mathf.FloorToInt(clock.Now);

        static bool VisiblePoster(JObject snapshot, string name) =>
            (snapshot["sights"] as JArray)?.Values<string>().Contains(name) == true ||
            (snapshot["landmarks"] as JArray)?.OfType<JObject>().Any(l => (string)l["name"] == name) == true;

        internal static void AddTo(JObject snapshot, NPCController speaker, bool knowsBrightoak)
        {
            var clock = DayNightCycle.Instance;
            if (!clock || !SaveMaster.IsSlotLoaded() || snapshot == null) return;
            int day = Mathf.FloorToInt(clock.Now);
            int season = Array.IndexOf(Seasons, clock.CurrentSeason);
            snapshot["day"] = day;
            if (season < 0 || clock.YearLength < 4) return;
            snapshot["calendar"] = new JObject
            {
                ["season"] = clock.SeasonLocalized, ["seasonIndex"] = season, ["dayOfSeason"] = clock.DayOfSeason,
                ["year"] = clock.YearNumber, ["timeOfDay"] = clock.TimeOfDayString
            };
            int seasonLength = clock.YearLength / 4;
            if (clock.YearLength % 4 == 0 && clock.DayNumber >= 0 && clock.YearNumber >= 0 &&
                clock.DayNumber / seasonLength == season && clock.DayNumber % seasonLength + 1 == clock.DayOfSeason &&
                (long)clock.YearNumber * clock.YearLength + clock.DayNumber == day)
                snapshot["calendar"]["daysInSeason"] = seasonLength;
            var today = new SortedDictionary<string, JObject>(StringComparer.Ordinal);
            string actor = Actor(speaker);
            if (actor != null)
            {
                var boards = knowsBrightoak ? null : ReadIndex(BoardsKey)[actor] as JObject;
                bool festival = knowsBrightoak || VisiblePoster(snapshot, "festival poster") || Reached(boards?["Events"], clock.Now);
                bool joust = knowsBrightoak || VisiblePoster(snapshot, "joust poster") || Reached(boards?["Jousting"], clock.Now);
                if (festival || joust) AddNotices(snapshot, today, clock, season, knowsBrightoak, festival, joust);

                var earlier = TodayIndex(day)["people"][actor] as JObject;
                if (earlier != null)
                {
                    foreach (var record in earlier.Properties())
                    {
                        if (!(record.Value is JObject entry) || !SameDay(entry, clock)) continue;
                        bool ended = (string)entry["status"] == "ended";
                        string source = (string)entry["source"];
                        if (source != "firsthand" && source != "public announcement") continue;
                        var item = Event(record.Name, ended ? "ended" : "active", source, season, clock.DayOfSeason, clock.YearNumber,
                            (string)entry["place"]);
                        if (!ended) item["when"] = "earlier today";
                        today[record.Name] = item;
                    }
                }
                foreach (var venue in ActiveVenues())
                {
                    if (!venue.Visible(speaker)) continue;
                    string place = (snapshot["place"] as JObject)?.Value<string>("name");
                    today[venue.Title] = Event(venue.Title, "active", "firsthand", season, clock.DayOfSeason, clock.YearNumber, place);
                }
                AddWeatherEvents(snapshot, today, clock, season);
            }
            snapshot["today"] = new JArray(today.Values);
        }

        static void AddWeatherEvents(JObject snapshot, IDictionary<string, JObject> today, DayNightCycle clock, int season)
        {
            // Weather is populated only from the speaker's own open-sky region. A global
            // story card, a coming night or an indoor observer cannot establish a sighting.
            var weather = snapshot.Value<string>("weather");
            // The shipped BlightStory produces Dust Storm weather. It does not tell a
            // resident what happened to the player's crops. Full Moon uses ordinary Storm
            // weather, which provides no evidence that the resident can see a full moon.
            if (weather == "Dust Storm")
                today[weather] = Event(weather, "active", "firsthand", season, clock.DayOfSeason, clock.YearNumber);
        }

        sealed class Board
        {
            internal string Type;
            internal Transform Paper;
        }

        static Board[] PresentBoards()
        {
            var result = new List<Board>();
            foreach (var menu in SpeakerPerception.CalendarPosters())
            {
                if (!menu || !menu.isActiveAndEnabled) continue;
                string type = BoardOption?.GetValue(menu)?.ToString();
                if (type != "Events" && type != "Jousting") continue;
                var paper = menu.GetComponentInChildren<SpriteRenderer>();
                if (paper && paper.enabled) result.Add(new Board { Type = type, Paper = paper.transform });
            }
            return result.ToArray();
        }

        static bool LearnBoards(Board[] boards, NPCController speaker, string actor, float now, JObject readings)
        {
            bool changed = false;
            foreach (var board in boards)
            {
                var known = readings[actor] as JObject;
                if (Reached(known?[board.Type], now) || (speaker.transform.position - board.Paper.position).sqrMagnitude > 81f ||
                    !SpeakerPerception.CanSee(speaker, board.Paper)) continue;
                Child(readings, actor)[board.Type] = now;
                changed = true;
            }
            return changed;
        }

        internal static void ObserveNearby()
        {
            try
            {
                var clock = DayNightCycle.Instance;
                var player = PlayerCharacterController.Instance;
                if (!InteractionHooks.Compatible || !clock || !player || !SaveMaster.IsSlotLoaded()) return;
                var active = ActiveVenues();
                var boards = PresentBoards();
                if (active.Count == 0 && boards.Length == 0) return;
                int day = Mathf.FloorToInt(clock.Now);
                var readings = ReadIndex(BoardsKey);
                var index = TodayIndex(day);
                var people = (JObject)index["people"];
                bool changed = false, learned = false;
                foreach (var speaker in TagSystem.AllGameObjectsForTag<NPCController>("npc"))
                {
                    string actor = Actor(speaker);
                    if (actor == null) continue;
                    learned |= LearnBoards(boards, speaker, actor, clock.Now, readings);
                    foreach (var venue in active)
                    {
                        var old = people[actor]?[venue.Title] as JObject;
                        bool alreadySeen = old != null && SameDay(old, clock) &&
                            (string)old["status"] == "active" && (string)old["source"] == "firsthand";
                        if (alreadySeen && (bool?)old["withPlayer"] == true) continue;
                        var visible = venue.Visible(speaker);
                        if (!visible) continue;
                        bool withPlayer = (player.transform.position - visible.position).sqrMagnitude <= 16f &&
                            (speaker.transform.position - visible.position).sqrMagnitude <= 16f &&
                            SpeakerPerception.CanSee(speaker, player.transform);
                        string playerName = player.CharacterInfo?.name;
                        withPlayer &= !string.IsNullOrWhiteSpace(playerName);
                        if (alreadySeen && !withPlayer) continue;
                        string fact = venue.Title == "Werewolf sighting" ? "I saw a werewolf." : "I saw " + venue.Title + " underway.";
                        if (withPlayer) fact += " I saw " + playerName + " there.";
                        var details = new JObject { ["title"] = venue.Title, ["day"] = day, ["status"] = "active",
                            ["source"] = "firsthand", ["sawPlayer"] = withPlayer };
                        if (withPlayer) details["player"] = player.CharacterInfo.id;
                        if (NativeCues.Fact(speaker, "worldEvent", fact, false, details) == null) continue;
                        Child(people, actor)[venue.Title] = new JObject { ["at"] = clock.Now, ["status"] = "active",
                            ["source"] = "firsthand", ["withPlayer"] = withPlayer };
                        changed = true;
                    }
                }
                if (learned) SaveMaster.SetString(BoardsKey, readings.ToString(Formatting.None));
                if (changed) SaveMaster.SetString(SaveKey, index.ToString(Formatting.None));
            }
            catch (Exception e) { Debug.LogWarning("Echoes could not record a resident's event observation. " + e.GetBaseException().Message); }
        }

        // Called by the native barks hook only after the appropriate incident coroutine
        // selected the speaker and the overhead UI displayed that exact warning.
        internal static void Announced(NPCController speaker, string barkType)
        {
            try
            {
                string title = barkType == "BanditRaid" ? "Town Raid" : barkType == "VerminPlague" ? "Vermin Plague" : null;
                var clock = DayNightCycle.Instance;
                string actor = Actor(speaker);
                if (title == null || !clock || actor == null || !SaveMaster.IsSlotLoaded()) return;
                var index = TodayIndex(Mathf.FloorToInt(clock.Now));
                var records = Child((JObject)index["people"], actor);
                if (records[title] is JObject prior && SameDay(prior, clock) && (string)prior["status"] == "active") return;
                records[title] = new JObject { ["at"] = clock.Now, ["status"] = "active", ["source"] = "public announcement" };
                SaveMaster.SetString(SaveKey, index.ToString(Formatting.None));
            }
            catch (Exception e) { Debug.LogWarning("Echoes could not retain the public warning. " + e.GetBaseException().Message); }
        }

        // Snapshot direct witnesses while the native venue still exists. Apply the end
        // only after the native method returned successfully; absence or a failed start
        // cannot establish an end or cancellation.
        sealed class Ending
        {
            internal string Title, Session;
            internal int Day;
            internal Dictionary<string, NPCController> Witnesses;
        }

        static Ending Witnesses(Venue venue)
        {
            var clock = DayNightCycle.Instance;
            if (venue == null || !venue.Underway || !clock || !SaveMaster.IsSlotLoaded()) return null;
            var witnesses = new Dictionary<string, NPCController>(StringComparer.Ordinal);
            foreach (var npc in TagSystem.AllGameObjectsForTag<NPCController>("npc"))
            {
                string actor = Actor(npc);
                if (actor != null && venue.Visible(npc)) witnesses[actor] = npc;
            }
            return witnesses.Count == 0 ? null : new Ending { Title = venue.Title, Session = NativeCues.Session,
                Day = Mathf.FloorToInt(clock.Now), Witnesses = witnesses };
        }

        static void Ended(Ending ending)
        {
            var clock = DayNightCycle.Instance;
            if (ending == null || !clock || !SaveMaster.IsSlotLoaded() || ending.Session != NativeCues.Session ||
                ending.Day != Mathf.FloorToInt(clock.Now)) return;
            try
            {
                var index = TodayIndex(ending.Day);
                foreach (var witness in ending.Witnesses)
                {
                    var records = Child((JObject)index["people"], witness.Key);
                    if (records[ending.Title] is JObject prior && SameDay(prior, clock) && (string)prior["status"] == "ended") continue;
                    var details = new JObject { ["title"] = ending.Title, ["day"] = ending.Day, ["status"] = "ended",
                        ["source"] = "firsthand" };
                    string fact = ending.Title == "Werewolf sighting" ? "The werewolf I saw was gone." : "I saw " + ending.Title + " end.";
                    NativeCues.Fact(witness.Value, "worldEvent", fact, false, details);
                    records[ending.Title] = new JObject { ["at"] = clock.Now, ["status"] = "ended", ["source"] = "firsthand" };
                }
                SaveMaster.SetString(SaveKey, index.ToString(Formatting.None));
            }
            catch (Exception e) { Debug.LogWarning("Echoes could not retain the end of the event. " + e.GetBaseException().Message); }
        }

        [HarmonyPatch(typeof(Storyteller), "EndStory", typeof(Storyteller.ActiveStory))]
        static class StoryEnding
        {
            static void Prefix(Storyteller.ActiveStory __0, out Ending __state)
            {
                __state = null;
                try { if (__0 != null && !previousTimeline.Contains(__0)) __state = Witnesses(VenueFor(__0.story)); }
                catch (Exception e) { Debug.LogWarning("Echoes could not observe the ending story. " + e.GetBaseException().Message); }
            }
            static void Postfix(Ending __state) => Ended(__state);
        }

        [HarmonyPatch]
        static class IncidentEnding
        {
            static IEnumerable<MethodBase> TargetMethods()
            {
                yield return AccessTools.Method(typeof(BanditRaidController), "SetQuestInactive");
                yield return AccessTools.Method(typeof(VerminPlagueController), "SetQuestInactive");
            }
            static void Prefix(Component __instance, out Ending __state)
            {
                __state = null;
                try { __state = Witnesses(ActiveVenues().FirstOrDefault(v => v.Incident == __instance)); }
                catch (Exception e) { Debug.LogWarning("Echoes could not observe the ending incident. " + e.GetBaseException().Message); }
            }
            static void Postfix(Ending __state) => Ended(__state);
        }

        [HarmonyPatch]
        static class StoryLoad
        {
            static IEnumerable<MethodBase> TargetMethods()
            {
                yield return AccessTools.Method(typeof(SaveMaster), nameof(SaveMaster.SyncLoad));
                yield return AccessTools.Method(typeof(Storyteller), nameof(Storyteller.OnLoad));
            }

            static void Prefix()
            {
                // Native LoadStories appends fresh ActiveStory objects after a delay.
                // An old controller can still hold the preceding slot's list meanwhile.
                previousTimeline.Clear();
                var teller = Teller;
                if (teller) previousTimeline.UnionWith(teller.GetAllActiveStories());
                venues.Clear();
                notices = null;
            }
        }

        internal static void Install(Harmony hooks)
        {
            foreach (var type in new[] { typeof(StoryEnding), typeof(IncidentEnding), typeof(StoryLoad) })
            {
                try { hooks.CreateClassProcessor(type).Patch(); }
                catch (Exception e) { Debug.LogWarning("Echoes could not observe " + type.Name + ". " + e.GetBaseException().Message); }
            }
        }
    }
}
