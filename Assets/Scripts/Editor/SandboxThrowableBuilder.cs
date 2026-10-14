using Echo.DevTools;
using Echo.Interaction;
using StarterAssets;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Echo.EditorTools
{
    /// ECHO > Sandbox > Build Throwable Test Area. (Re)builds the throwable area south of the sandbox spawn
    /// from the shared prefabs: a brick, a metal basin, a 40 kg crate, a 25 kg barrel and a 5 cm thin wall.
    /// Adds a PhysicsCarrier to the sandbox player, lets the player nudge light props (BasicRigidBodyPush,
    /// mask = Default until the Pushable layer exists) and wires the self-test. Refuses any scene but the sandbox.
    static class SandboxThrowableBuilder
    {
        const string RootName = "ThrowableTestArea";
        // Floor 12 x 12 m whose north edge meets the spawn ground (z = -5).
        static readonly Vector3 RootPosition = new(0f, 0f, -11f);

        [MenuItem("ECHO/Sandbox/Build Throwable Test Area")]
        static void Build()
        {
            var scene = EditorSceneManager.GetActiveScene();
            if (scene.path != SandboxDoorBuilder.ScenePath)
            {
                Debug.LogError($"[SandboxThrowableBuilder] Open {SandboxDoorBuilder.ScenePath} first. This tool only edits the sandbox.");
                return;
            }
            if (EditorApplication.isPlayingOrWillChangePlaymode) return;

            ThrowablePrefabBuilder.BuildMissing();
            var brickPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(ThrowablePrefabBuilder.BrickPath);
            var basinPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(ThrowablePrefabBuilder.BasinPath);
            var cratePrefab = AssetDatabase.LoadAssetAtPath<GameObject>(ThrowablePrefabBuilder.CratePath);
            var barrelPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(ThrowablePrefabBuilder.BarrelPath);
            if (brickPrefab == null || basinPrefab == null || cratePrefab == null || barrelPrefab == null) return;

            var old = GameObject.Find(RootName);
            if (old != null) Undo.DestroyObjectImmediate(old);
            var root = new GameObject(RootName);
            Undo.RegisterCreatedObjectUndo(root, "Build Throwable Test Area");
            root.transform.position = RootPosition;

            Box(root.transform, "Floor", new Vector3(0f, -0.1f, 0f), new Vector3(12f, 0.2f, 12f));
            // 5 cm: thinner than one physics step of a 12 m/s throw (0.24 m at 50 Hz), so only CCD stops it.
            Collider thinWall = Box(root.transform, "ThinWall_5cm", new Vector3(0f, 1.5f, -4f), new Vector3(4f, 3f, 0.05f));

            Carryable brick = Place(brickPrefab, root.transform, "Throwable_Brick", new Vector3(-1.5f, 0.15f, 2f));
            Carryable basin = Place(basinPrefab, root.transform, "Throwable_MetalBasin", new Vector3(0f, 0.05f, 2f));
            Carryable crate = Place(cratePrefab, root.transform, "Barricade_CrateLong", new Vector3(2.5f, 0.02f, 2f));
            Place(barrelPrefab, root.transform, "Barricade_Barrel", new Vector3(4.5f, 0.02f, 2f));

            var interactor = Object.FindFirstObjectByType<PlayerInteractor>();
            PhysicsCarrier carrier = null;
            if (interactor != null)
            {
                carrier = interactor.GetComponent<PhysicsCarrier>();
                if (carrier == null) carrier = Undo.AddComponent<PhysicsCarrier>(interactor.gameObject);

                // Walking into light props nudges them. The Starter Assets script already filters by its serialized
                // pushLayers mask; only the instance values are set (scene override, the script is untouched).
                // Heavy props barely move: the impulse is mass-independent and their floor friction is larger.
                var push = interactor.GetComponent<BasicRigidBodyPush>();
                if (push != null)
                {
                    Undo.RecordObject(push, "Enable prop push");
                    push.canPush = true;
                    if (push.pushLayers.value == 0) push.pushLayers = 1 << 0;   // Default, until a Pushable layer exists
                    EditorUtility.SetDirty(push);
                }
            }

            var test = Object.FindFirstObjectByType<InteractionSelfTest>();
            if (test != null)
            {
                Undo.RecordObject(test, "Wire self-test throwables");
                test.carrier = carrier;
                test.brick = brick;
                test.basin = basin;
                test.heavyCrate = crate;
                test.thinWall = thinWall;
                EditorUtility.SetDirty(test);
            }

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            Selection.activeGameObject = root;
            Debug.Log($"[SandboxThrowableBuilder] Built {RootName}: brick, metal basin, 40 kg crate, 25 kg barrel, 5 cm wall. " +
                      $"PhysicsCarrier on player: {carrier != null}. Self-test wired: {test != null}.");
        }

        static Collider Box(Transform parent, string name, Vector3 localPos, Vector3 size)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            go.transform.localScale = size;
            GameObjectUtility.SetStaticEditorFlags(go, StaticEditorFlags.BatchingStatic);
            return go.GetComponent<Collider>();
        }

        static Carryable Place(GameObject prefab, Transform parent, string name, Vector3 localPos)
        {
            var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab, parent);
            go.name = name;
            go.transform.localPosition = localPos;
            // Rest the collider's underside 1 cm above the floor, whatever the kit pivot is.
            Physics.SyncTransforms();
            float bottom = float.MaxValue;
            foreach (Collider c in go.GetComponentsInChildren<Collider>()) bottom = Mathf.Min(bottom, c.bounds.min.y);
            if (bottom < float.MaxValue) go.transform.position += Vector3.up * (parent.position.y + 0.01f - bottom);
            return go.GetComponent<Carryable>();
        }
    }
}
