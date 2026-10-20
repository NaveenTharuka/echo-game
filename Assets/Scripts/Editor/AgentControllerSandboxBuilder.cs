using System.IO;
using Echo.AI;
using Echo.DevTools;
using Echo.Navigation;
using Unity.AI.Navigation;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.Rendering;

namespace Echo.EditorTools
{
    /// ECHO > Agents > Agent Controller > Build Sandbox Scene. (Re)creates Sandbox_AgentController.unity:
    ///
    ///   one 24 x 16 m room cut into four bays by three walls with gaps at alternate ends (a serpentine), a pillar in
    ///   two bays, NavMesh + auto-linked NavGraph, and a capsule agent (no model needed) patrolling 4 corner waypoints.
    ///   Every lap has many sharp node corners, so smoothing, turn-in-place and the path lines are easy to see.
    public static class AgentControllerSandboxBuilder
    {
        public const string ScenePath = "Assets/Scenes/Sandbox/Sandbox_AgentController.unity";
        const string MatFolder = "Assets/Art/Materials/Sandbox/AgentController";

        [MenuItem("ECHO/Agents/Agent Controller/Build Sandbox Scene")]
        static void BuildMenu()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode || !EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            Build();
        }

        public static void Build()
        {
            int env = LayerMask.NameToLayer(AgentAssetBuilder.EnvironmentLayer);
            int agents = LayerMask.NameToLayer(AgentAssetBuilder.AgentsLayer);
            if (env < 0 || agents < 0) { Debug.LogError("[AgentControllerSandbox] Layers Environment / Agents are missing."); return; }

            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            RenderSettings.ambientMode = AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.45f, 0.47f, 0.5f);

            var light = new GameObject("Directional Light").AddComponent<Light>();
            light.type = LightType.Directional; light.intensity = 1f; light.shadows = LightShadows.Soft;
            light.transform.rotation = Quaternion.Euler(55f, -30f, 0f);

            var cam = new GameObject("Main Camera").AddComponent<Camera>();
            cam.tag = "MainCamera";
            cam.fieldOfView = 55f;
            cam.clearFlags = CameraClearFlags.SolidColor; cam.backgroundColor = new Color(0.08f, 0.09f, 0.1f);
            cam.transform.position = new Vector3(0f, 19f, -16f);
            cam.transform.LookAt(new Vector3(0f, 0f, -0.5f));

            // Level: walls on Environment (also what NavGraph.Nearest checks line of sight against).
            Material floorMat = Mat("AC_Floor", new Color(0.32f, 0.33f, 0.35f));
            Material wallMat = Mat("AC_Wall", new Color(0.55f, 0.52f, 0.48f));
            var level = new GameObject("Level").transform;
            Box(level, "Floor", new Vector3(0f, -0.1f, 0f), new Vector3(24f, 0.2f, 16f), env, floorMat);
            Box(level, "Wall_N", new Vector3(0f, 1.25f, 8.1f), new Vector3(24.4f, 2.5f, 0.2f), env, wallMat);
            Box(level, "Wall_S", new Vector3(0f, 1.25f, -8.1f), new Vector3(24.4f, 2.5f, 0.2f), env, wallMat);
            Box(level, "Wall_W", new Vector3(-12.1f, 1.25f, 0f), new Vector3(0.2f, 2.5f, 16f), env, wallMat);
            Box(level, "Wall_E", new Vector3(12.1f, 1.25f, 0f), new Vector3(0.2f, 2.5f, 16f), env, wallMat);
            // Serpentine: gaps at the north end of x = -6 and x = 6, at the south end of x = 0.
            Box(level, "Divider_A", new Vector3(-6f, 1.25f, -2.5f), new Vector3(0.3f, 2.5f, 11f), env, wallMat);   // z -8 .. 3
            Box(level, "Divider_B", new Vector3(0f, 1.25f, 2.5f), new Vector3(0.3f, 2.5f, 11f), env, wallMat);     // z -3 .. 8
            Box(level, "Divider_C", new Vector3(6f, 1.25f, -2.5f), new Vector3(0.3f, 2.5f, 11f), env, wallMat);    // z -8 .. 3
            Box(level, "Pillar_1", new Vector3(-3f, 1.25f, 2.75f), new Vector3(1.2f, 2.5f, 1.2f), env, wallMat);
            Box(level, "Pillar_2", new Vector3(3f, 1.25f, -2.75f), new Vector3(1.2f, 2.5f, 1.2f), env, wallMat);

            var surface = new GameObject("NavMeshSurface").AddComponent<NavMeshSurface>();
            surface.collectObjects = CollectObjects.All;
            surface.useGeometry = NavMeshCollectGeometry.PhysicsColliders;
            surface.layerMask = 1 << env;
            surface.BuildNavMesh();
            string navPath = Path.ChangeExtension(ScenePath, null) + "_NavMesh.asset";
            AssetDatabase.DeleteAsset(navPath);
            AssetDatabase.CreateAsset(surface.navMeshData, navPath);

            // NavGraph: a 3 x 4 grid (bay centres) + one node in each gap.
            var graph = new GameObject("NavGraph").AddComponent<NavGraph>();
            int n = 0;
            foreach (float x in new[] { -9f, -3f, 3f, 9f })
                foreach (float z in new[] { -5.5f, 0f, 5.5f })
                    Node(graph.transform, ref n, new Vector3(x, 0f, z), NodeTag.Room);
            Node(graph.transform, ref n, new Vector3(-6f, 0f, 5.5f), NodeTag.Doorway);
            Node(graph.transform, ref n, new Vector3(0f, 0f, -5.5f), NodeTag.Doorway);
            Node(graph.transform, ref n, new Vector3(6f, 0f, 5.5f), NodeTag.Doorway);
            int reachable = NavGraphTools.AutoLink(graph);

