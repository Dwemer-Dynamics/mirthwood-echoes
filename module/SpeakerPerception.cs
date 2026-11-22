using System;
using System.Collections.Generic;
using System.Linq;
using CleverCrow.Fluid.BTs.Tasks;
using CleverCrow.Fluid.BTs.Trees;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.SceneManagement;
using _Mirthwood.Scripts.Demo;
using _Mirthwood.Scripts.Demo.AI;
using _Mirthwood.Scripts.Demo.Chat;
using _Mirthwood.Scripts.Demo.Combat;
using _Mirthwood.Scripts.Demo.Menus;
using _Mirthwood.Scripts.Demo.ScriptableObjects;
using _Mirthwood.Scripts.Demo.Triggers;
using _Mirthwood.Scripts.Demo.Weather;
using _Mirthwood.Scripts.Localization;
using _Mirthwood.Scripts.Runtime.Demo.Utils;
using Object = UnityEngine.Object;

namespace Mirthwood.Echoes
{
    internal static class SpeakerPerception
    {
        const float SightDistance = 9f;
        static readonly System.Reflection.FieldInfo ActiveTree = AccessTools.Field(typeof(NPCController), "_activeTreeRef");
        static readonly System.Reflection.FieldInfo RoutineTree = AccessTools.Field(typeof(NPCController), "_behaviorTree");
        static readonly System.Reflection.FieldInfo CalendarOption = AccessTools.Field(typeof(CalendarContextMenu), "option");
        static readonly System.Reflection.FieldInfo IsWorldRegion = AccessTools.Field(typeof(RegionTrigger), "isWorld");
        static readonly System.Reflection.FieldInfo Tavern = AccessTools.Field(typeof(NPCController), "tavernLocation");
        static Geometry geometry;
        static string playSession;

        static SpeakerPerception()
        {
            SceneManager.sceneLoaded += (_, __) => geometry = null;
            SceneManager.sceneUnloaded += _ => geometry = null;
        }

        // Cache authored architecture only. Each observation reads live collider membership,
        // enabled objects, relationships, weather and the two subjects' actual positions.
        sealed class Geometry
        {
            internal readonly DoorTrigger[] Doors = Object.FindObjectsOfType<DoorTrigger>(true);
            internal readonly CalendarContextMenu[] Posters = Object.FindObjectsOfType<CalendarContextMenu>(true);
            internal readonly List<RoomFloor> Floors = new List<RoomFloor>();
            internal readonly Dictionary<DoorTrigger, Transform> Buildings = new Dictionary<DoorTrigger, Transform>();

            internal Geometry()
            {
                var indexed = new HashSet<Transform>();
                foreach (var door in Doors) {
                    for (var building = door.transform.parent; building; building = building.parent) {
                        var interior = building.Find("Interior");
                        if (!interior) continue;
                        Buildings.Add(door, building);
                        if (indexed.Add(building)) {
                            AddFloors(building, interior);
                            var upper = building.Find("Upper Floor");
                            if (upper) AddFloors(building, upper);
                        }
                        break;
                    }
                }
            }

            void AddFloors(Transform building, Transform level)
            {
                foreach (var renderer in level.GetComponentsInChildren<SpriteRenderer>(true)) {
                    if (!renderer.sprite || !string.Equals(renderer.name, "floor", StringComparison.OrdinalIgnoreCase) ||
                        Mathf.Abs(Vector3.Dot(renderer.transform.forward, Vector3.up)) < .98f) continue;
                    Floors.Add(new RoomFloor(building, level, renderer));
                }
            }
        }

        sealed class RoomFloor
        {
            internal readonly Transform Building, Level;
            readonly SpriteRenderer floor;
            Sprite shape;
            Vector2[] vertices;
            ushort[] triangles;

            internal RoomFloor(Transform building, Transform level, SpriteRenderer renderer)
            {
                Building = building;
                Level = level;
                floor = renderer;
            }

