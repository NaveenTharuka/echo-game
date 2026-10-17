using System.Collections.Generic;
using System.Linq;
using Echo.AI;
using Echo.Interaction;
using Echo.Navigation;
using Echo.Sound;
using Unity.AI.Navigation;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AI;

namespace Echo.EditorTools
{
    /// ECHO > Agents > All Agents > Build Facility Sandbox. (Re)creates Assets/Scenes/Sandbox/Sandbox_AllAgents.unity
    /// as a COPY of SampleScene (the real map: stairwell, entrance, ramps, corridor tee, warehouse, containment, lab)
    /// and wires it for the AI. SampleScene itself is never opened or saved.
    ///
    ///   1. player  = the fully wired one from Sandbox_Interaction (noise, gun, death), at SampleScene's spawn
    ///   2. solid static geometry on layer Environment (blocks sound; floors stay on layer 8 for the player)
    ///   3. NavMesh baked with the door leaves left out (agents open doors themselves)
    ///   4. NavGraph: nodes sampled from the NavMesh every ~6 m (only what is reachable from the spawn),
    ///      a node pair on each door (Door.sideA/sideB), zones per room, auto-link, auto-tags
    ///   5. Systems (audio, music, debug overlay, sound rings, HearingVisualizer) + Listener, Tracker, Ambusher, Mimic
    public static class FacilitySandboxBuilder
    {
        public const string ScenePath = "Assets/Scenes/Sandbox/Sandbox_AllAgents.unity";
        const string SourceScene = "Assets/Scenes/SampleScene.unity";
        const float NodeSpacing = 6f;

        // Rooms of SampleScene (world-space boxes, see Docs/Map/ECHO_floor_plan_v2.png). Order matters where they
        // touch: ZoneVolume.ZoneAt returns the first match.
        static readonly (string id, Vector3 min, Vector3 max)[] Zones =
        {
            ("Stairwell",          new(20f, -8f, 57f),   new(28f, 6f, 71f)),
            ("EntranceCorridor_W", new(20f, -8f, 45f),   new(33f, 0f, 57f)),
            ("EntranceCorridor_E", new(33f, -8f, 40.5f), new(46f, 0f, 53f)),
            ("EntranceHall_W",     new(32f, -8f, 21f),   new(45f, 4f, 40.5f)),
            ("EntranceHall_E",     new(45f, -8f, 21f),   new(58f, 4f, 40.5f)),
            ("Ramp",               new(57f, -14f, 27f),  new(69f, 0f, 35f)),
            ("CorridorTee_S",      new(69f, -13f, 21f),  new(81f, -6f, 31f)),
            ("CorridorTee_N",      new(69f, -13f, 31f),  new(81f, -6f, 40f)),
            ("RampsDown",          new(74f, -26f, 40f),  new(82f, -6f, 62f)),
            ("Warehouse_W",        new(44f, -26f, 62f),  new(57f, -4f, 94f)),
            ("Warehouse_C",        new(57f, -26f, 62f),  new(69f, -4f, 94f)),
            ("Bay_S",              new(69f, -26f, 62f),  new(88f, -4f, 75f)),
            ("Bay_N",              new(69f, -26f, 75f),  new(88f, -4f, 94f)),
            ("CorridorCorner",     new(74f, -13f, 9f),   new(87f, -6f, 22f)),
            ("Passthrough",        new(87f, -13f, 9f),   new(93f, -6f, 17f)),
            ("Containment_S",      new(93f, -14f, 3f),   new(112f, -2f, 16f)),
            ("Containment_N",      new(93f, -14f, 16f),  new(112f, -2f, 29f)),
            ("LabStairs",          new(86f, -32f, 44f),  new(100f, -14f, 76f)),
            ("Lab",                new(86f, -32f, 20f),  new(107f, -20f, 44f)),
        };

        // Agent homes: Listener guards the entrance hall, the Ambusher learns at the corridor tee (every route
        // passes it), the Tracker hunts the warehouse.
        static readonly Vector3 ListenerHome = new(50f, -6f, 31f);
        static readonly Vector3 AmbusherHome = new(75f, -12f, 26f);
        static readonly Vector3 TrackerHome = new(54f, -24f, 78f);
        static readonly Vector3 MimicHome = new(102f, -14f, 22f);      // hides in containment, projects its voice

        [MenuItem("ECHO/Agents/All Agents/Build Facility Sandbox (copy of SampleScene)")]
        static void BuildMenu()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode || !EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            Build();
        }

