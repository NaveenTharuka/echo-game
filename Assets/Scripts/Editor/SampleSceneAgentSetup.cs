using System.Collections.Generic;
using System.Linq;
using Echo.AI;
using Echo.Navigation;
using Echo.Player;
using Echo.Sound;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AI;

namespace Echo.EditorTools
{
    /// ECHO > SampleScene > ... Wires the real level for the AI, in place (same steps as FacilitySandboxBuilder).
    /// Every item can be re-run: it first removes what it made before.
    ///   1. Shared Setup: Environment layer, wired player, NavMesh, door links, zones, NavGraph, Systems
    ///   2-5. Add one agent at its post (floor plan v5) under "Agents"
    public static class SampleSceneAgentSetup
    {
        const string ScenePath = "Assets/Scenes/SampleScene.unity";
        const string NavMeshPath = "Assets/Scenes/SampleScene_NavMesh.asset";
        static readonly string[] GeneratedRoots = { "NavMeshSurface", "Zones", "NavGraph", "Systems" };
        static readonly string[] GeneratedDoorChildren = { "DoorNavLink", "AgentDoorSensor" };

        // Posts (Docs/Map/ECHO_floor_plan_v5.png).
        static readonly Vector3 ListenerPost = new(50f, -6f, 31f);
        static readonly Vector3 AmbusherPost = new(75f, -12f, 26f);
        static readonly Vector3 TrackerPost = new(54f, -24f, 78f);
        static readonly Vector3 MimicPost = new(93f, -24f, 68f);

        [MenuItem("ECHO/SampleScene/1. Shared Setup (player, NavMesh, NavGraph, zones, systems)")]
        static void SharedSetupMenu() { if (OpenScene()) SharedSetup(); }

        [MenuItem("ECHO/SampleScene/2. Add Listener")]
        static void AddListenerMenu() { if (OpenScene()) AddAgent(AgentAssetBuilder.Listener, ListenerPost, 90f); }

        [MenuItem("ECHO/SampleScene/3. Add Ambusher")]
        static void AddAmbusherMenu() { if (OpenScene()) AddAgent(AgentAssetBuilder.Ambusher, AmbusherPost, 0f); }

        [MenuItem("ECHO/SampleScene/4. Add Tracker")]
        static void AddTrackerMenu() { if (OpenScene()) AddAgent(AgentAssetBuilder.Tracker, TrackerPost, 90f); }

        [MenuItem("ECHO/SampleScene/5. Add Mimic")]
        static void AddMimicMenu() { if (OpenScene()) AddAgent(AgentAssetBuilder.Mimic, MimicPost, -90f); }

        [MenuItem("ECHO/SampleScene/6. Use Horror Music (TensionMusic)")]
        static void UseTensionMusicMenu()
        {
            if (!OpenScene()) return;
            var systems = GameObject.Find("Systems");
            if (systems == null) { Debug.LogError("[SampleSceneSetup] run 1. Shared Setup first"); return; }
            UseTensionMusic(systems);
            EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
            EditorSceneManager.SaveScene(EditorSceneManager.GetActiveScene());
        }

        /// Same horror music as the sandboxes: DarkDrone calm bed, HorrorDrone while an agent hunts.
        /// The old synth BackgroundMusic is switched off (kept in the scene, not deleted).
        static void UseTensionMusic(GameObject systems)
        {
            var old = GameObject.Find("BackgroundMusic");
            if (old != null) old.SetActive(false);
            if (!systems.TryGetComponent<TensionMusic>(out var music)) music = systems.AddComponent<TensionMusic>();
            music.calmLoop = AssetDatabase.LoadAssetAtPath<AudioClip>("Assets/Audio/Music/DarkDrone_Loop.wav");
            music.huntLoop = AssetDatabase.LoadAssetAtPath<AudioClip>("Assets/Audio/Music/HorrorDrone_Loop.wav");
            Debug.Log($"[SampleSceneSetup] TensionMusic on Systems (calm {music.calmLoop != null}, hunt {music.huntLoop != null}), " +
                      $"BackgroundMusic off: {old != null}.");
        }