            internal bool Contains(Vector3 feet)
            {
                if (!floor || !floor.sprite || !Building || !Building.gameObject.activeInHierarchy ||
                    Mathf.Abs(feet.y - floor.transform.position.y) > .85f) return false;
                var local = floor.transform.InverseTransformPoint(feet);
                var point = new Vector2(floor.flipX ? -local.x : local.x, floor.flipY ? -local.y : local.y);
                // Native interiors can switch their presentation with the player's camera.
                // Their horizontal floor sprite is an authored triangulated footprint (both
                // Brightoak inns have concave cutouts). A renderer's rectangular bounds alone
                // include the street. Do not depend on a player's current interior visibility.
                var box = floor.sprite.bounds;
                if (point.x < box.min.x || point.x > box.max.x || point.y < box.min.y || point.y > box.max.y) return false;
                if (shape != floor.sprite) {
                    shape = floor.sprite;
                    vertices = shape.vertices;
                    triangles = shape.triangles;
                }
                for (int i = 0; i + 2 < triangles.Length; i += 3) {
                    var a = vertices[triangles[i]];
                    var b = vertices[triangles[i + 1]];
                    var c = vertices[triangles[i + 2]];
                    if (Mathf.Abs(Cross(a, b, c)) < .000001f) continue;
                    float first = Cross(a, b, point), second = Cross(b, c, point), third = Cross(c, a, point);
                    if (first >= 0 && second >= 0 && third >= 0 || first <= 0 && second <= 0 && third <= 0) return true;
                }
                return false;
            }

            static float Cross(Vector2 a, Vector2 b, Vector2 p) => (b.x-a.x)*(p.y-a.y)-(b.y-a.y)*(p.x-a.x);
        }

        static Geometry WorldGeometry()
        {
            if (geometry == null || playSession != NativeCues.Session) {
                geometry = new Geometry();
                playSession = NativeCues.Session;
            }
            return geometry;
        }

        internal static IEnumerable<CalendarContextMenu> CalendarPosters() => WorldGeometry().Posters;

        readonly struct Surroundings
        {
            internal readonly bool? Interior;
            internal readonly RoomFloor Room;
            internal Surroundings(bool? interior, RoomFloor room = null) { Interior = interior; Room = room; }
        }

        static Surroundings LocateFloor(Vector3 feet, Geometry world)
        {
            RoomFloor room = null;
            foreach (var floor in world.Floors) {
                if (!floor.Contains(feet)) continue;
                if (room != null && room.Level != floor.Level) return default;
                room = floor;
            }
            foreach (var door in world.Doors) {
                if (!door || !door.isActiveAndEnabled) continue;
                var delta = feet - door.transform.position;
                if (delta.sqrMagnitude > 144f) continue;
                float side = Vector3.Dot(delta, door.transform.forward);
                var portal = door.GetComponent<Collider>();
                if (Mathf.Abs(side) < .12f && portal && portal.bounds.Contains(feet + Vector3.up * .5f)) return default;
                // A doorway without a matching floor cannot establish where its room ends.
                if (room == null && side > 0 && (!world.Buildings.TryGetValue(door, out var building) ||
                    !world.Floors.Any(f => f.Building == building))) return default;
            }
            if (room != null) return new Surroundings(true, room);
            float? terrain = TerrainHelper.GetTerrainHitPoint(feet);
            if (!terrain.HasValue || Mathf.Abs(terrain.Value - feet.y) > .85f) return default;
            return new Surroundings(false);
        }

        static bool Living(NPCController speaker) =>
            speaker && speaker.isActiveAndEnabled && !speaker.IsDead && speaker.CharacterInfo != null;

