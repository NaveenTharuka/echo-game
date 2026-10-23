using Echo.AI;
using Echo.Gameplay;
using Echo.Interaction;
using Echo.Navigation;
using UnityEditor;
using UnityEditor.Events;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.Rendering;

namespace Echo.EditorTools
{
    /// ECHO > SampleScene > 7. Objectives and Patrols (Phase 2). Re-runnable: it moves / rebuilds what it made before.
    ///   - Fusebox (Switch_Fusebox_Power) moves to the warehouse's north-west wall, 10 m from the Tracker's post, deep in
    ///     its territory and a dead end (you come back past it). Its old "unlock the Lab door" event is removed: the
    ///     ObjectiveDirector opens the door only with the access pass AND power.
    ///   - Medicine pickup on the cabinet in the Containment ward (the Lab).
    ///   - Exit point at the player's spawn; ObjectiveDirector wired to all of it.
    ///   - AgentPatrol territories for the four agents (floor plan v5).
    public static class SampleSceneObjectiveSetup
    {
        const string ScenePath = "Assets/Scenes/SampleScene.unity";
        const string MaterialFolder = "Assets/Art/Materials/Objectives";

        // Fusebox: west wall of the warehouse, the free stretch next to the crates in the NW corner.
        static readonly Vector3 FuseboxProbe = new(47.5f, -21.6f, 84f);
        const float FuseboxHeight = 2.4f;                    // pivot above the floor, as before
        static readonly Vector3 MedicineSpot = new(107f, -9.8f, 6.5f);   // above the cabinet top (y -11.01)

        static readonly (string agent, string[] zones, AgentMotor.Gait gait)[] Territories =
        {
            ("Listener", new[] { "EntranceHall_E", "EntranceHall_W", "EntranceCorridor_E", "Ramp" }, AgentMotor.Gait.Walk),
            ("Ambusher", new[] { "CorridorTee_N", "CorridorTee_S", "CorridorCorner", "Passthrough" }, AgentMotor.Gait.Sneak),
            ("Tracker", new[] { "Warehouse_W", "Warehouse_C", "Bay_S", "Bay_N" }, AgentMotor.Gait.Walk),
            ("Mimic", new[] { "LabStairs", "Lab" }, AgentMotor.Gait.Sneak),
        };

