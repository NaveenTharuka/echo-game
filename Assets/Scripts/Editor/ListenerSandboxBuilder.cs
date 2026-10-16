using System.IO;
using Echo.AI;
using Echo.Navigation;
using Echo.Sound;
using Unity.AI.Navigation;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Echo.EditorTools
{
    /// ECHO > Agents > Listener > 2. Build Sandbox Scene. (Re)creates Assets/Scenes/Sandbox/Sandbox_Listener.unity:
    ///
    ///   West room (Listener home)  |wall with one doorway|  East room (player spawn)       20 x 30 m in total
    ///   crates for cover in both rooms, all walls on layer Environment (they block sound: it goes round via the door).
    ///
    /// Then bakes the NavMesh, places + auto-links waypoint nodes, adds Systems (AudioService, SoundDebug, overlay),
    /// copies the fully wired player from Sandbox_Interaction, and drops in the Listener prefab.
    public static class ListenerSandboxBuilder
    {
        public const string ScenePath = "Assets/Scenes/Sandbox/Sandbox_Listener.unity";
        public const string TrackerScenePath = "Assets/Scenes/Sandbox/Sandbox_Tracker.unity";
        public const string AmbusherScenePath = "Assets/Scenes/Sandbox/Sandbox_Ambusher.unity";
        const string PlayerSourceScene = "Assets/Scenes/Sandbox/Sandbox_Interaction.unity";
        static readonly Vector3 PlayerSpawn = new(9f, 0f, -6f);
        static readonly Vector3 AgentHome = new(-9f, 0f, 3f);

        [MenuItem("ECHO/Agents/Listener/2. Build Sandbox Scene")]
        static void BuildMenu() { if (CanBuild()) Build(); }

        [MenuItem("ECHO/Agents/Tracker/2. Build Sandbox Scene")]
        static void BuildTrackerMenu() { if (CanBuild()) Build(AgentAssetBuilder.Tracker, TrackerScenePath); }

        [MenuItem("ECHO/Agents/Ambusher/2. Build Sandbox Scene")]
        static void BuildAmbusherMenu() { if (CanBuild()) Build(AgentAssetBuilder.Ambusher, AmbusherScenePath); }

        static bool CanBuild() => !EditorApplication.isPlayingOrWillChangePlaymode &&
                                  EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo();

        public static void Build() => Build(AgentAssetBuilder.Listener, ScenePath);

        /// Same two-room level for every agent; only the agent prefab and the scene file differ.
        public static void Build(AgentAssetBuilder.AgentSpec spec, string scenePath)
        {
            AgentAssetBuilder.EnsureLayers();
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(spec.PrefabPath);
            if (prefab == null) prefab = AgentAssetBuilder.Build(spec);

            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            int env = LayerMask.NameToLayer(AgentAssetBuilder.EnvironmentLayer);

            // Light: a faint cold "moon" only; the rooms are lit by lamps (ApplyLook).
            var light = new GameObject("Directional Light").AddComponent<Light>();
            light.type = LightType.Directional; light.intensity = 0.12f; light.color = new Color(0.7f, 0.8f, 1f);
            light.shadows = LightShadows.Soft;
            light.transform.rotation = Quaternion.Euler(50f, -30f, 0f);

            // Level
            var level = new GameObject("Level").transform;
            Box(level, "Floor", new Vector3(0f, -0.1f, 0f), new Vector3(30f, 0.2f, 20f), env);
            Box(level, "Wall_N", new Vector3(0f, 1.5f, 10.1f), new Vector3(30.4f, 3f, 0.2f), env);
            Box(level, "Wall_S", new Vector3(0f, 1.5f, -10.1f), new Vector3(30.4f, 3f, 0.2f), env);
            Box(level, "Wall_W", new Vector3(-15.1f, 1.5f, 0f), new Vector3(0.2f, 3f, 20f), env);
            Box(level, "Wall_E", new Vector3(15.1f, 1.5f, 0f), new Vector3(0.2f, 3f, 20f), env);
            // Dividing wall at x = 0, doorway 2.4 m wide centred on z = 5.
            Box(level, "Divider_S", new Vector3(0f, 1.5f, -3.4f), new Vector3(0.3f, 3f, 13.2f), env);   // z -10 .. 3.2
            Box(level, "Divider_N", new Vector3(0f, 1.5f, 8.4f), new Vector3(0.3f, 3f, 3.2f), env);     // z 6.8 .. 10
            // Crates (cover / obstacles)
            Box(level, "Crate_W1", new Vector3(-6f, 0.6f, -4f), new Vector3(1.2f, 1.2f, 1.2f), env);
            Box(level, "Crate_W2", new Vector3(-11f, 0.6f, -7f), new Vector3(2.4f, 1.2f, 1.2f), env);
            Box(level, "Crate_E1", new Vector3(5f, 0.6f, 2f), new Vector3(1.2f, 1.2f, 2.4f), env);
            Box(level, "Crate_E2", new Vector3(10f, 0.6f, 5f), new Vector3(1.2f, 1.2f, 1.2f), env);

            // NavMesh
            var surfaceGo = new GameObject("NavMeshSurface");
            var surface = surfaceGo.AddComponent<NavMeshSurface>();
            surface.collectObjects = CollectObjects.All;
            surface.useGeometry = NavMeshCollectGeometry.PhysicsColliders;
            surface.layerMask = 1 << env;
            surface.BuildNavMesh();
            string navPath = Path.ChangeExtension(scenePath, null) + "_NavMesh.asset";
            AssetDatabase.DeleteAsset(navPath);
            AssetDatabase.CreateAsset(surface.navMeshData, navPath);

            // Waypoint graph: a loose grid in each room + both sides of the doorway.
            var graph = new GameObject("NavGraph").AddComponent<NavGraph>();
            int n = 0;
            // Corners = AmbushSpot, room centres = Junction (the Ambusher only waits on tagged nodes, guide §12.3).
            NodeTag TagFor(float x, float z) =>
                (Mathf.Abs(x) == 12f || Mathf.Abs(x) == 3f) && (z == -7f || z == 6f) ? NodeTag.Room | NodeTag.AmbushSpot
                : z == -1f && (x == -7f || x == 8f) ? NodeTag.Room | NodeTag.Junction | NodeTag.Hub
                : NodeTag.Room;
            foreach (float x in new[] { -12f, -7f, -3f })
                foreach (float z in new[] { -7f, -1f, 6f })
                    Node(graph.transform, ref n, new Vector3(x, 0f, z), TagFor(x, z));
            Node(graph.transform, ref n, new Vector3(-1.5f, 0f, 5f), NodeTag.Doorway);
            Node(graph.transform, ref n, new Vector3(1.5f, 0f, 5f), NodeTag.Doorway);
            foreach (float x in new[] { 3f, 8f, 12f })
                foreach (float z in new[] { -7f, -1f, 6f })
                    Node(graph.transform, ref n, new Vector3(x, 0f, z), TagFor(x, z));

            // Zones
            var zones = new GameObject("Zones").transform;
            // 9 zones (quarters of each room + the doorway), non-overlapping, so the Ambusher has routes to learn.
            Zone(zones, "West_SW", Box(-15f, -7.5f, -10f, 2f)); Zone(zones, "West_SE", Box(-7.5f, 0f, -10f, 2f));
            Zone(zones, "West_NW", Box(-15f, -7.5f, 2f, 10f));  Zone(zones, "West_NE", Box(-7.5f, -2f, 2f, 10f));
            Zone(zones, "Doorway", Box(-2f, 2f, 2f, 10f));
            Zone(zones, "East_NW", Box(2f, 7.5f, 2f, 10f));     Zone(zones, "East_NE", Box(7.5f, 15f, 2f, 10f));
            Zone(zones, "East_SW", Box(0f, 7.5f, -10f, 2f));     Zone(zones, "East_SE", Box(7.5f, 15f, -10f, 2f));
            int reachable = NavGraphTools.AutoLink(graph);     // after the zones: it also stamps each node's zone

            // Systems
            var systems = new GameObject("Systems");
            systems.AddComponent<AudioService>().library = SoundLibraryBuilder.Build();
            var music = systems.AddComponent<TensionMusic>();
            music.calmLoop = AssetDatabase.LoadAssetAtPath<AudioClip>("Assets/Audio/Music/DarkDrone_Loop.wav");
            music.huntLoop = AssetDatabase.LoadAssetAtPath<AudioClip>("Assets/Audio/Music/HorrorDrone_Loop.wav");

            // Ambience: audio only, never SoundEvents (agents must not investigate decoration, guide §5.4).
            var amb = new GameObject("Ambience").transform;
            Ambient(amb, "RoomTone_West", "RoomTone_Loop", new Vector3(-8f, 2.5f, 0f), 0.35f, 4f, 16f);
            Ambient(amb, "RoomTone_East", "RoomTone_Loop", new Vector3(8f, 2.5f, 0f), 0.35f, 4f, 16f);
            Ambient(amb, "Hum_WestWall", "IndustrialHum_Loop", new Vector3(-14f, 1.5f, 6f), 0.4f, 2f, 14f);
            Ambient(amb, "Vent_East", "AirVent_Loop", new Vector3(13f, 2.8f, -8f), 0.5f, 1.5f, 10f);
            Ambient(amb, "Drip_West", "WaterDrip_Loop", new Vector3(-13f, 0.5f, -8f), 0.5f, 1f, 9f);
            systems.AddComponent<SoundDebug>();
            systems.AddComponent<AgentDebugOverlay>();

            // Player (copied fully wired from the interaction sandbox) and agent
            bool playerOk = CopyPlayer();
            ApplyLook(level);
            var agent = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            agent.transform.SetPositionAndRotation(AgentHome, Quaternion.Euler(0f, 90f, 0f));

            Directory.CreateDirectory(Path.GetDirectoryName(scenePath));
            EditorSceneManager.SaveScene(scene, scenePath);
            Selection.activeGameObject = agent;
            Debug.Log($"[AgentSandbox] Built {scenePath} with {spec.Name}: {n} nodes ({reachable} connected), player copied: {playerOk}.");
        }

        // ------------------------------------------------------------------ look (matches SampleScene's horror setup)
        const string HorrorMats = "Assets/Art/Materials/Horror/";

        static void ApplyLook(Transform level)
        {
            var floor = TiledCopy(HorrorMats + "Floor Tile Mat Horror.mat", "Sandbox_Floor", new Vector2(10f, 7f));
            var wallLong = TiledCopy(HorrorMats + "Walls Horror.mat", "Sandbox_Wall_Long", new Vector2(8f, 1f));
            var wallShort = TiledCopy(HorrorMats + "Walls Horror.mat", "Sandbox_Wall_Short", new Vector2(4f, 1f));
            var crate = AssetDatabase.LoadAssetAtPath<Material>(HorrorMats + "Crate Short Mat Horror.mat");
            foreach (Transform t in level)
            {
                var r = t.GetComponent<Renderer>(); if (r == null) continue;
                var m = t.name == "Floor" ? floor
                      : t.name.StartsWith("Crate") ? crate
                      : t.localScale.x > 10f || t.localScale.z > 10f ? wallLong : wallShort;
                if (m != null) r.sharedMaterial = m;
            }

            // Fog, ambient and sky as in SampleScene.
            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.ExponentialSquared;
            RenderSettings.fogColor = new Color(0.012f, 0.013f, 0.015f);
            RenderSettings.fogDensity = 0.028f;
            RenderSettings.ambientMode = AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.03f, 0.035f, 0.04f);   // a touch above SampleScene: no ceiling bounce here
            var sky = AssetDatabase.FindAssets("NightLabSky t:Material");
            if (sky.Length > 0) RenderSettings.skybox = AssetDatabase.LoadAssetAtPath<Material>(AssetDatabase.GUIDToAssetPath(sky[0]));

            // Post-processing: the team's horror profile.
            var profile = AssetDatabase.LoadAssetAtPath<VolumeProfile>("Assets/Settings/EchoHorrorProfile.asset");
            if (profile != null)
            {
                var vol = new GameObject("Global Volume").AddComponent<Volume>();
                vol.isGlobal = true; vol.sharedProfile = profile;
            }

            // Lamps: cold ceiling lights, a sick green one, a red emergency light at the doorway.
            var lamps = new GameObject("Lamps").transform;
            Color cold = new(0.8f, 0.89f, 0.92f), green = new(0.72f, 0.84f, 0.78f), red = new(1f, 0.08f, 0.05f);
            Lamp(lamps, "Lamp_W1", new Vector3(-10f, 2.8f, -4f), cold, 5f, 10f);
            Lamp(lamps, "Lamp_W2", new Vector3(-6f, 2.8f, 5f), cold, 4f, 9f);
            Lamp(lamps, "Lamp_E1", new Vector3(6f, 2.8f, -5f), cold, 5f, 10f);
            Lamp(lamps, "Lamp_E2", new Vector3(11f, 2.8f, 4f), green, 3.5f, 9f);
            Lamp(lamps, "Emergency_Door", new Vector3(0f, 2.6f, 5f), red, 2.2f, 6f);
        }

        static Material TiledCopy(string source, string name, Vector2 tiling)
        {
            var src = AssetDatabase.LoadAssetAtPath<Material>(source);
            if (src == null) return null;
            const string dir = "Assets/Art/Materials/Sandbox";
            Directory.CreateDirectory(dir);
            string path = $"{dir}/{name}.mat";
            var m = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (m == null) { m = new Material(src); AssetDatabase.CreateAsset(m, path); }
            else m.CopyPropertiesFromMaterial(src);
            m.SetTextureScale("_BaseMap", tiling);
            EditorUtility.SetDirty(m);
            return m;
        }

        static void Lamp(Transform parent, string name, Vector3 pos, Color c, float intensity, float range)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false); go.transform.position = pos;
            var l = go.AddComponent<Light>();
            l.type = LightType.Point; l.color = c; l.intensity = intensity; l.range = range;
            l.shadows = LightShadows.Soft;
        }

        /// Same flashlight as SampleScene's player (PlayerCameraRoot/Main Camera/Flashlight), and post-processing on.
        static void SetUpPlayerCamera(GameObject player)
        {
            var cam = player.GetComponentInChildren<Camera>(true);
            if (cam == null) return;
            cam.GetUniversalAdditionalCameraData().renderPostProcessing = true;
            if (cam.transform.Find("Flashlight") != null) return;
            var go = new GameObject("Flashlight");
            go.transform.SetParent(cam.transform, false);
            var l = go.AddComponent<Light>();
            l.type = LightType.Spot; l.color = new Color(0.91f, 0.93f, 0.95f);
            l.intensity = 70f; l.range = 25f; l.spotAngle = 55f; l.innerSpotAngle = 28f; l.shadows = LightShadows.Soft;
        }

        static bool CopyPlayer() => CopyPlayer(PlayerSpawn, Quaternion.Euler(0f, -60f, 0f));

        /// Copies the fully wired PlayerCapsule + InteractionHUD from Sandbox_Interaction into the active scene.
        internal static bool CopyPlayer(Vector3 spawn, Quaternion rotation)
        {
            if (!File.Exists(PlayerSourceScene)) { Debug.LogWarning($"[ListenerSandboxBuilder] {PlayerSourceScene} missing: add a player by hand."); return false; }
            var target = EditorSceneManager.GetActiveScene();
            var source = EditorSceneManager.OpenScene(PlayerSourceScene, OpenSceneMode.Additive);
            bool found = false;
            try
            {
                foreach (var root in source.GetRootGameObjects())
                {
                    if (root.name != "PlayerCapsule" && root.name != "InteractionHUD") continue;
                    var copy = Object.Instantiate(root);
                    copy.name = root.name;
                    UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(copy, target);
                    if (root.name == "PlayerCapsule")
                    {
                        found = true;
                        copy.transform.SetPositionAndRotation(spawn, rotation);
                        SetUpPlayerCamera(copy);
                    }
                }
            }
            finally
            {
                EditorSceneManager.CloseScene(source, true);
            }
            return found;
        }

        static GameObject Box(Transform parent, string name, Vector3 pos, Vector3 size, int layer)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name; go.layer = layer; go.isStatic = true;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = pos; go.transform.localScale = size;
            return go;
        }

        static void Ambient(Transform parent, string name, string clip, Vector3 pos, float volume, float min, float max)
        {
            var c = AssetDatabase.LoadAssetAtPath<AudioClip>($"Assets/Audio/Ambience/{clip}.wav");
            if (c == null) return;
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.position = pos;
            var s = go.AddComponent<AudioSource>();
            s.clip = c; s.loop = true; s.playOnAwake = true; s.volume = volume;
            s.spatialBlend = 1f; s.dopplerLevel = 0f; s.minDistance = min; s.maxDistance = max;
            s.rolloffMode = AudioRolloffMode.Linear;
            s.time = Random.Range(0f, c.length * 0.9f);   // rooms don't pulse in sync
        }

        static void Node(Transform parent, ref int n, Vector3 pos, NodeTag tag)
        {
            var go = new GameObject($"Node_{n++:00}");
            go.transform.SetParent(parent, false);
            if (NavMesh.SamplePosition(pos, out var hit, 2f, NavMesh.AllAreas)) pos = hit.position;
            go.transform.position = pos;
            go.AddComponent<NavNode>().tags = tag;
        }

        static (Vector3 centre, Vector3 size) Box(float x0, float x1, float z0, float z1)
            => (new Vector3((x0 + x1) * 0.5f, 1.5f, (z0 + z1) * 0.5f), new Vector3(x1 - x0, 3f, z1 - z0));

        static void Zone(Transform parent, string id, (Vector3 centre, Vector3 size) box) => Zone(parent, id, box.centre, box.size);

        static void Zone(Transform parent, string id, Vector3 centre, Vector3 size)
        {
            var go = new GameObject("Zone_" + id);
            go.transform.SetParent(parent, false);
            go.transform.position = centre;
            go.layer = LayerMask.NameToLayer("Ignore Raycast");
            go.AddComponent<BoxCollider>().size = size;
            go.AddComponent<ZoneVolume>().zoneId = id;
        }
    }
}
