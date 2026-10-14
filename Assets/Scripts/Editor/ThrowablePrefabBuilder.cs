using Echo.Interaction;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Echo.EditorTools
{
    /// ECHO > Prefabs > Build Throwable Prefabs. Builds the carryable prop prefabs from existing kit art,
    /// in an isolated preview scene (no open scene is touched):
    ///   Prefabs/Interactables/Throwable_Brick.prefab        - 2 kg, BoxCollider, ObjectDrop impacts
    ///   Prefabs/Interactables/Throwable_MetalBasin.prefab   - 3 kg, BoxCollider, MetalImpact impacts
    ///   Prefabs/Interactables/Barricade_CrateLong.prefab    - 40 kg, BoxCollider (too heavy to lift)
    /// The kit meshes are copied (mesh + materials only); their non-convex MeshColliders are replaced by one
    /// BoxCollider sized from the mesh bounds, because a dynamic Rigidbody needs a convex collider and
    /// primitives are the cheapest shapes for the solver. Never overwrites: delete a prefab to rebuild it.
    public static class ThrowablePrefabBuilder
    {
        public const string BrickPath = InteractablePrefabBuilder.Folder + "/Throwable_Brick.prefab";
        public const string BasinPath = InteractablePrefabBuilder.Folder + "/Throwable_MetalBasin.prefab";
        public const string CratePath = InteractablePrefabBuilder.Folder + "/Barricade_CrateLong.prefab";

        const string KitBrick = "Assets/Abandoned_Building/Prefabs/SM_Bricks_Brick_01.prefab";
        // Rusty enamel basin, 0.46 x 0.08 x 0.25 m: a single object (the kit "Fire Extinguisher" is a wall
        // hanger with a hose and two extinguishers, so it can't be one throwable).
        const string KitBasin = "Assets/Abandoned_Building/Prefabs/SM_Object_08.prefab";
        const string KitCrateLong = "Assets/SciFi Warehouse Kit/Prefabs/Props/Crates Barrels Pallets/Crate Long.prefab";

        [MenuItem("ECHO/Prefabs/Build Throwable Prefabs")]
        public static void BuildMissing()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) return;
            // new GameObject() lands in the active scene before moving to the preview scene: sandbox only.
            if (SceneManager.GetActiveScene().path != SandboxDoorBuilder.ScenePath)
            {
                Debug.LogError($"[ThrowablePrefabBuilder] Open {SandboxDoorBuilder.ScenePath} first.");
                return;
            }
            if (!AssetDatabase.IsValidFolder(InteractablePrefabBuilder.Folder)) AssetDatabase.CreateFolder("Assets/Prefabs", "Interactables");

            string brick = Build(BrickPath, KitBrick, "Throwable_Brick", 2f, NoiseType.ObjectDrop);
            string basin = Build(BasinPath, KitBasin, "Throwable_MetalBasin", 3f, NoiseType.MetalImpact);
            string crate = Build(CratePath, KitCrateLong, "Barricade_CrateLong", 40f, NoiseType.ObjectDrop);
            // One line: the console list shows only the first line of a multi-line message.
            Debug.Log($"[ThrowablePrefabBuilder] {brick} | {basin} | {crate} (existing prefabs are never overwritten)");
        }

        static string Build(string path, string kitPath, string name, float mass, NoiseType impact)
        {
            if (AssetDatabase.LoadAssetAtPath<GameObject>(path) != null) return $"{name}: exists";
            var kit = AssetDatabase.LoadAssetAtPath<GameObject>(kitPath);
            MeshFilter kitMesh = kit != null ? kit.GetComponentInChildren<MeshFilter>() : null;
            if (kitMesh == null || kitMesh.sharedMesh == null) return $"{name}: FAILED (kit prefab or mesh not found: {kitPath})";

            Scene preview = EditorSceneManager.NewPreviewScene();
            try
            {
                var root = new GameObject(name);
                SceneManager.MoveGameObjectToScene(root, preview);

                // Visual + collider on one child, same pose relative to the root as in the kit prefab.
                var meshGo = new GameObject("Mesh");
                meshGo.transform.SetParent(root.transform, false);
                Transform k = kitMesh.transform;
                meshGo.transform.SetLocalPositionAndRotation(
                    kit.transform.InverseTransformPoint(k.position),
                    Quaternion.Inverse(kit.transform.rotation) * k.rotation);
                meshGo.transform.localScale = k.lossyScale;
                meshGo.AddComponent<MeshFilter>().sharedMesh = kitMesh.sharedMesh;
                meshGo.AddComponent<MeshRenderer>().sharedMaterials = kitMesh.GetComponent<MeshRenderer>().sharedMaterials;

                Bounds b = kitMesh.sharedMesh.bounds;
                var box = meshGo.AddComponent<BoxCollider>();
                box.center = b.center;
                box.size = b.size;
                string colliderInfo = $"Box {Vector3.Scale(b.size, meshGo.transform.localScale)}";

                var rb = root.AddComponent<Rigidbody>();
                rb.mass = mass;
                rb.interpolation = RigidbodyInterpolation.None;            // Carryable turns it on only while moving
                rb.collisionDetectionMode = CollisionDetectionMode.Discrete;

                var carry = root.AddComponent<Carryable>();
                carry.impactNoise = impact;

                PrefabUtility.SaveAsPrefabAsset(root, path, out bool ok);
                return ok ? $"{name}: built ({mass} kg, {colliderInfo}, impact {impact})" : $"{name}: FAILED to save";
            }
            finally { EditorSceneManager.ClosePreviewScene(preview); }
        }
    }
}
