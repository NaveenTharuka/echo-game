using Echo.Interaction;
using Unity.AI.Navigation;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.SceneManagement;

namespace Echo.EditorTools
{
    /// ECHO > Prefabs > Build Throwable Prefabs. Builds the carryable prop prefabs from existing kit art,
    /// in an isolated preview scene (no open scene is touched):
    ///   Prefabs/Interactables/Throwable_Brick.prefab        - 2 kg, BoxCollider, ObjectDrop impacts
    ///   Prefabs/Interactables/Throwable_MetalBasin.prefab   - 3 kg, BoxCollider, MetalImpact impacts
    ///   Prefabs/Interactables/Barricade_CrateLong.prefab    - 40 kg, BoxCollider (dragged)
    ///   Prefabs/Interactables/Barricade_Barrel.prefab       - 25 kg, CapsuleCollider (dragged)
    /// The kit meshes are copied (mesh + materials only); their non-convex MeshColliders are replaced by one
    /// primitive collider sized from the mesh bounds, because a dynamic Rigidbody needs a convex collider and
    /// primitives are the cheapest shapes for the solver. Never overwrites: delete a prefab to rebuild it.
    ///
    /// Barricades (heavier than the lift limit) are then given, additively and in place (GUID kept):
    ///   - X/Z rotation frozen: they stay upright (a capsule barrel would otherwise roll off its rounded end);
    ///   - NavMeshObstacle, carving only when stationary: agents path around a placed barricade, and the
    ///     NavMesh is not re-carved every frame while it is dragged;
    ///   - NavMeshModifier "ignore from build": a movable prop must never be baked into the static NavMesh.
    public static class ThrowablePrefabBuilder
    {
        public const string BrickPath = InteractablePrefabBuilder.Folder + "/Throwable_Brick.prefab";
        public const string BasinPath = InteractablePrefabBuilder.Folder + "/Throwable_MetalBasin.prefab";
        public const string CratePath = InteractablePrefabBuilder.Folder + "/Barricade_CrateLong.prefab";
        public const string BarrelPath = InteractablePrefabBuilder.Folder + "/Barricade_Barrel.prefab";

        // Carving settings (see viva notes): re-carve only after 0.5 s at rest, ignore sub-10 cm jitter.
        public const float CarveTimeToStationary = 0.5f;
        public const float CarveMoveThreshold = 0.1f;

        const string KitBrick = "Assets/Abandoned_Building/Prefabs/SM_Bricks_Brick_01.prefab";
        // Rusty enamel basin, 0.46 x 0.08 x 0.25 m: a single object (the kit "Fire Extinguisher" is a wall
        // hanger with a hose and two extinguishers, so it can't be one throwable).
        const string KitBasin = "Assets/Abandoned_Building/Prefabs/SM_Object_08.prefab";
        const string KitCrateLong = "Assets/SciFi Warehouse Kit/Prefabs/Props/Crates Barrels Pallets/Crate Long.prefab";
        const string KitBarrel = "Assets/SciFi Warehouse Kit/Prefabs/Props/Crates Barrels Pallets/Barrel.prefab";

        enum Shape { Box, Capsule }

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

            string brick = Build(BrickPath, KitBrick, "Throwable_Brick", Shape.Box, 2f, NoiseType.ObjectDrop);
            string basin = Build(BasinPath, KitBasin, "Throwable_MetalBasin", Shape.Box, 3f, NoiseType.MetalImpact);
            string crate = Build(CratePath, KitCrateLong, "Barricade_CrateLong", Shape.Box, 40f, NoiseType.ObjectDrop);
            string barrel = Build(BarrelPath, KitBarrel, "Barricade_Barrel", Shape.Capsule, 25f, NoiseType.MetalImpact);
            crate += EnsureBarricade(CratePath);
            barrel += EnsureBarricade(BarrelPath);
            // One line: the console list shows only the first line of a multi-line message.
            Debug.Log($"[ThrowablePrefabBuilder] {brick} | {basin} | {crate} | {barrel} (existing prefabs are never overwritten)");
        }

        static string Build(string path, string kitPath, string name, Shape shape, float mass, NoiseType impact)
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
                string colliderInfo;
                if (shape == Shape.Box)
                {
                    var box = meshGo.AddComponent<BoxCollider>();
                    box.center = b.center;
                    box.size = b.size;
                    colliderInfo = $"Box {Vector3.Scale(b.size, meshGo.transform.localScale)}";
                }
                else
                {
                    // Upright cylinder (barrel): radius = half the wider horizontal extent, full height.
                    var cap = meshGo.AddComponent<CapsuleCollider>();
                    cap.direction = 1;
                    cap.center = b.center;
                    cap.radius = Mathf.Max(b.size.x, b.size.z) * 0.5f;
                    cap.height = Mathf.Max(b.size.y, cap.radius * 2f);
                    Vector3 s = meshGo.transform.localScale;
                    colliderInfo = $"Capsule height {cap.height * s.y:F2} diameter {cap.radius * 2f * s.x:F2} (mesh {Vector3.Scale(b.size, s)})";
                }

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

        /// Adds what a barricade needs to an existing prefab, only where missing (no rebuild, GUID kept).
        static string EnsureBarricade(string path)
        {
            if (AssetDatabase.LoadAssetAtPath<GameObject>(path) == null) return "";
            GameObject root = PrefabUtility.LoadPrefabContents(path);
            try
            {
                var rb = root.GetComponent<Rigidbody>();
                rb.constraints |= RigidbodyConstraints.FreezeRotationX | RigidbodyConstraints.FreezeRotationZ;

                Collider col = root.GetComponentInChildren<Collider>();
                if (col.GetComponent<NavMeshObstacle>() == null)
                {
                    // On the collider's object, so the obstacle shape uses the same local space as the collider.
                    var obstacle = col.gameObject.AddComponent<NavMeshObstacle>();
                    if (col is CapsuleCollider cap)
                    {
                        obstacle.shape = NavMeshObstacleShape.Capsule;
                        obstacle.center = cap.center;
                        obstacle.radius = cap.radius;
                        obstacle.height = cap.height;
                    }
                    else if (col is BoxCollider box)
                    {
                        obstacle.shape = NavMeshObstacleShape.Box;
                        obstacle.center = box.center;
                        obstacle.size = box.size;
                    }
                    obstacle.carving = true;
                    obstacle.carveOnlyStationary = true;
                    obstacle.carvingTimeToStationary = CarveTimeToStationary;
                    obstacle.carvingMoveThreshold = CarveMoveThreshold;
                }

                if (root.GetComponent<NavMeshModifier>() == null)
                {
                    var mod = root.AddComponent<NavMeshModifier>();
                    mod.ignoreFromBuild = true;
                    mod.applyToChildren = true;
                }

                PrefabUtility.SaveAsPrefabAsset(root, path);
                return " +barricade (X/Z frozen, carving obstacle, ignored by NavMesh bake)";
            }
            finally { PrefabUtility.UnloadPrefabContents(root); }
        }
    }
}