        public static void Build()
        {
            AgentAssetBuilder.EnsureLayers();
            int env = LayerMask.NameToLayer(AgentAssetBuilder.EnvironmentLayer);

            AssetDatabase.DeleteAsset(ScenePath);
            if (!AssetDatabase.CopyAsset(SourceScene, ScenePath)) { Debug.LogError("[FacilitySandbox] copy failed"); return; }
            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);

            // 1. Player: swap SampleScene's bare capsule for the wired one, same spawn.
            var old = GameObject.Find("PlayerCapsule");
            Vector3 spawn = old ? old.transform.position : new Vector3(24f, 0.05f, 68.6f);
            Quaternion spawnRot = old ? old.transform.rotation : Quaternion.Euler(0f, 180f, 0f);
            foreach (var root in scene.GetRootGameObjects())
                if (root.name is "PlayerCapsule" or "InteractionHUD") Object.DestroyImmediate(root);

            // 2. Sound occluders.
            int moved = 0;
            foreach (var rootName in new[] { "Build", "Horror_Props" })
            {
                var root = GameObject.Find(rootName);
                if (root == null) continue;
                foreach (var c in root.GetComponentsInChildren<Collider>(true))
                {
                    if (c.isTrigger || c.gameObject.layer != 0) continue;
                    if (c.attachedRigidbody != null && !c.attachedRigidbody.isKinematic) continue;   // loose props
                    if (c.GetComponentInParent<IInteractable>() != null && c.GetComponentInParent<Door>() == null) continue;
                    c.gameObject.layer = env; moved++;
                }
            }

            // 3. NavMesh (before the player and agents exist, so they are not baked in as obstacles).
            var doors = Object.FindObjectsByType<Door>(FindObjectsSortMode.None);
            var leafColliders = doors.Where(d => d.leaf != null)
                                     .SelectMany(d => d.leaf.GetComponentsInChildren<Collider>(true))
                                     .Where(c => c.enabled).ToList();
            foreach (var c in leafColliders) c.enabled = false;
            var surface = new GameObject("NavMeshSurface").AddComponent<NavMeshSurface>();
            surface.collectObjects = CollectObjects.All;
            surface.useGeometry = NavMeshCollectGeometry.PhysicsColliders;
            surface.layerMask = (1 << 0) | (1 << 8) | (1 << env);
            surface.overrideVoxelSize = true; surface.voxelSize = 0.1f;   // 1.5 m doorways survive the agent radius
            surface.BuildNavMesh();
            foreach (var c in leafColliders) c.enabled = true;
            string navPath = ScenePath.Replace(".unity", "_NavMesh.asset");
            AssetDatabase.DeleteAsset(navPath);
            AssetDatabase.CreateAsset(surface.navMeshData, navPath);

            if (!NavMesh.SamplePosition(spawn, out var spawnHit, 4f, NavMesh.AllAreas))
            { Debug.LogError("[FacilitySandbox] spawn is not on the NavMesh"); return; }

            // 4. Zones, nodes, links.
            var zoneRoot = new GameObject("Zones").transform;
            foreach (var (id, min, max) in Zones) Zone(zoneRoot, id, min, max);

            var graph = new GameObject("NavGraph").AddComponent<NavGraph>();
            var doorNodes = new List<NavNode>();
            int n = 0;
            foreach (var d in doors) doorNodes.AddRange(DoorNodes(graph.transform, d, ref n));
            NavMesh.SamplePosition(spawn, out spawnHit, 4f, NavMesh.AllAreas);   // links are registered now

            var accepted = new List<Vector3>(doorNodes.Select(x => x.transform.position));
            foreach (var p in SampleNavMesh(spawnHit.position))
            {
                if (accepted.Any(a => Vector3.Distance(a, p) < NodeSpacing * 0.55f)) continue;
                accepted.Add(p);
                NewNode(graph.transform, ref n, p, NodeTag.Room);
            }

            NavGraphTools.AutoLink(graph);
            int cut = CutLinksThroughDoors(graph, doors);
            AutoTag(graph, doorNodes);

            // 5. Systems, player, agents.
            var systems = new GameObject("Systems");
            systems.AddComponent<AudioService>().library = SoundLibraryBuilder.Build();
            var music = systems.AddComponent<TensionMusic>();
            music.calmLoop = AssetDatabase.LoadAssetAtPath<AudioClip>("Assets/Audio/Music/DarkDrone_Loop.wav");
            music.huntLoop = AssetDatabase.LoadAssetAtPath<AudioClip>("Assets/Audio/Music/HorrorDrone_Loop.wav");
            systems.AddComponent<SoundDebug>();
            systems.AddComponent<AgentDebugOverlay>();
            systems.AddComponent<HearingVisualizer>().material = HearingMaterial();