        static bool OpenScene()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) return false;
            if (EditorSceneManager.GetActiveScene().path == ScenePath) return true;
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return false;
            EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            return true;
        }

        public static void SharedSetup()
        {
            AgentAssetBuilder.EnsureLayers();
            int env = LayerMask.NameToLayer(AgentAssetBuilder.EnvironmentLayer);
            var scene = EditorSceneManager.GetActiveScene();

            // Remove the last run's output.
            foreach (var root in scene.GetRootGameObjects())
                if (GeneratedRoots.Contains(root.name)) Object.DestroyImmediate(root);
            var doors = Object.FindObjectsByType<Door>(FindObjectsSortMode.None);
            foreach (var d in doors)
                foreach (var t in d.GetComponentsInChildren<Transform>(true).Where(t => GeneratedDoorChildren.Contains(t.name)).ToList())
                    Object.DestroyImmediate(t.gameObject);

            // Player: swap the bare capsule for the wired one (keep it if it is wired already).
            var player = GameObject.Find("PlayerCapsule");
            bool swapPlayer = player == null || player.GetComponentInChildren<PlayerNoise>(true) == null;
            Vector3 spawn = player != null ? player.transform.position : new Vector3(24f, 0.05f, 68.6f);
            Quaternion spawnRot = player != null ? player.transform.rotation : Quaternion.Euler(0f, 180f, 0f);
            if (swapPlayer)
                foreach (var root in scene.GetRootGameObjects())
                    if (root.name is "PlayerCapsule" or "InteractionHUD") Object.DestroyImmediate(root);

            int moved = FacilitySandboxBuilder.MoveOccludersToEnvironment(env);

            // NavMesh without the player and agents in it.
            var hidden = scene.GetRootGameObjects().Where(r => r.activeSelf && r.name is "PlayerCapsule" or "Agents").ToList();
            foreach (var r in hidden) r.SetActive(false);
            var surface = FacilitySandboxBuilder.BakeNavMesh(env, doors, NavMeshPath);
            foreach (var r in hidden) r.SetActive(true);
            if (!NavMesh.SamplePosition(spawn, out var spawnHit, 4f, NavMesh.AllAreas))
            { Debug.LogError("[SampleSceneSetup] spawn is not on the NavMesh"); return; }

            // Zones, nodes (door pairs first), links, tags.
            var zoneRoot = new GameObject("Zones").transform;
            foreach (var (id, min, max) in FacilitySandboxBuilder.Zones) FacilitySandboxBuilder.Zone(zoneRoot, id, min, max);

            var graph = new GameObject("NavGraph").AddComponent<NavGraph>();
            var doorNodes = new List<NavNode>();
            int n = 0;
            foreach (var d in doors) doorNodes.AddRange(FacilitySandboxBuilder.DoorNodes(graph.transform, d, ref n));
            NavMesh.SamplePosition(spawn, out spawnHit, 4f, NavMesh.AllAreas);   // links are registered now

            float minGap = FacilitySandboxBuilder.NodeSpacing * 0.55f;
            var accepted = new List<Vector3>(doorNodes.Select(x => x.transform.position));
            foreach (var p in FacilitySandboxBuilder.SampleNavMesh(spawnHit.position))
            {
                if (accepted.Any(a => Vector3.Distance(a, p) < minGap)) continue;
                accepted.Add(p);
                FacilitySandboxBuilder.NewNode(graph.transform, ref n, p, NodeTag.Room);
            }
            NavGraphTools.AutoLink(graph);
            int cut = FacilitySandboxBuilder.CutLinksThroughDoors(graph, doors);
            FacilitySandboxBuilder.AutoTag(graph, doorNodes);

            // Systems. Music = the sandboxes' TensionMusic (replaces SampleScene's BackgroundMusic).
            var systems = new GameObject("Systems");
            UseTensionMusic(systems);
            var library = AssetDatabase.LoadAssetAtPath<SoundLibrary>(SoundLibraryBuilder.LibraryPath);
            systems.AddComponent<AudioService>().library = library != null ? library : SoundLibraryBuilder.Build();
            systems.AddComponent<SoundDebug>();
            // Debug views start hidden in the real level (F1 / F5 show them).
            systems.AddComponent<AgentDebugOverlay>().visible = false;
            var hearing = systems.AddComponent<HearingVisualizer>();
            hearing.material = FacilitySandboxBuilder.HearingMaterial();
            hearing.visible = false;

            bool playerOk = !swapPlayer || ListenerSandboxBuilder.CopyPlayer(spawn, spawnRot);
            FacilitySandboxBuilder.FixGroundLayers(env);
            FacilitySandboxBuilder.FixInteractMask(env);
            if (GameObject.Find("Agents") == null) new GameObject("Agents");

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);

            var nodes = graph.GetComponentsInChildren<NavNode>();
            int reached = Reachable(nodes);
            var unzoned = nodes.Count(x => string.IsNullOrEmpty(x.zone) || x.zone == "Unzoned");
            string msg = $"[SampleSceneSetup] {moved} colliders -> Environment, NavMesh {NavMeshPath}, {nodes.Length} nodes " +
                         $"({reached} connected, {unzoned} unzoned, {doorNodes.Count} at {doors.Length} doors, {cut} links through doors removed), " +
                         $"tags: {FacilitySandboxBuilder.Count(nodes, NodeTag.Doorway)} doorway / {FacilitySandboxBuilder.Count(nodes, NodeTag.AmbushSpot)} ambush / " +
                         $"{FacilitySandboxBuilder.Count(nodes, NodeTag.Junction)} junction, player swapped: {swapPlayer} (ok {playerOk}).";
            if (reached == nodes.Length && playerOk) Debug.Log(msg); else Debug.LogWarning(msg);
            if (surface == null) Debug.LogError("[SampleSceneSetup] NavMesh bake failed");
        }

        /// Puts one agent at its post, replacing an earlier copy of the same agent.
        public static void AddAgent(AgentAssetBuilder.AgentSpec spec, Vector3 post, float yaw)
        {
            var scene = EditorSceneManager.GetActiveScene();
            if (GameObject.Find("NavGraph") == null) { Debug.LogError("[SampleSceneSetup] run 1. Shared Setup first"); return; }
            var agents = GameObject.Find("Agents");
            if (agents == null) agents = new GameObject("Agents");
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(spec.PrefabPath);
            foreach (Transform child in agents.transform.Cast<Transform>().ToList())
                if (PrefabUtility.GetCorrespondingObjectFromSource(child.gameObject) == prefab) Object.DestroyImmediate(child.gameObject);

            FacilitySandboxBuilder.Place(agents.transform, spec, post, yaw);
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            Debug.Log($"[SampleSceneSetup] {spec.Name} placed at {post}.");
        }

        /// Nodes reachable from the first one over the graph links.
        static int Reachable(NavNode[] nodes)
        {
            if (nodes.Length == 0) return 0;
            var seen = new HashSet<NavNode> { nodes[0] };
            var open = new Stack<NavNode>(seen);
            while (open.Count > 0)
                foreach (var l in open.Pop().links)
                    if (l != null && seen.Add(l)) open.Push(l);
            return seen.Count;
        }
    }
}