        [MenuItem("ECHO/SampleScene/7. Objectives and Patrols (fusebox, medicine, exit, patrols)")]
        static void Menu()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) return;
            if (EditorSceneManager.GetActiveScene().path != ScenePath)
            {
                if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
                EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            }
            Run();
        }

        public static void Run()
        {
            var scene = EditorSceneManager.GetActiveScene();
            var fusebox = MoveFusebox();
            var pass = FindSwitch("Lab Access Pass - Highlighted Pickup");   // the key card (Key room)
            if (pass != null)
            {
                pass.verb = "Take the key card"; pass.activatedMessage = "Key card taken";
                EditorUtility.SetDirty(pass);
            }
            var medicine = BuildMedicine();
            var labDoor = FindDoor("Door_Bulkhead_Containment");
            var exit = BuildExit();
            int patrols = SetupPatrols();

            var director = Object.FindFirstObjectByType<ObjectiveDirector>();
            if (director == null) director = new GameObject("Objectives").AddComponent<ObjectiveDirector>();
            director.labDoor = labDoor; director.powerSwitch = fusebox; director.accessPass = pass;
            director.medicine = medicine; director.exit = exit;
            EditorUtility.SetDirty(director);

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            Debug.Log($"[ObjectiveSetup] fusebox {(fusebox ? fusebox.transform.position.ToString() : "MISSING")}, pass {(pass ? "ok" : "MISSING")}, " +
                      $"medicine {(medicine ? medicine.transform.position.ToString() : "MISSING")}, Lab door {(labDoor ? "ok" : "MISSING")}, " +
                      $"exit {exit.position}, patrols on {patrols} agents.");
        }

        // ------------------------------------------------------------------ fusebox

        static SwitchInteractable MoveFusebox()
        {
            var sw = FindSwitch("Switch_Fusebox_Power");
            if (sw == null) return null;
            Undo.RecordObject(sw.transform, "Move fusebox");
            // Ignore the fusebox itself when probing (re-runs: it may already hang on this wall).
            var cols = sw.GetComponentsInChildren<Collider>();
            foreach (var c in cols) c.enabled = false;
            if (Physics.Raycast(FuseboxProbe, Vector3.left, out var wall, 5f, ~0, QueryTriggerInteraction.Ignore) &&
                Physics.Raycast(FuseboxProbe + Vector3.right * 1f, Vector3.down, out var floor, 6f, ~0, QueryTriggerInteraction.Ignore))
            {
                Vector3 n = wall.normal; n.y = 0f; n.Normalize();
                // The box faces out along its local +X (its lamp sits on that side): right = wall normal.
                sw.transform.SetPositionAndRotation(
                    new Vector3(wall.point.x, floor.point.y + FuseboxHeight, wall.point.z) + n * 0.05f,
                    Quaternion.LookRotation(Vector3.Cross(n, Vector3.up), Vector3.up));
            }
            else Debug.LogError("[ObjectiveSetup] no wall / floor found for the fusebox");
            foreach (var c in cols) c.enabled = true;

            // Its door-unlock event goes: the director decides (pass AND power).
            for (int i = sw.onActivated.GetPersistentEventCount() - 1; i >= 0; i--)
                if (sw.onActivated.GetPersistentTarget(i) is Door) UnityEventTools.RemovePersistentListener(sw.onActivated, i);
            sw.verb = "Restore power";
            sw.activatedMessage = "";            // the director says it (and that it was loud)
            EditorUtility.SetDirty(sw);

            // A faint red glow so it can be found in the dark corner - but only from close by.
            var glow = sw.transform.Find("FuseboxGlow");
            if (glow == null)
            {
                glow = new GameObject("FuseboxGlow").transform;
                glow.SetParent(sw.transform, false);
                var l = glow.gameObject.AddComponent<Light>();
                l.type = LightType.Point; l.color = new Color(1f, 0.15f, 0.08f); l.range = 2.5f; l.intensity = 0.8f;
                l.shadows = LightShadows.None;
            }
            glow.localPosition = new Vector3(0.35f, 0.2f, 0f);
            return sw;
        }

        // ------------------------------------------------------------------ medicine

        static SwitchInteractable BuildMedicine()
        {
            var old = GameObject.Find("Medicine_Pickup");
            if (old != null) Object.DestroyImmediate(old);
            if (!Physics.Raycast(MedicineSpot, Vector3.down, out var top, 3f, ~0, QueryTriggerInteraction.Ignore))
            { Debug.LogError("[ObjectiveSetup] no surface for the medicine"); return null; }

            var parent = GameObject.Find("Build/Section_2");
            var root = new GameObject("Medicine_Pickup");
            if (parent != null) root.transform.SetParent(parent.transform, true);
            root.transform.SetPositionAndRotation(top.point, Quaternion.Euler(0f, 205f, 0f));

            var white = Mat("Medicine_Case", new Color(0.86f, 0.87f, 0.84f), 0.55f, Color.black);
            var red = Mat("Medicine_Cross", new Color(0.75f, 0.05f, 0.04f), 0.4f, new Color(0.25f, 0f, 0f));
            var latch = Mat("Medicine_Latch", new Color(0.35f, 0.36f, 0.38f), 0.7f, Color.black, metallic: 0.9f);

            Box(root.transform, "Case", new Vector3(0f, 0.075f, 0f), new Vector3(0.34f, 0.15f, 0.24f), white);
            Box(root.transform, "Lid seam", new Vector3(0f, 0.11f, 0f), new Vector3(0.345f, 0.006f, 0.245f), latch);
            Box(root.transform, "Cross H", new Vector3(0f, 0.152f, 0f), new Vector3(0.12f, 0.004f, 0.035f), red);
            Box(root.transform, "Cross V", new Vector3(0f, 0.152f, 0f), new Vector3(0.035f, 0.004f, 0.12f), red);
            Box(root.transform, "Handle", new Vector3(0f, 0.09f, -0.125f), new Vector3(0.12f, 0.025f, 0.012f), latch);
            Box(root.transform, "Latch L", new Vector3(-0.1f, 0.1f, -0.122f), new Vector3(0.03f, 0.03f, 0.006f), latch);
            Box(root.transform, "Latch R", new Vector3(0.1f, 0.1f, -0.122f), new Vector3(0.03f, 0.03f, 0.006f), latch);

            // A cold work light over it, like the access pass's: the one lit thing in a dark ward.
            var lightGo = new GameObject("Medicine Spotlight");
            lightGo.transform.SetParent(root.transform, false);
            lightGo.transform.localPosition = new Vector3(0f, 1.4f, 0f);
            lightGo.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
            var spot = lightGo.AddComponent<Light>();
            spot.type = LightType.Spot; spot.color = new Color(0.75f, 0.9f, 1f); spot.intensity = 6f; spot.range = 3f;
            spot.spotAngle = 40f; spot.innerSpotAngle = 18f; spot.shadows = LightShadows.Soft;

            var col = root.AddComponent<BoxCollider>();
            col.isTrigger = true; col.center = new Vector3(0f, 0.1f, 0f); col.size = new Vector3(0.5f, 0.25f, 0.4f);

            var sw = root.AddComponent<SwitchInteractable>();
            sw.verb = "Take the medicine"; sw.singleUse = true;
            sw.noise = NoiseType.ObjectDrop; sw.noiseIntensity = 0.1f;
            UnityEventTools.AddBoolPersistentListener(sw.onActivated, root.SetActive, false);
            Undo.RegisterCreatedObjectUndo(root, "Medicine");
            return sw;
        }

        static void Box(Transform parent, string name, Vector3 pos, Vector3 size, Material m)
        {
            var b = GameObject.CreatePrimitive(PrimitiveType.Cube);
            b.name = name;
            Object.DestroyImmediate(b.GetComponent<Collider>());
            b.transform.SetParent(parent, false);
            b.transform.localPosition = pos; b.transform.localScale = size;
            var r = b.GetComponent<MeshRenderer>(); r.sharedMaterial = m; r.shadowCastingMode = ShadowCastingMode.On;
        }

        static Material Mat(string name, Color c, float smooth, Color emission, float metallic = 0f)
        {
            if (!AssetDatabase.IsValidFolder(MaterialFolder))
            {
                if (!AssetDatabase.IsValidFolder("Assets/Art/Materials")) AssetDatabase.CreateFolder("Assets/Art", "Materials");
                AssetDatabase.CreateFolder("Assets/Art/Materials", "Objectives");
            }
            string path = $"{MaterialFolder}/{name}.mat";
            var m = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (m == null) { m = new Material(Shader.Find("Universal Render Pipeline/Lit")); AssetDatabase.CreateAsset(m, path); }
            m.SetColor("_BaseColor", c); m.SetFloat("_Smoothness", smooth); m.SetFloat("_Metallic", metallic);
            if (emission != Color.black) { m.EnableKeyword("_EMISSION"); m.SetColor("_EmissionColor", emission); }
            EditorUtility.SetDirty(m);
            return m;
        }

        // ------------------------------------------------------------------ exit and patrols

        static Transform BuildExit()
        {
            var exit = GameObject.Find("Exit_Point");
            if (exit == null) exit = new GameObject("Exit_Point");
            var player = GameObject.Find("PlayerCapsule");
            exit.transform.position = player != null ? player.transform.position : new Vector3(24f, 0.05f, 68.6f);
            return exit.transform;
        }

        static int SetupPatrols()
        {
            var agents = GameObject.Find("Agents");
            if (agents == null) return 0;
            int n = 0;
            foreach (Transform a in agents.transform)
            foreach (var (agent, zones, gait) in Territories)
            {
                if (!a.name.StartsWith(agent)) continue;
                if (!a.TryGetComponent(out AgentPatrol p)) p = Undo.AddComponent<AgentPatrol>(a.gameObject);
                p.zones = zones; p.gait = gait; p.patrolEnabled = true;
                EditorUtility.SetDirty(p);
                n++;
            }
            return n;
        }

        static SwitchInteractable FindSwitch(string name)
        {
            foreach (var s in Object.FindObjectsByType<SwitchInteractable>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                if (s.name == name) return s;
            return null;
        }

        static Door FindDoor(string parentName)
        {
            foreach (var d in Object.FindObjectsByType<Door>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                if (d.transform.parent != null && d.transform.parent.name == parentName) return d;
            return null;
        }
    }
}
