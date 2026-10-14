using Echo.DevTools;
using Echo.Interaction;
using Echo.Player;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Echo.EditorTools
{
    /// ECHO > Sandbox > Build Player Test Area. (Re)builds the movement area east of the sandbox spawn - a staircase
    /// (0.18 m risers, 0.3 m treads, up to a 0.9 m platform) and a 0.35 m ledge (above the 0.25 m step offset) -
    /// adds PlayerNoise, HitscanWeapon and PlayerDeath to the sandbox player and wires the self-test.
    /// Refuses any scene but the sandbox.
    static class SandboxPlayerBuilder
    {
        const string RootName = "PlayerTestArea";
        // Floor 12 x 10 m whose west edge meets the spawn ground (x = 5).
        static readonly Vector3 RootPosition = new(11f, 0f, 0f);
        public const float Riser = 0.18f, Tread = 0.3f;
        public const int Steps = 4;   // + the platform = 5 risers, 0.9 m

        [MenuItem("ECHO/Sandbox/Build Player Test Area")]
        static void Build()
        {
            var scene = EditorSceneManager.GetActiveScene();
            if (scene.path != SandboxDoorBuilder.ScenePath)
            {
                Debug.LogError($"[SandboxPlayerBuilder] Open {SandboxDoorBuilder.ScenePath} first. This tool only edits the sandbox.");
                return;
            }
            if (EditorApplication.isPlayingOrWillChangePlaymode) return;

            var old = GameObject.Find(RootName);
            if (old != null) Undo.DestroyObjectImmediate(old);
            var root = new GameObject(RootName);
            Undo.RegisterCreatedObjectUndo(root, "Build Player Test Area");
            root.transform.position = RootPosition;

            Box(root.transform, "Floor", new Vector3(0f, -0.1f, 0f), new Vector3(12f, 0.2f, 10f));

            // Staircase along +X, 2 m wide, centred on z = -2.5.
            float x0 = -4f;
            for (int i = 0; i < Steps; i++)
            {
                float h = (i + 1) * Riser;
                Box(root.transform, $"Step_{i + 1}", new Vector3(x0 + Tread * (i + 0.5f), h * 0.5f, -2.5f), new Vector3(Tread, h, 2f));
            }
            float top = (Steps + 1) * Riser;
            float platformStart = x0 + Tread * Steps;
            Box(root.transform, "Platform", new Vector3(platformStart + 1.25f, top * 0.5f, -2.5f), new Vector3(2.5f, top, 2f));
            Marker(root.transform, "StairsStart", new Vector3(x0 - 1.5f, 0f, -2.5f));

            // 0.35 m ledge: higher than the CharacterController step offset (0.25 m), so it must block.
            Box(root.transform, "Ledge_35cm", new Vector3(-2f, 0.175f, 2.5f), new Vector3(1f, 0.35f, 2f));
            Marker(root.transform, "LedgeStart", new Vector3(-5f, 0f, 2.5f));

            var interactor = Object.FindFirstObjectByType<PlayerInteractor>();
            int added = 0;
            if (interactor != null)
            {
                GameObject player = interactor.gameObject;
                if (player.GetComponent<PlayerNoise>() == null) { Undo.AddComponent<PlayerNoise>(player); added++; }
                if (player.GetComponent<HitscanWeapon>() == null) { Undo.AddComponent<HitscanWeapon>(player); added++; }
                if (player.GetComponent<PlayerDeath>() == null) { Undo.AddComponent<PlayerDeath>(player); added++; }
            }

            var test = Object.FindFirstObjectByType<InteractionSelfTest>();
            if (test != null)
            {
                Undo.RecordObject(test, "Wire self-test player area");
                test.playerArea = root.transform;
                EditorUtility.SetDirty(test);
            }

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            Selection.activeGameObject = root;
            Debug.Log($"[SandboxPlayerBuilder] Built {RootName}: stairs ({Steps + 1} x {Riser} m), 0.35 m ledge. " +
                      $"Player components added: {added}. Self-test wired: {test != null}.");
        }

        static void Box(Transform parent, string name, Vector3 localPos, Vector3 size)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            go.transform.localScale = size;
            GameObjectUtility.SetStaticEditorFlags(go, StaticEditorFlags.BatchingStatic);
        }

        static void Marker(Transform parent, string name, Vector3 localPos)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
        }
    }
}
