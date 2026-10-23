using Echo.AI.Tracker;
using Echo.Navigation;
using Echo.UI;
using Unity.AI.Navigation;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AI;
using static Echo.AI.Tracker.SandboxTrackerLayout;

namespace Echo.EditorTools
{
    /// ECHO > Tracker > Build Sandbox_TrackerGrid. Creates Assets/Scenes/Sandbox/Sandbox_TrackerGrid.unity as a copy of
    /// Sandbox_Interaction (keeps the configured player rig, HUD and light, removes its test areas), then
    /// (re)builds the tracker level: a 9 x 9 grid of 6 m cells - west spawn room, junction, north room
    /// (Tracker home), east room, a south arm with a sliding door, a south room and a loop corridor from the
    /// south room back to the east room (so a locked door has a detour). Bakes the NavMesh, writes the stub
    /// waypoint graph (one node per open cell, 4-neighbour links, the door link carries the Door) and places
    /// the stub Tracker. Re-runnable; only ever edits Sandbox_TrackerGrid.
    static class SandboxTrackerBuilder
    {
        public const string ScenePath = "Assets/Scenes/Sandbox/Sandbox_TrackerGrid.unity";
        const string NavMeshFolder = "Assets/Scenes/Sandbox/Sandbox_TrackerGrid";
        const string NavMeshPath = NavMeshFolder + "/NavMesh-TrackerArea.asset";
        const float WallHeight = 4f;