        internal static JObject Capture(NPCController speaker)
        {
            var result = new JObject();
            if (!Living(speaker)) return result;
            var world = WorldGeometry();
            var feet = speaker.transform.position;
            var surroundings = LocateFloor(feet, world);
            // These native triggers normally update the player's HUD. Query their actual
            // colliders at this resident's feet without dispatching discovery or changing saves.
            var volumes = Physics.OverlapSphere(feet, .01f, Physics.AllLayers, QueryTriggerInteraction.Collide)
                .Where(c => c.isTrigger && Contains(c, feet)).ToArray();
            var points = volumes.Select(c => c.GetComponent<PointOfInterestTrigger>()).Where(t => t && t.isActiveAndEnabled).Distinct().ToArray();
            string point = UniqueName(points.Where(t => !(t is RegionTrigger)));
            var regionTriggers = points.OfType<RegionTrigger>().ToArray();
            string region = IsWorldRegion == null ? UniqueName(regionTriggers) :
                UniqueName(regionTriggers.Where(t => !(bool)IsWorldRegion.GetValue(t))) ??
                UniqueName(regionTriggers.Where(t => (bool)IsWorldRegion.GetValue(t)));
            var place = new JObject();
            string venue = PublicVenue(surroundings.Room?.Building);
            if (venue != null) place["name"] = venue;
            else if (point != null) place["name"] = LocalizationManager.GetLocalizedStringUI(point);
            if (region != null) place["region"] = LocalizationManager.GetLocalizedStringUI(region);
            if (surroundings.Interior.HasValue) place["interior"] = surroundings.Interior.Value;
            if (place.HasValues) result["place"] = place;

            if (surroundings.Interior == false && OpenSky(feet, speaker.transform)) {
                var regions = volumes.Select(c => c.GetComponent<Region>()).Where(r => r && r.isActiveAndEnabled).Distinct().Take(2).ToArray();
                var condition = regions.Length == 1 ? regions[0].CurrentWeatherCondition : null;
                // Mirthwood uses "Sun" for its fair-weather condition even at night.
                if (condition && !string.IsNullOrWhiteSpace(condition.Type))
                    result["weather"] = condition.Type == "Sun" ? "clear weather" : condition.Type;
            }
            var sights = new HashSet<string>(StringComparer.Ordinal);
            CaptureCompany(speaker, world, surroundings, result, sights);
            CaptureLandmarks(speaker, world, surroundings, sights, result);
            if (sights.Count > 0) result["sights"] = new JArray(sights.OrderBy(s => s, StringComparer.Ordinal).Take(6));
            return result;
        }

        static bool Contains(Collider volume, Vector3 position)
        {
            // Stock place and weather volumes are boxes, some rotated or non-uniformly
            // scaled. Exactly on a shared boundary the resident has no single verified side.
            if (volume is BoxCollider box) {
                var point = box.transform.InverseTransformPoint(position) - box.center;
                var half = box.size * .5f;
                var scale = box.transform.lossyScale;
                return (half.x - Mathf.Abs(point.x)) * Mathf.Abs(scale.x) > .01f &&
                    (half.y - Mathf.Abs(point.y)) * Mathf.Abs(scale.y) > .01f &&
                    (half.z - Mathf.Abs(point.z)) * Mathf.Abs(scale.z) > .01f;
            }
            if (!(volume is CapsuleCollider || volume is SphereCollider || volume is MeshCollider mesh && mesh.convex)) return false;
            return (volume.ClosestPoint(position) - position).sqrMagnitude < .000001f;
        }

        static string UniqueName(IEnumerable<PointOfInterestTrigger> triggers)
        {
            string[] names = triggers.Select(t => t.name).Where(s => !string.IsNullOrWhiteSpace(s)).Distinct().Take(2).ToArray();
            return names.Length == 1 ? names[0] : null;
        }

        static string PointNameAt(Vector3 feet)
        {
            var locals = Physics.OverlapSphere(feet, .01f, Physics.AllLayers, QueryTriggerInteraction.Collide)
                .Where(c => c.isTrigger && Contains(c, feet)).Select(c => c.GetComponent<PointOfInterestTrigger>())
                .Where(t => t && t.isActiveAndEnabled && !(t is RegionTrigger));
            return UniqueName(locals);
        }

        internal static bool KnowsBrightoak(NPCController speaker) =>
            Living(speaker) && speaker.CharacterInfo.town == CharacterData.Character.Towns.Brightoak;

        static bool OpenSky(Vector3 feet, Transform actor)
        {
            // Include an overhead obstruction even when it belongs to the same building as
            // the ground. Current outdoor weather is never promoted to evidence of indoor sound.
            return !Physics.RaycastAll(feet + Vector3.up * 1.2f, Vector3.up, 15f, Physics.AllLayers,
                QueryTriggerInteraction.Ignore).Any(h => !h.transform.IsChildOf(actor));
        }

