using Echo.DevTools;
using Echo.Interaction;
using Echo.Navigation;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Echo.EditorTools
{
    /// ECHO > Sandbox > Build Door Test Area. (Re)builds the sandbox door area from the shared prefabs
    /// (Door_Slide, Switch_Fusebox): a normal door and a locked door unlocked by a fusebox switch.
    /// Reproducible scene setup; refuses to touch any other scene.
    static class SandboxDoorBuilder
    {
        public const string ScenePath = "Assets/Scenes/Sandbox/Sandbox_Interaction.unity";
        const string RootName = "DoorTestArea";

        // Kit wall pieces are planes in local YZ; rotated 90° about Y the wall runs along world X and the
        // door's local +X faces the spawn (world -Z).
        static readonly Quaternion WallRotation = Quaternion.Euler(0f, 90f, 0f);
        // Fusebox position in the door's local space: on the spawn-side wall face, left of the doorway.
        static readonly Vector3 SwitchLocalPos = new(0.15f, 1.9f, 1.3f);

        [MenuItem("ECHO/Sandbox/Build Door Test Area")]
        static void Build()
        {
            var scene = EditorSceneManager.GetActiveScene();
            if (scene.path != ScenePath)
            {
                Debug.LogError($"[SandboxDoorBuilder] Open {ScenePath} first. This tool only edits the sandbox.");
                return;
            }
            if (EditorApplication.isPlayingOrWillChangePlaymode) return;

            InteractablePrefabBuilder.BuildMissing();
            var doorPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(InteractablePrefabBuilder.DoorPrefabPath);
            var switchPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(InteractablePrefabBuilder.SwitchPrefabPath);
            if (doorPrefab == null || switchPrefab == null) return;

            var old = GameObject.Find(RootName);
            if (old != null) Undo.DestroyObjectImmediate(old);
            var root = new GameObject(RootName);
            Undo.RegisterCreatedObjectUndo(root, "Build Door Test Area");

            var floor = GameObject.CreatePrimitive(PrimitiveType.Cube);
            floor.name = "Floor";
            floor.transform.SetParent(root.transform, false);
            floor.transform.localPosition = new Vector3(0f, -0.1f, 10.5f);
            floor.transform.localScale = new Vector3(24f, 0.2f, 11f);
            GameObjectUtility.SetStaticEditorFlags(floor, StaticEditorFlags.BatchingStatic);

            Door normal = PlaceDoor(doorPrefab, root.transform, "Door_Normal", new Vector3(-7f, 0f, 7.5f), false);
            Door locked = PlaceDoor(doorPrefab, root.transform, "Door_Locked", new Vector3(1f, 0f, 7.5f), true);
            SwitchInteractable sw = PlaceSwitch(switchPrefab, root.transform, locked);

            var test = Object.FindFirstObjectByType<InteractionSelfTest>();
            if (test != null)
            {
                Undo.RecordObject(test, "Wire self-test doors");
                test.normalDoor = normal;
                test.lockedDoor = locked;
                test.unlockSwitch = sw;
                EditorUtility.SetDirty(test);
            }

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            Selection.activeGameObject = root;
            Debug.Log($"[SandboxDoorBuilder] Built {RootName} from prefabs: Door_Normal, Door_Locked + UnlockSwitch. Self-test wired: {test != null}.");
        }

        /// Same as the self-test component's context menu, but scriptable (MCP / keyboard-free).
        [MenuItem("ECHO/Sandbox/Run Self-Test")]
        static void RunSelfTest()
        {
            var test = Object.FindFirstObjectByType<InteractionSelfTest>();
            if (test == null) Debug.LogWarning("[SelfTest] No InteractionSelfTest in the open scene.");
            else test.RunSelfTest();
        }

        [MenuItem("ECHO/Sandbox/Run Self-Test", true)]
        static bool CanRunSelfTest() => EditorApplication.isPlaying;

        static Door PlaceDoor(GameObject prefab, Transform parent, string name, Vector3 position, bool startLocked)
        {
            var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab, parent);
            go.name = name;
            go.transform.SetLocalPositionAndRotation(position, WallRotation);
            Door door = go.GetComponentInChildren<Door>();
            InteractablePrefabBuilder.SetStartLocked(door, startLocked);
            return door;
        }

        static SwitchInteractable PlaceSwitch(GameObject prefab, Transform parent, Door door)
        {
            Transform doorRoot = door.transform.parent;
            var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab, parent);
            go.name = "UnlockSwitch";
            go.transform.SetPositionAndRotation(doorRoot.TransformPoint(SwitchLocalPos), doorRoot.rotation);
            var sw = go.GetComponent<SwitchInteractable>();
            InteractablePrefabBuilder.WireUnlock(sw, door);
            return sw;
        }
    }
}