        [MenuItem("ECHO/Tracker/Build Sandbox_TrackerGrid")]
        static void Build()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) { Debug.LogError("[SandboxTrackerBuilder] Exit Play Mode first."); return; }
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;

            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(ScenePath) == null &&
                !AssetDatabase.CopyAsset(SandboxDoorBuilder.ScenePath, ScenePath))
            { Debug.LogError($"[SandboxTrackerBuilder] Could not copy {SandboxDoorBuilder.ScenePath}."); return; }
            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);

            // Keep the player rig, the HUD and lights; everything else is rebuilt.
            GameObject player = null;
            foreach (var root in scene.GetRootGameObjects())
            {
                var cc = root.GetComponentInChildren<CharacterController>(true);
                if (cc != null) { player = root; continue; }
                if (root.GetComponentInChildren<InteractionPromptUI>(true) != null || root.GetComponent<Light>() != null) continue;
                Object.DestroyImmediate(root);
            }

            var area = new GameObject("TrackerArea");
            var floor = Box(area.transform, "Floor", new Vector3(W * Cell * 0.5f, -0.1f, H * Cell * 0.5f), new Vector3(W * Cell, 0.2f, H * Cell));
            var walls = new GameObject("Walls");
            walls.transform.SetParent(area.transform, false);
            var wallMod = walls.AddComponent<NavMeshModifier>();
            wallMod.overrideArea = true; wallMod.area = 1;   // Not Walkable: no NavMesh on wall tops
            int wallCount = 0;
            for (int z = -1; z <= H; z++)
            for (int x = -1; x <= W; x++)
            {
                if (Open(x, z) || !TouchesOpen(x, z)) continue;
                Box(walls.transform, $"Wall_{x}_{z}", Centre(x, z) + Vector3.up * WallHeight * 0.5f, new Vector3(Cell, WallHeight, Cell));
                wallCount++;
            }

            Door door = PlaceDoor(area.transform);

            // NavMesh from physics colliders of TrackerArea only (agents and the player are not baked).
            var surface = area.AddComponent<NavMeshSurface>();
            surface.collectObjects = CollectObjects.Children;
            surface.useGeometry = NavMeshCollectGeometry.PhysicsColliders;
            surface.BuildNavMesh();
            if (!AssetDatabase.IsValidFolder(NavMeshFolder)) AssetDatabase.CreateFolder("Assets/Scenes/Sandbox", "Sandbox_TrackerGrid");
            if (AssetDatabase.LoadAssetAtPath<Object>(NavMeshPath) != null) AssetDatabase.DeleteAsset(NavMeshPath);
            AssetDatabase.CreateAsset(surface.navMeshData, NavMeshPath);

            // Stub waypoint graph.
            var graphGo = new GameObject("WaypointGraph");
            graphGo.transform.SetParent(area.transform, false);
            var graph = graphGo.AddComponent<SandboxTrackerGraph>();
            Nodes(p => graph.nodes.Add(p),
                  (a, b, isDoor) => graph.links.Add(new SandboxTrackerGraph.Link { a = a, b = b, door = isDoor ? door : null }));

            // Player at the west room, facing east.
            if (player != null)
            {
                Vector3 spawn = Centre(SpawnCell.x, SpawnCell.y);
                player.transform.SetPositionAndRotation(new Vector3(spawn.x, player.transform.position.y, spawn.z), Quaternion.Euler(0f, 90f, 0f));
            }

            GameObject tracker = BuildTracker(graph, Centre(HomeCell.x, HomeCell.y));

            // Sanity: the baked NavMesh connects spawn -> home and runs through the doorway.
            string pathSpawn = PathStatus(Centre(SpawnCell.x, SpawnCell.y), Centre(HomeCell.x, HomeCell.y));
            string pathDoor = PathStatus(Centre(4, 4), Centre(4, 1));

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            Selection.activeGameObject = tracker;
            Debug.Log($"[SandboxTrackerBuilder] Built {ScenePath}: {graph.nodes.Count} nodes, {graph.links.Count} links, {wallCount} wall blocks, " +
                      $"door {(door != null ? "placed" : "MISSING")}. NavMesh path spawn->home: {pathSpawn}; through the door: {pathDoor}. " +
                      $"Player kept: {player != null}.");
        }

        /// Play Mode scenarios (a-e) in Sandbox_TrackerGrid; one report in the console when done (~2 min).
        [MenuItem("ECHO/Tracker/Run Scenarios")]
        static void RunScenarios()
        {
            if (EditorSceneManager.GetActiveScene().path != ScenePath)
            { Debug.LogError($"[TrackerScenarios] Open {ScenePath} and enter Play Mode first."); return; }
            Echo.DevTools.TrackerScenarios.Launch();
        }

        [MenuItem("ECHO/Tracker/Run Scenarios", true)]
        static bool CanRunScenarios() => EditorApplication.isPlaying;

        /// Scenario (a) only, ten times: pass rate + ETA errors (Tracker and runner) at the pick (~4 min).
        [MenuItem("ECHO/Tracker/Run Scenario A x10")]
        static void RunScenarioA()
        {
            if (EditorSceneManager.GetActiveScene().path != ScenePath)
            { Debug.LogError($"[TrackerScenarios] Open {ScenePath} and enter Play Mode first."); return; }
            Echo.DevTools.TrackerScenarios.LaunchA(10);
        }

        [MenuItem("ECHO/Tracker/Run Scenario A x10", true)]
        static bool CanRunScenarioA() => EditorApplication.isPlaying;

        /// A/B of scenario (a): P3, C, C-shrunk, C-raw, 20 paired runs each (~30 min).
        [MenuItem("ECHO/Tracker/A-B Scenario A x20 (core configs)")]
        static void RunAbCore() => RunAb(false);

        [MenuItem("ECHO/Tracker/A-B Scenario A x20 (core configs)", true)]
        static bool CanRunAbCore() => EditorApplication.isPlaying;

        /// A/B of scenario (a): P3, P3+point, C-node, C-m0, 20 paired runs each (~30 min).
        [MenuItem("ECHO/Tracker/A-B Scenario A x20 (toggles)")]
        static void RunAbToggles() => RunAb(true);

        [MenuItem("ECHO/Tracker/A-B Scenario A x20 (toggles)", true)]
        static bool CanRunAbToggles() => EditorApplication.isPlaying;

        static void RunAb(bool toggles)
        {
            if (EditorSceneManager.GetActiveScene().path != ScenePath)
            { Debug.LogError($"[TrackerScenarios] Open {ScenePath} and enter Play Mode first."); return; }
            Echo.DevTools.TrackerScenarios.LaunchAB(20, toggles);
        }

        /// Integrated Tracker (TrackerBrain, real NavGraph + Hearing) in dev's Sandbox_Tracker, scenarios a-e (~3 min).
        [MenuItem("ECHO/Tracker/Run NavGraph Scenarios (Sandbox_Tracker)")]
        static void RunNavGraphScenarios()
        {
            const string scene = "Assets/Scenes/Sandbox/Sandbox_Tracker.unity";
            if (EditorSceneManager.GetActiveScene().path != scene)
            { Debug.LogError($"[TrackerNavGraphScenarios] Open {scene} and enter Play Mode first."); return; }
            Echo.DevTools.TrackerNavGraphScenarios.Launch();
        }

        [MenuItem("ECHO/Tracker/Run NavGraph Scenarios (Sandbox_Tracker)", true)]
        static bool CanRunNavGraphScenarios() => EditorApplication.isPlaying;

        /// Any agent sandbox: a gunshot through InteractionNoise near each agent must reach its Hearing.
        [MenuItem("ECHO/Dev/Agent Hearing Smoke Test")]
        static void RunHearingSmokeTest() => Echo.DevTools.AgentHearingSmokeTest.Launch();

        [MenuItem("ECHO/Dev/Agent Hearing Smoke Test", true)]
        static bool CanRunHearingSmokeTest() => EditorApplication.isPlaying;

        /// Times the real mover over straight and cornered routes and fits the ETA model (~3 min).
        [MenuItem("ECHO/Tracker/Calibrate Mover")]
        static void CalibrateMover()
        {
            if (EditorSceneManager.GetActiveScene().path != ScenePath)
            { Debug.LogError($"[TrackerMoverCalibration] Open {ScenePath} and enter Play Mode first."); return; }
            Echo.DevTools.TrackerMoverCalibration.Launch();
        }

        [MenuItem("ECHO/Tracker/Calibrate Mover", true)]
        static bool CanCalibrateMover() => EditorApplication.isPlaying;

        static bool TouchesOpen(int x, int z)
        {
            for (int dz = -1; dz <= 1; dz++)
            for (int dx = -1; dx <= 1; dx++)
                if (Open(x + dx, z + dz)) return true;
            return false;
        }

        static GameObject Box(Transform parent, string name, Vector3 position, Vector3 size)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = position;
            go.transform.localScale = size;
            GameObjectUtility.SetStaticEditorFlags(go, StaticEditorFlags.BatchingStatic | StaticEditorFlags.NavigationStatic);
            return go;
        }

        /// Door_Slide is a kit wall piece (local YZ plane, 6 m along local Z). Rotated 90 deg about Y it spans
        /// world X from its pivot, so it closes the 6 m corridor between cells DoorA and DoorB.
        static Door PlaceDoor(Transform parent)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(InteractablePrefabBuilder.DoorPrefabPath);
            if (prefab == null) { Debug.LogError("[SandboxTrackerBuilder] Door_Slide prefab missing - run ECHO > Prefabs > Build Interactable Prefabs."); return null; }
            var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab, parent);
            go.name = "Door_South";
            go.transform.SetLocalPositionAndRotation(new Vector3(DoorA.x * Cell, 0f, DoorA.y * Cell), Quaternion.Euler(0f, 90f, 0f));
            var door = go.GetComponentInChildren<Door>();
            // The use volume is a trigger in the doorway: keep it out of the bake (instance override only).
            foreach (var c in go.GetComponentsInChildren<Collider>(true))
                if (c.isTrigger && c.GetComponent<NavMeshModifier>() == null) c.gameObject.AddComponent<NavMeshModifier>().ignoreFromBuild = true;
            return door;
        }

        static GameObject BuildTracker(SandboxTrackerGraph graph, Vector3 home)
        {
            var go = new GameObject("Tracker");
            go.transform.SetPositionAndRotation(home, Quaternion.Euler(0f, 180f, 0f));

            var rb = go.AddComponent<Rigidbody>();
            rb.isKinematic = true; rb.useGravity = false;
            var col = go.AddComponent<CapsuleCollider>();
            col.center = Vector3.up; col.height = 2f; col.radius = 0.4f;
            var nav = go.AddComponent<NavMeshAgent>();
            nav.radius = 0.4f; nav.height = 2f; nav.speed = 2.2f; nav.acceleration = 20f; nav.angularSpeed = 720f; nav.stoppingDistance = 0f;

            var mover = go.AddComponent<NavMeshTrackerMover>(); mover.graph = graph;
            var ears = go.AddComponent<TrackerPerceptionStub>(); ears.graph = graph;
            var agent = go.AddComponent<TrackerStubAgent>(); agent.graph = graph;
            go.AddComponent<TrackerDebugOverlay>();

            // Placeholder body (no collider); visuals are not the Tracker's business.
            var body = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            body.name = "Body";
            Object.DestroyImmediate(body.GetComponent<Collider>());
            body.transform.SetParent(go.transform, false);
            body.transform.localPosition = Vector3.up;
            body.transform.localScale = new Vector3(0.8f, 1f, 0.8f);
            var face = GameObject.CreatePrimitive(PrimitiveType.Cube);
            face.name = "Face";
            Object.DestroyImmediate(face.GetComponent<Collider>());
            face.transform.SetParent(go.transform, false);
            face.transform.localPosition = new Vector3(0f, 1.55f, 0.35f);
            face.transform.localScale = new Vector3(0.45f, 0.12f, 0.2f);
            return go;
        }

        static string PathStatus(Vector3 a, Vector3 b)
        {
            if (!NavMesh.SamplePosition(a, out var ha, 2f, NavMesh.AllAreas) || !NavMesh.SamplePosition(b, out var hb, 2f, NavMesh.AllAreas))
                return "no NavMesh at an end";
            var path = new NavMeshPath();
            NavMesh.CalculatePath(ha.position, hb.position, NavMesh.AllAreas, path);
            return path.status.ToString();
        }
    }
}