            // Waypoints: the four room corners. A -> B snakes through all three gaps.
            Material wpMat = Mat("AC_Waypoint", new Color(0.2f, 0.6f, 1f));
            var wpRoot = new GameObject("Waypoints").transform;
            var wps = new[] { new Vector3(-9.5f, 0f, -6f), new Vector3(9.5f, 0f, -6f),
                              new Vector3(9.5f, 0f, 6f), new Vector3(-9.5f, 0f, 6f) };
            var wpTransforms = new Transform[wps.Length];
            for (int i = 0; i < wps.Length; i++)
            {
                var m = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                m.name = "WP_" + (char)('A' + i);
                Object.DestroyImmediate(m.GetComponent<Collider>());
                m.transform.SetParent(wpRoot, false);
                m.transform.position = wps[i] + Vector3.up * 0.02f;
                m.transform.localScale = new Vector3(0.8f, 0.02f, 0.8f);
                m.GetComponent<Renderer>().sharedMaterial = wpMat;
                wpTransforms[i] = m.transform;
            }

            var systems = new GameObject("Systems");
            systems.AddComponent<AgentDebugOverlay>().visible = false;   // F2 / F3 / F4 time scale (F1 overlay, F5 hearing)

            var agent = BuildAgent(agents, wps[0], wpTransforms);

            Directory.CreateDirectory(Path.GetDirectoryName(ScenePath));
            EditorSceneManager.SaveScene(scene, ScenePath);
            Selection.activeGameObject = agent;
            Debug.Log($"[AgentControllerSandbox] Built {ScenePath}: {n} nodes ({reachable} connected), {wps.Length} waypoints. Play: F7 path lines, F8 smoothing, 1/2/3 gait.");
        }

        /// Capsule stand-in for an agent prefab: same NavMeshAgent settings as AgentAssetBuilder, a "nose" to show
        /// the facing, motor + bridge + path visualizer, driven by a WaypointPatrol instead of a brain.
        static GameObject BuildAgent(int layer, Vector3 at, Transform[] waypoints)
        {
            var root = new GameObject("TestAgent") { layer = layer };
            root.transform.SetPositionAndRotation(at, Quaternion.Euler(0f, -90f, 0f));   // faces away: shows turn-in-place

            var nav = root.AddComponent<NavMeshAgent>();
            nav.radius = 0.4f; nav.height = 2f; nav.baseOffset = 0f;
            nav.speed = 2.2f; nav.angularSpeed = 360f; nav.acceleration = 12f;
            nav.stoppingDistance = 0.5f; nav.autoBraking = true;
            nav.obstacleAvoidanceType = ObstacleAvoidanceType.MedQualityObstacleAvoidance;
            var capsule = root.AddComponent<CapsuleCollider>();
            capsule.center = new Vector3(0f, 1f, 0f); capsule.height = 2f; capsule.radius = 0.4f;

            root.AddComponent<AgentMotor>();
            root.AddComponent<AgentAnimatorBridge>().footSlideFix = true;
            root.AddComponent<AgentPathVisualizer>();
            root.AddComponent<WaypointPatrol>().waypoints.AddRange(waypoints);

            Visual(root.transform, PrimitiveType.Capsule, "Body", new Vector3(0f, 1f, 0f), new Vector3(0.8f, 1f, 0.8f),
                   Mat("AC_Agent", new Color(0.85f, 0.25f, 0.2f)), layer);
            Visual(root.transform, PrimitiveType.Cube, "Nose", new Vector3(0f, 1.5f, 0.45f), new Vector3(0.2f, 0.2f, 0.4f),
                   Mat("AC_Nose", new Color(1f, 0.85f, 0.2f)), layer);
            return root;
        }

        static void Visual(Transform parent, PrimitiveType type, string name, Vector3 pos, Vector3 scale, Material m, int layer)
        {
            var go = GameObject.CreatePrimitive(type);
            go.name = name; go.layer = layer;
            Object.DestroyImmediate(go.GetComponent<Collider>());
            go.transform.SetParent(parent, false);
            go.transform.localPosition = pos; go.transform.localScale = scale;
            go.GetComponent<Renderer>().sharedMaterial = m;
        }

        static void Box(Transform parent, string name, Vector3 pos, Vector3 size, int layer, Material m)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name; go.layer = layer; go.isStatic = true;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = pos; go.transform.localScale = size;
            go.GetComponent<Renderer>().sharedMaterial = m;
        }

        static void Node(Transform parent, ref int n, Vector3 pos, NodeTag tag)
        {
            var go = new GameObject($"Node_{n++:00}");
            go.transform.SetParent(parent, false);
            if (NavMesh.SamplePosition(pos, out var hit, 2f, NavMesh.AllAreas)) pos = hit.position;
            go.transform.position = pos;
            go.AddComponent<NavNode>().tags = tag;
        }

        /// Plain URP Lit colour materials, saved as assets so the scene keeps them.
        static Material Mat(string name, Color c)
        {
            if (!AssetDatabase.IsValidFolder(MatFolder)) AssetDatabase.CreateFolder("Assets/Art/Materials/Sandbox", "AgentController");
            string path = $"{MatFolder}/{name}.mat";
            var m = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (m == null)
            {
                var shader = Shader.Find("Universal Render Pipeline/Lit");
                if (shader == null) shader = Shader.Find("Standard");
                m = new Material(shader);
                AssetDatabase.CreateAsset(m, path);
            }
            m.SetColor("_BaseColor", c);
            m.color = c;
            EditorUtility.SetDirty(m);
            return m;
        }
    }
}