            bool playerOk = ListenerSandboxBuilder.CopyPlayer(spawn, spawnRot);
            FixGroundLayers(env);

            var agents = new GameObject("Agents").transform;
            Place(agents, AgentAssetBuilder.Listener, ListenerHome, 90f);
            Place(agents, AgentAssetBuilder.Ambusher, AmbusherHome, 0f);
            Place(agents, AgentAssetBuilder.Tracker, TrackerHome, 90f);
            Place(agents, AgentAssetBuilder.Mimic, MimicHome, -90f);

            EditorSceneManager.SaveScene(scene, ScenePath);
            var nodes = graph.GetComponentsInChildren<NavNode>();
            Debug.Log($"[FacilitySandbox] Built {ScenePath}: {moved} colliders -> Environment, {nodes.Length} nodes " +
                      $"({doorNodes.Count} at {doors.Length} doors, {cut} links through doors removed), " +
                      $"tags: {Count(nodes, NodeTag.Doorway)} doorway / {Count(nodes, NodeTag.AmbushSpot)} ambush / " +
                      $"{Count(nodes, NodeTag.Junction)} junction, player copied: {playerOk}.");
        }

        // ------------------------------------------------------------------ graph helpers

        /// NavMesh triangle centres bucketed into NodeSpacing cells per ~3 m of height; one point per cell (the one
        /// nearest the cell centre), kept only if walkable from the spawn and pushed away from walls.
        static IEnumerable<Vector3> SampleNavMesh(Vector3 from)
        {
            var tri = NavMesh.CalculateTriangulation();
            var best = new Dictionary<(int, int, int), (Vector3 p, float d)>();
            var samples = new List<Vector3>();
            for (int i = 0; i < tri.indices.Length; i += 3)
            {
                // Points on a ~1.5 m lattice inside the triangle (big floor triangles span many cells).
                Vector3 v0 = tri.vertices[tri.indices[i]], v1 = tri.vertices[tri.indices[i + 1]], v2 = tri.vertices[tri.indices[i + 2]];
                int steps = Mathf.Clamp(Mathf.CeilToInt(Mathf.Max((v1 - v0).magnitude, (v2 - v0).magnitude) / 1.5f), 1, 60);
                for (int a = 0; a <= steps; a++)
                for (int b = 0; a + b <= steps; b++)
                    samples.Add(v0 + (v1 - v0) * ((float)a / steps) + (v2 - v0) * ((float)b / steps));
            }
            foreach (var c in samples)
            {
                var key = (Mathf.FloorToInt(c.x / NodeSpacing), Mathf.FloorToInt(c.z / NodeSpacing), Mathf.RoundToInt(c.y / 3f));
                var centre = new Vector2((key.Item1 + 0.5f) * NodeSpacing, (key.Item2 + 0.5f) * NodeSpacing);
                float d = Vector2.Distance(new Vector2(c.x, c.z), centre);
                if (!best.TryGetValue(key, out var b) || d < b.d) best[key] = (c, d);
            }
            var path = new NavMeshPath();
            foreach (var (p0, _) in best.Values)
            {
                if (!NavMesh.SamplePosition(p0, out var hit, 1f, NavMesh.AllAreas)) continue;
                Vector3 p = hit.position;
                if (NavMesh.FindClosestEdge(p, out var edge, NavMesh.AllAreas) && edge.distance < 0.8f)
                {
                    Vector3 away = p - edge.position; away.y = 0f;
                    if (away.sqrMagnitude < 1e-4f) away = edge.normal;
                    if (NavMesh.SamplePosition(p + away.normalized * (0.8f - edge.distance), out var moved, 0.6f, NavMesh.AllAreas))
                        p = moved.position;
                }
                if (NavMesh.CalculatePath(from, p, NavMesh.AllAreas, path) && path.status == NavMeshPathStatus.PathComplete)
                    yield return p;
            }
        }

        /// One Doorway node on each side of the door leaf, wired into Door.sideA/sideB so the motor opens it.
        static IEnumerable<NavNode> DoorNodes(Transform parent, Door door, ref int n)
        {
            var result = new List<NavNode>();
            var box = door.leafCollider != null ? door.leafCollider : door.GetComponentInChildren<BoxCollider>();
            if (box == null) return result;
            var t = box.transform;
            Vector3 size = Vector3.Scale(box.size, t.lossyScale);
            Vector3 axis = Mathf.Abs(size.x) <= Mathf.Abs(size.z) ? t.right : t.forward;   // thin axis = through the door
            axis.y = 0f; axis.Normalize();
            Vector3 centre = box.bounds.center;
            NavNode a = null, b = null;
            foreach (float s in new[] { -1f, 1f })
            {
                if (!NavMesh.SamplePosition(centre + axis * (1.8f * s), out var hit, 3f, NavMesh.AllAreas)) continue;
                var node = NewNode(parent, ref n, hit.position, NodeTag.Doorway);
                if (s < 0f) a = node; else b = node;
                result.Add(node);
            }
            if (a != null && b != null)
            {
                // Explicit link through the doorway: the NavMesh may not squeeze through a 1.5 m door frame.
                var linkGo = new GameObject("DoorNavLink");
                linkGo.transform.SetParent(door.transform, true);
                linkGo.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
                var link = linkGo.AddComponent<NavMeshLink>();
                link.startPoint = linkGo.transform.InverseTransformPoint(a.transform.position);
                link.endPoint = linkGo.transform.InverseTransformPoint(b.transform.position);
                link.width = 1f; link.bidirectional = true;
                link.UpdateLink();

                // Trigger around the leaf (3 m each side): an agent walking up opens the door (DoorAgentPassage).
                var sensor = new GameObject("AgentDoorSensor");
                sensor.layer = LayerMask.NameToLayer("Ignore Raycast");
                sensor.transform.SetParent(door.transform, true);
                sensor.transform.SetPositionAndRotation(box.bounds.center, Quaternion.LookRotation(axis));
                var trigger = sensor.AddComponent<BoxCollider>();
                trigger.isTrigger = true;
                trigger.size = new Vector3(Mathf.Max(Mathf.Abs(size.x), Mathf.Abs(size.z)) + 0.6f, 2.4f, 6f);
                var passage = sensor.AddComponent<DoorAgentPassage>();
                passage.door = door; passage.link = link;
                Undo.RecordObject(door, "Door sides");
                door.sideA = a; door.sideB = b;
                EditorUtility.SetDirty(door);
            }
            return result;
        }

        /// Auto-link may join two nodes whose straight walk crosses a door leaf; only the door's own pair may do
        /// that (it carries the Door, so the motor opens it).
        static int CutLinksThroughDoors(NavGraph graph, Door[] doors)
        {
            int cut = 0;
            var leaves = doors.Where(d => d.leafCollider != null)
                              .Select(d => (door: d, bounds: Expanded(d.leafCollider.bounds))).ToList();
            foreach (var node in graph.GetComponentsInChildren<NavNode>())
                for (int i = node.links.Count - 1; i >= 0; i--)
                {
                    var other = node.links[i];
                    Vector3 a = node.transform.position + Vector3.up, b = other.transform.position + Vector3.up;
                    var ray = new Ray(a, b - a);
                    foreach (var (door, bounds) in leaves)
                    {
                        bool pair = (door.sideA == node && door.sideB == other) || (door.sideB == node && door.sideA == other);
                        if (pair || !bounds.IntersectRay(ray, out float dist) || dist > Vector3.Distance(a, b)) continue;
                        node.links.RemoveAt(i); cut++; EditorUtility.SetDirty(node);
                        break;
                    }
                }
            return cut / 2;
        }

        static Bounds Expanded(Bounds b) { b.Expand(new Vector3(0.2f, 0.5f, 0.2f)); return b; }

        /// Tags from the room layout:
        ///   Doorway    = for each pair of connected rooms, the shortest link across their boundary (both ends),
        ///                plus the real doors' node pairs
        ///   Junction   = 4+ rooms around a node (its own + its neighbours')
        ///   AmbushSpot = per doorway, the node of the same room 3-9 m away that hugs a wall most (waits beside the way in)
        ///   Corridor / Room = narrow (< 1 m to the NavMesh edge) or not
        static void AutoTag(NavGraph graph, List<NavNode> doorNodes)
        {
            var nodes = graph.GetComponentsInChildren<NavNode>();
            var clearance = nodes.ToDictionary(x => x, x =>
                NavMesh.FindClosestEdge(x.transform.position, out var e, NavMesh.AllAreas) ? e.distance : 5f);

            foreach (var x in nodes)
            {
                NodeTag tag = clearance[x] < 1f ? NodeTag.Corridor : NodeTag.Room;
                if (new HashSet<string>(x.links.Select(l => l.zone)) { x.zone }.Count >= 4) tag |= NodeTag.Junction;
                if (doorNodes.Contains(x)) tag |= NodeTag.Doorway;
                x.tags = tag;
            }

            var crossings = new Dictionary<(string, string), (NavNode a, NavNode b, float len)>();
            foreach (var a in nodes)
                foreach (var b in a.links)
                {
                    if (a.zone == b.zone || string.CompareOrdinal(a.zone, b.zone) > 0) continue;
                    float len = Vector3.Distance(a.transform.position, b.transform.position);
                    var key = (a.zone, b.zone);
                    if (!crossings.TryGetValue(key, out var c) || len < c.len) crossings[key] = (a, b, len);
                }
            foreach (var (a, b, _) in crossings.Values) { a.tags |= NodeTag.Doorway; b.tags |= NodeTag.Doorway; }

            var doorways = nodes.Where(x => (x.tags & NodeTag.Doorway) != 0).ToList();
            foreach (var d in doorways)
            {
                var spot = nodes.Where(x => x.zone == d.zone && (x.tags & NodeTag.Doorway) == 0)
                                .Where(x => { float dist = Vector3.Distance(x.transform.position, d.transform.position); return dist > 3f && dist < 9f; })
                                .OrderBy(x => clearance[x]).FirstOrDefault();
                if (spot != null) spot.tags |= NodeTag.AmbushSpot;
            }
            foreach (var x in nodes) EditorUtility.SetDirty(x);
        }

        static float EdgeDistance(Vector3 p, Vector3 min, Vector3 max)
            => Mathf.Min(Mathf.Min(p.x - min.x, max.x - p.x), Mathf.Min(p.z - min.z, max.z - p.z));

        static NavNode NewNode(Transform parent, ref int n, Vector3 pos, NodeTag tag)
        {
            var go = new GameObject($"Node_{n++:000}");
            go.transform.SetParent(parent, false);
            go.transform.position = pos;
            var node = go.AddComponent<NavNode>();
            node.tags = tag;
            return node;
        }

        static int Count(NavNode[] nodes, NodeTag t) => nodes.Count(x => (x.tags & t) != 0);

        static void Zone(Transform parent, string id, Vector3 min, Vector3 max)
        {
            var go = new GameObject("Zone_" + id);
            go.transform.SetParent(parent, false);
            go.transform.position = (min + max) * 0.5f;
            go.layer = LayerMask.NameToLayer("Ignore Raycast");
            var box = go.AddComponent<BoxCollider>();
            box.size = max - min; box.isTrigger = true;
            go.AddComponent<ZoneVolume>().zoneId = id;
        }

        // ------------------------------------------------------------------ scene helpers

        static void Place(Transform parent, AgentAssetBuilder.AgentSpec spec, Vector3 home, float yaw)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(spec.PrefabPath);
            if (prefab == null) { Debug.LogError($"[FacilitySandbox] {spec.PrefabPath} missing"); return; }
            if (NavMesh.SamplePosition(home, out var hit, 4f, NavMesh.AllAreas)) home = hit.position;
            else Debug.LogWarning($"[FacilitySandbox] {spec.Name} home {home} is not on the NavMesh");
            var agent = (GameObject)PrefabUtility.InstantiatePrefab(prefab, parent);
            agent.transform.SetPositionAndRotation(home, Quaternion.Euler(0f, yaw, 0f));
        }

        /// Floors that became Environment must still count as ground for the Starter Assets controller.
        static void FixGroundLayers(int env)
        {
            var player = GameObject.Find("PlayerCapsule");
            if (player == null) return;
            foreach (var mb in player.GetComponents<MonoBehaviour>())
            {
                var so = new SerializedObject(mb);
                var prop = so.FindProperty("GroundLayers");
                if (prop == null) continue;
                prop.intValue |= (1 << env) | (1 << 0) | (1 << 8);
                so.ApplyModifiedPropertiesWithoutUndo();
            }
        }

        static Material HearingMaterial()
        {
            const string path = "Assets/Art/Materials/Sandbox/HearingArea.mat";
            var m = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (m != null) return m;
            System.IO.Directory.CreateDirectory("Assets/Art/Materials/Sandbox");
            m = new Material(Shader.Find("Sprites/Default")) { name = "HearingArea" };
            AssetDatabase.CreateAsset(m, path);
            return m;
        }
    }
}