        static string PublicVenue(Transform building)
        {
            if (!building) return null;
            switch (building.name) {
                case "Hazy Hag Tavern": case "Bog & Briar Inn": return building.name;
                default: return null;
            }
        }

        static void CaptureCompany(NPCController speaker, Geometry world, Surroundings surroundings, JObject result, HashSet<string> sights)
        {
            var nearby = Physics.OverlapSphere(speaker.transform.position, SightDistance, Physics.AllLayers, QueryTriggerInteraction.Collide);
            var names = new HashSet<string>(StringComparer.Ordinal);
            var seen = new HashSet<Component>();
            NPCController[] population = null;
            foreach (var hit in nearby) {
                var person = hit.GetComponentInParent<Character>();
                if (person) {
                    if (person == speaker || person == PlayerCharacterController.Instance || !seen.Add(person)) continue;
                    var npc = person as NPCController;
                    if (npc ? !Living(npc) : !(person is PlayerCharacterController)) continue;
                    if (!Visible(speaker, person.transform, world, surroundings)) continue;
                    result["bystandersPresent"] = true;
                    if (npc && Recognizes(speaker, npc)) {
                        if (population == null) population = Object.FindObjectsOfType<NPCController>(true);
                        var info = npc.CharacterInfo;
                        // Relationship keys are native IDs. Some saves have duplicate IDs
                        // on distinct controllers. A bystander is still visible in that case.
                        if (!population.Any(p => p != npc && p.CharacterInfo != null &&
                            (p.CharacterInfo.id == info.id || string.Equals(p.CharacterInfo.name, info.name, StringComparison.OrdinalIgnoreCase))))
                            names.Add(info.name);
                    }
                    continue;
                }
                var animal = hit.GetComponentInParent<Animal>();
                if (!animal || !animal.AnimalData || !seen.Add(animal)) continue;
                var body = animal.GetComponentInChildren<MeshRenderer>();
                if (body && Visible(speaker, body.transform, world, surroundings)) {
                    string label = animal.AnimalData.ItemNameLocalized;
                    if (!string.IsNullOrWhiteSpace(label)) sights.Add(label);
                }
            }
            if (names.Count > 0) result["nearbyPeople"] = new JArray(names.OrderBy(s => s, StringComparer.Ordinal).Take(8));
        }

        static bool Recognizes(NPCController speaker, NPCController other)
        {
            var who = other.CharacterInfo;
            var relationships = speaker.CharacterInfo.relationships;
            if (relationships == null || string.IsNullOrWhiteSpace(who.id) || string.IsNullOrWhiteSpace(who.name)) return false;
            if (relationships.GetSpouse() == who.id) return true;
            relationships.GetRelationship(who.id, out float friendship, out float romance);
            return friendship != 0 || romance != 0;
        }

        internal static bool CanSee(NPCController speaker, Transform subject)
        {
            if (!Living(speaker) || !subject) return false;
            var world = WorldGeometry();
            return Visible(speaker, subject, world, LocateFloor(speaker.transform.position, world));
        }

        static bool Visible(NPCController speaker, Transform subject, Geometry world, Surroundings from)
        {
            if (!subject || !subject.gameObject.activeInHierarchy || !from.Interior.HasValue) return false;
            if (speaker.gameObject.scene.GetPhysicsScene() != subject.gameObject.scene.GetPhysicsScene()) return false;
            var actor = subject.GetComponentInParent<Character>();
            var renderer = subject.GetComponent<Renderer>();
            if (actor && subject == actor.transform) {
                var visual = actor.Visuals?.Data;
                var body = visual?.SkeletonAnimation;
                if (!body || !body.isActiveAndEnabled || visual.Skeleton == null || visual.Skeleton.A <= .05f) return false;
                renderer = body.GetComponent<MeshRenderer>();
            }
            if (!renderer || !renderer.enabled || !renderer.gameObject.activeInHierarchy || renderer.forceRenderingOff ||
                renderer is SpriteRenderer sprite && (!sprite.sprite || sprite.color.a <= .05f)) return false;
            if (renderer is MeshRenderer) {
                var filter = renderer.GetComponent<MeshFilter>();
                if (!filter || !filter.sharedMesh || filter.sharedMesh.vertexCount == 0) return false;
            } else if (!(renderer is SpriteRenderer)) return false;
            var feet = actor ? actor.transform.position : subject.position;
            if (!actor) {
                // A roof or sign above a room can project onto its floor. Only credit
                // an indoor furnishing when the scene actually places it in that room.
                if (from.Interior == true && !subject.IsChildOf(from.Room.Level)) return false;
                float? ground = TerrainHelper.GetTerrainHitPoint(feet);
                if (ground.HasValue && feet.y >= ground.Value && feet.y - ground.Value < 3f) feet.y = ground.Value;
            }
            var to = LocateFloor(feet, world);
            if (!to.Interior.HasValue || from.Interior != to.Interior || from.Room?.Level != to.Room?.Level) return false;
            if (actor) {
                var first = speaker.GetComponentInChildren<FloorLayerManager>();
                var second = actor.GetComponentInChildren<FloorLayerManager>();
                if (first && second && first.CurrentLayer != second.CurrentLayer) return false;
            }
            var eye = speaker.transform.position + Vector3.up * .95f;
            var target = actor ? actor.transform.position + Vector3.up * .95f : renderer.bounds.center;
            var ray = target - eye;
            if (ray.sqrMagnitude > SightDistance * SightDistance || Mathf.Abs(feet.y - speaker.transform.position.y) > 1.4f) return false;
            var data = speaker.DataContainer?.data;
            if (data == null) return false;
            var facing = data.FacingDirection;
            if (facing.x*facing.x + facing.z*facing.z < .01f || facing.x*ray.x + facing.z*ray.z < 0) return false;
            if (ray.sqrMagnitude < .0001f) return true;
            var owner = actor ? actor.transform : subject;
            return !Physics.RaycastAll(eye, ray.normalized, ray.magnitude, Physics.AllLayers, QueryTriggerInteraction.Ignore)
                .Any(h => !h.transform.IsChildOf(speaker.transform) && !h.transform.IsChildOf(owner));
        }

        static void CaptureLandmarks(NPCController speaker, Geometry world, Surroundings surroundings, HashSet<string> sights, JObject result)
        {
            var destinations = new Dictionary<string, Vector3>(StringComparer.Ordinal);
            var origin = speaker.transform.position;
            string homeTown = speaker.CharacterInfo.town.ToString();
            // The resident's own native routine navigates to this assigned public tavern.
            // Its destination belongs to this NPC even when someone else's route differs.
            if (Tavern?.GetValue(speaker) is _Mirthwood.Scripts.Demo.Utils.Location tavern && tavern && tavern.isActiveAndEnabled) {
                var position = tavern.transform.position;
                if ((position-origin).sqrMagnitude <= 120f * 120f) {
                    string name = PublicVenue(LocateFloor(position, world).Room?.Building) ?? "tavern";
                    Add(name, position);
                }
            }
            foreach (var poster in world.Posters) {
                if (!poster || !poster.isActiveAndEnabled || (poster.transform.position-origin).sqrMagnitude > 120f * 120f) continue;
                string option = CalendarOption?.GetValue(poster)?.ToString();
                string label = option == "Jousting" ? "joust poster" : option == "Events" ? "festival poster" : null;
                if (label == null) continue;
                var paper = poster.GetComponentInChildren<SpriteRenderer>();
                bool visible = paper && (poster.transform.position - origin).sqrMagnitude <= SightDistance * SightDistance &&
                    Visible(speaker, paper.transform, world, surroundings);
                if (visible) sights.Add(label);
                if (visible || PointNameAt(poster.transform.position) == homeTown) Add(label, poster.transform.position);
            }
            foreach (var entry in world.Buildings) {
                var door = entry.Key;
                string venue = PublicVenue(entry.Value);
                if (venue == null || !door || !door.isActiveAndEnabled || (door.transform.position-origin).sqrMagnitude > 120f * 120f ||
                    PointNameAt(door.transform.position) != homeTown) continue;
                var upper = entry.Value.Find("Upper Floor");
                if (upper && door.transform.IsChildOf(upper)) continue;
                Add(venue, door.transform.position);
            }
            if (destinations.Count > 0) result["landmarks"] = new JArray(destinations.OrderBy(p => (p.Value-origin).sqrMagnitude)
                .ThenBy(p => p.Key, StringComparer.Ordinal).Take(5).Select(p => new JObject { ["name"] = p.Key, ["direction"] = Direction(origin,p.Value) }));

            void Add(string name, Vector3 point)
            {
                float distance = (point - origin).sqrMagnitude;
                if (distance > 120f * 120f || destinations.TryGetValue(name, out var previous) && (previous - origin).sqrMagnitude <= distance) return;
                destinations[name] = point;
            }
        }

        static readonly string[] Compass = { "north", "northeast", "east", "southeast", "south", "southwest", "west", "northwest" };
        static string Direction(Vector3 from, Vector3 to)
        {
            if (new Vector2(to.x-from.x, to.z-from.z).sqrMagnitude < 4f) return "here";
            var delta = MapMenuController.WorldPositionToImagePosition(to) - MapMenuController.WorldPositionToImagePosition(from);
            return Compass[(Mathf.RoundToInt(Mathf.Atan2(delta.x, delta.y) / (Mathf.PI / 4)) + 8) % 8];
        }

        internal static string InterruptedActivity(NPCController speaker)
        {
            if (!Living(speaker) || speaker.DataContainer.data.InCombat || !(ActiveTree?.GetValue(speaker) is BehaviorTree tree) ||
                !ReferenceEquals(tree, RoutineTree?.GetValue(speaker))) return null;
            // ActiveTasks contains running tasks. The NPC's displayed debug status is sampled
            // every 60 frames and includes completed work, so it cannot prove an interruption.
            var leaves = tree.ActiveTasks.Where(t => t.Enabled && t.LastStatus == TaskStatus.Continue &&
                t.Owner == speaker.gameObject && (t.Children == null || t.Children.Count == 0)).Take(2).ToArray();
            if (leaves.Length != 1) return null;
            var leaf = leaves[0];
            var agent = speaker.GetComponent<NavMeshAgent>();
            bool walking = agent && agent.isActiveAndEnabled && agent.isOnNavMesh && !agent.isStopped && agent.hasPath;
            if (walking) {
                switch (leaf.Name) {
                    case "Go Home": case "Walk Home": return "walking home";
                    case "Go to Tavern": return "on the way to the tavern";
                    case "Go To Work": case "Walk to job location": return "on the way to work";
                    case "Walk to tree": return "walking to a tree";
                    case "Walk to ore": return "heading to a mineral deposit";
                    case "Walk to crop": case "Walk to soil": return "heading to tend the fields";
                    case "Walk to herb": return "walking to gather herbs";
                    case "Walk to bed": return "going to bed";
                    case "Walk To Relax": return "heading to a place to rest";
                    case "Walk to dropped item": return "going to pick something up";
                }
            }
            switch (leaf.Name) {
                case "Relax": return "resting";
                case "Sleep": return "sleeping";
                case "Water soil": return "watering the soil";
                case "Plant seed": return "planting";
                case "Mine ore": return "mining ore";
                case "Chop tree": return "chopping wood";
                case "Harvest crop": return "harvesting a crop";
                case "Pick herb": return "gathering herbs";
            }
            if (leaf.Name == "Wait Time") {
                if (RunningWithin(tree.Root, leaf, "Eat at Tavern")) return "having a meal at the tavern";
                if (RunningWithin(tree.Root, leaf, "Eat at Home")) return "having a meal at home";
                if (RunningWithin(tree.Root, leaf, "Bake Bread")) return "baking bread";
                if (RunningWithin(tree.Root, leaf, "Sleep")) return "sleeping";
            }
            return walking && agent.velocity.sqrMagnitude > .04f ? "walking" : null;
        }

        static bool RunningWithin(ITask task, ITask leaf, string activity, bool within = false)
        {
            if (task == leaf) return within;
            if (task.Children == null) return false;
            return task.Children.Any(child => child.LastStatus == TaskStatus.Continue &&
                RunningWithin(child, leaf, activity, within || task.Name == activity));
        }
    }
}
