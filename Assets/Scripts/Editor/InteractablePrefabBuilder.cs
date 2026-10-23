using Echo.Interaction;
using Echo.Navigation;
using Unity.AI.Navigation;
using UnityEditor;
using UnityEditor.Events;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Echo.EditorTools
{
    /// ECHO > Prefabs > Build Interactable Prefabs. Builds the shared interactable prefabs from the SciFi kit
    /// in an isolated preview scene (no open scene is touched):
    ///   Prefabs/Interactables/Door_Slide.prefab     - kit Wall DoorClosed frame + wall colliders + sliding Door
    ///   Prefabs/Interactables/Switch_Fusebox.prefab - kit Fusebox 01 + SwitchInteractable
    ///   Prefabs/Interactables/Door_Bulkhead.prefab  - variant of Door_Slide for kit tunnels: 6x10 wall hidden,
    ///                                                 filler panels sized to the tunnel interior
    /// Only creates prefabs that are missing; it never overwrites (scene instances may carry overrides).
    /// Delete a prefab first to rebuild it.
    public static class InteractablePrefabBuilder
    {
        public const string Folder = "Assets/Prefabs/Interactables";
        public const string DoorPrefabPath = Folder + "/Door_Slide.prefab";
        public const string SwitchPrefabPath = Folder + "/Switch_Fusebox.prefab";
        public const string BulkheadPrefabPath = Folder + "/Door_Bulkhead.prefab";

        const string ClosedDoorPath = "Assets/SciFi Warehouse Kit/Prefabs/Structures/Walls/Wall DoorClosed.prefab";
        const string FuseboxPath = "Assets/SciFi Warehouse Kit/Prefabs/Structures/Structure Props/Fusebox 01.prefab";
        const string KitLeafObject = "LP_Door_snaps";
        const string KitWallObject = "LP_Door_Wall_snaps";
        const string TunnelPath = "Assets/SciFi Warehouse Kit/Prefabs/Structures/Corridors/Corridor Passthrough.prefab";
        const string WallMaterialPath = "Assets/SciFi Warehouse Kit/Art/Materials/Walls.mat";

        [MenuItem("ECHO/Prefabs/Build Interactable Prefabs")]
        public static void BuildMissing()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) return;
            // new GameObject()/CreatePrimitive() spawn in the active scene before being moved to the preview
            // scene, which would dirty it - so only run with the sandbox active, never the real level.
            if (SceneManager.GetActiveScene().path != SandboxDoorBuilder.ScenePath)
            {
                Debug.LogError($"[InteractablePrefabBuilder] Open {SandboxDoorBuilder.ScenePath} first.");
                return;
            }
            if (!AssetDatabase.IsValidFolder(Folder)) AssetDatabase.CreateFolder("Assets/Prefabs", "Interactables");

            bool door = Exists(DoorPrefabPath) || BuildDoorPrefab();
            bool sw = Exists(SwitchPrefabPath) || BuildSwitchPrefab();
            bool bulkhead = door && (Exists(BulkheadPrefabPath) || BuildBulkheadPrefab());
            Debug.Log($"[InteractablePrefabBuilder] Door_Slide: {(door ? "ok" : "FAILED")}, Switch_Fusebox: {(sw ? "ok" : "FAILED")}, " +
                      $"Door_Bulkhead: {(bulkhead ? "ok" : "FAILED")} (existing prefabs are never overwritten).");
        }

        static bool Exists(string path) => AssetDatabase.LoadAssetAtPath<GameObject>(path) != null;

        // ------------------------------------------------------------------------------------- door
        // Kit "Wall DoorClosed": wall plane in local YZ (z 0..6, y 0..9, 0.3 m thick) whose frame hugs the leaf
        // mesh. Its only collider seals the whole wall, so it is disabled and replaced by colliders around the
        // opening. Pivot and orientation equal the kit wall pieces, so it can replace one at the same transform.
        static bool BuildDoorPrefab()
        {
            var closedDoor = AssetDatabase.LoadAssetAtPath<GameObject>(ClosedDoorPath);
            if (closedDoor == null || FindDeep(closedDoor.transform, KitLeafObject) == null)
            {
                Debug.LogError("[InteractablePrefabBuilder] Kit 'Wall DoorClosed' prefab or its leaf not found.");
                return false;
            }

            Scene preview = EditorSceneManager.NewPreviewScene();
            try
            {
                var root = new GameObject("Door_Slide");
                SceneManager.MoveGameObjectToScene(root, preview);

                // Frame + wall (nested kit prefab). Its own leaf is hidden (ours moves instead) and its
                // wall-sized collider disabled; both are overrides, the kit prefab is untouched.
                var frame = (GameObject)PrefabUtility.InstantiatePrefab(closedDoor, preview);
                frame.name = "Frame";
                frame.transform.SetParent(root.transform, false);
                Transform kitLeaf = FindDeep(frame.transform, KitLeafObject);
                kitLeaf.gameObject.SetActive(false);
                BoxCollider sealedWall = frame.GetComponentInChildren<BoxCollider>();
                Bounds wall = new(sealedWall.center, sealedWall.size);   // wall object space == root space
                sealedWall.enabled = false;

                Mesh mesh = kitLeaf.GetComponent<MeshFilter>().sharedMesh;
                Bounds opening = mesh.bounds;
                AddWallColliders(root.transform, wall, opening);

                // Door object: sibling of the frame, so aiming at the wall never targets the door.
                var doorGo = new GameObject("Door");
                doorGo.transform.SetParent(root.transform, false);

                // Leaf: kit leaf mesh, primitive BoxCollider, kinematic interpolated body.
                var leafGo = new GameObject("Leaf");
                leafGo.transform.SetParent(doorGo.transform, false);
                leafGo.transform.SetLocalPositionAndRotation(kitLeaf.localPosition, kitLeaf.localRotation);
                leafGo.transform.localScale = kitLeaf.localScale;
                leafGo.AddComponent<MeshFilter>().sharedMesh = mesh;
                leafGo.AddComponent<MeshRenderer>().sharedMaterials = kitLeaf.GetComponent<MeshRenderer>().sharedMaterials;
                var box = leafGo.AddComponent<BoxCollider>();
                box.center = opening.center;
                box.size = opening.size;
                var rb = leafGo.AddComponent<Rigidbody>();
                rb.isKinematic = true;
                rb.useGravity = false;
                rb.interpolation = RigidbodyInterpolation.Interpolate;

                // Use volume: trigger filling the doorway, so an open door (leaf in the wall) can be aimed at and closed.
                var useGo = new GameObject("UseVolume");
                useGo.transform.SetParent(doorGo.transform, false);
                useGo.transform.SetLocalPositionAndRotation(leafGo.transform.localPosition, leafGo.transform.localRotation);
                useGo.transform.localScale = leafGo.transform.localScale;
                var use = useGo.AddComponent<BoxCollider>();
                use.isTrigger = true;
                use.center = box.center;
                use.size = box.size;

                // Leaf + use volume stay out of the NavMesh bake: the doorway is baked open and the
                // waypoint graph handles closed/locked doors (guide §3.2.6 - doors do not carve).
                var mod = doorGo.AddComponent<NavMeshModifier>();
                mod.ignoreFromBuild = true;
                mod.applyToChildren = true;

                var door = doorGo.AddComponent<Door>();
                door.leaf = rb;
                door.leafCollider = box;
                door.slideDirection = Vector3.forward;   // along the wall, into the pocket beside the doorway
                door.statusLight = MakeLamp(root.transform, "StatusLight",
                                            new Vector3(wall.max.x + 0.03f, opening.max.y + 0.25f, opening.center.z),
                                            new Vector3(0.06f, 0.08f, 0.5f));

                PrefabUtility.SaveAsPrefabAsset(root, DoorPrefabPath, out bool ok);
                return ok;
            }
            finally { EditorSceneManager.ClosePreviewScene(preview); }
        }

        /// Static wall colliders around the doorway: left and right of the leaf, and a header above it.
        static void AddWallColliders(Transform root, Bounds wall, Bounds opening)
        {
            var go = new GameObject("WallColliders");
            go.transform.SetParent(root, false);
            GameObjectUtility.SetStaticEditorFlags(go, StaticEditorFlags.BatchingStatic);

            void Box(float z0, float z1, float y0, float y1)
            {
                if (z1 - z0 < 0.001f || y1 - y0 < 0.001f) return;
                var b = go.AddComponent<BoxCollider>();
                b.center = new Vector3(wall.center.x, (y0 + y1) * 0.5f, (z0 + z1) * 0.5f);
                b.size = new Vector3(wall.size.x, y1 - y0, z1 - z0);
            }

            Box(wall.min.z, opening.min.z, wall.min.y, wall.max.y);         // left of the doorway
            Box(opening.max.z, wall.max.z, wall.min.y, wall.max.y);         // right (the leaf's pocket side)
            Box(opening.min.z, opening.max.z, opening.max.y, wall.max.y);   // header above the doorway
        }

        // ----------------------------------------------------------------------------------- switch
        static bool BuildSwitchPrefab()
        {
            var fusebox = AssetDatabase.LoadAssetAtPath<GameObject>(FuseboxPath);
            if (fusebox == null)
            {
                Debug.LogError("[InteractablePrefabBuilder] Kit 'Fusebox 01' prefab not found.");
                return false;
            }

            Scene preview = EditorSceneManager.NewPreviewScene();
            try
            {
                // Root = fusebox pivot (top of the box). The kit collider is on the nested child; the interactor
                // finds the SwitchInteractable on the root through GetComponentInParent.
                var root = new GameObject("Switch_Fusebox");
                SceneManager.MoveGameObjectToScene(root, preview);
                var fuse = (GameObject)PrefabUtility.InstantiatePrefab(fusebox, preview);
                fuse.name = "Fusebox";
                fuse.transform.SetParent(root.transform, false);

                var sw = root.AddComponent<SwitchInteractable>();
                sw.verb = "Restore door power";
                sw.singleUse = true;
                sw.noise = NoiseType.Switch;
                sw.noiseIntensity = 0.25f;
                sw.indicator = MakeLamp(root.transform, "SwitchLamp", new Vector3(0.21f, 0.15f, 0f), new Vector3(0.05f, 0.06f, 0.06f));

                PrefabUtility.SaveAsPrefabAsset(root, SwitchPrefabPath, out bool ok);
                return ok;
            }
            finally { EditorSceneManager.ClosePreviewScene(preview); }
        }

        // ---------------------------------------------------------------------------- instance wiring
        /// Door_Slide's lock state is a per-instance override of Door.startLocked.
        public static void SetStartLocked(Door door, bool locked)
        {
            var so = new SerializedObject(door);
            so.FindProperty("startLocked").boolValue = locked;
            so.ApplyModifiedProperties();
        }

        /// Adds switch.onActivated -> door.Unlock as a persistent (serialized) listener, once.
        public static void WireUnlock(SwitchInteractable sw, Door door)
        {
            for (int i = 0; i < sw.onActivated.GetPersistentEventCount(); i++)
                if (sw.onActivated.GetPersistentTarget(i) == door && sw.onActivated.GetPersistentMethodName(i) == nameof(Door.Unlock))
                    return;
            Undo.RecordObject(sw, "Wire switch to door");
            UnityEventTools.AddPersistentListener(sw.onActivated, door.Unlock);
            PrefabUtility.RecordPrefabInstancePropertyModifications(sw);
        }

        /// Removes switch.onActivated -> door.Unlock if present. Returns true if one was removed.
        public static bool UnwireUnlock(SwitchInteractable sw, Door door)
        {
            for (int i = sw.onActivated.GetPersistentEventCount() - 1; i >= 0; i--)
                if (sw.onActivated.GetPersistentTarget(i) == door && sw.onActivated.GetPersistentMethodName(i) == nameof(Door.Unlock))
                {
                    Undo.RecordObject(sw, "Unwire switch from door");
                    UnityEventTools.RemovePersistentListener(sw.onActivated, i);
                    PrefabUtility.RecordPrefabInstancePropertyModifications(sw);
                    return true;
                }
            return false;
        }

        // --------------------------------------------------------------------------------- bulkhead
        /// Interior of the kit tunnel module (Corridor Passthrough), from its own colliders, in metres:
        /// width between the inner faces of the two tunnel walls, height from floor top to ceiling underside.
        public static bool TryGetTunnelInterior(out float width, out float height)
        {
            width = height = 0f;
            var tunnel = AssetDatabase.LoadAssetAtPath<GameObject>(TunnelPath);
            if (tunnel == null) return false;
            var walls = new System.Collections.Generic.List<Bounds>();
            float floorTop = float.NaN, ceilingBottom = float.NaN;
            foreach (BoxCollider b in tunnel.GetComponentsInChildren<BoxCollider>(true))
            {
                Matrix4x4 m = tunnel.transform.worldToLocalMatrix * b.transform.localToWorldMatrix;
                var local = new Bounds(m.MultiplyPoint3x4(b.center), Vector3.Scale(b.size, m.lossyScale));
                if (b.name.Contains("Walls")) walls.Add(local);
                else if (b.name.Contains("Floor")) floorTop = local.max.y;
                else if (b.name.Contains("Ceiling")) ceilingBottom = local.min.y;
            }
            if (walls.Count != 2 || float.IsNaN(floorTop) || float.IsNaN(ceilingBottom)) return false;
            walls.Sort((a, b) => a.center.x.CompareTo(b.center.x));
            width = walls[1].min.x - walls[0].max.x;   // inner face to inner face
            height = ceilingBottom - floorTop;
            return true;
        }

        // Variant of Door_Slide for the kit tunnels: the kit 6x10 wall mesh and Door_Slide's wall colliders are
        // hidden, the kit door frame stays, and three static panels (kit wall material, primitive BoxColliders)
        // fill the tunnel cross-section exactly around the leaf opening. Same pivot as Door_Slide: floor at y 0,
        // wall plane local YZ, doorway centred on the leaf.
        static bool BuildBulkheadPrefab()
        {
            var doorSlide = AssetDatabase.LoadAssetAtPath<GameObject>(DoorPrefabPath);
            var wallMat = AssetDatabase.LoadAssetAtPath<Material>(WallMaterialPath);
            if (doorSlide == null || wallMat == null || !TryGetTunnelInterior(out float width, out float height))
            {
                Debug.LogError("[InteractablePrefabBuilder] Door_Slide, Walls.mat or the kit tunnel prefab not found.");
                return false;
            }

            Scene preview = EditorSceneManager.NewPreviewScene();
            try
            {
                var root = (GameObject)PrefabUtility.InstantiatePrefab(doorSlide, preview);
                root.name = "Door_Bulkhead";
                FindDeep(root.transform, KitWallObject).gameObject.SetActive(false);   // 6x10 wall mesh (frame is separate)
                root.transform.Find("WallColliders").gameObject.SetActive(false);    // Door_Slide's 6x10 wall colliders

                Door door = root.GetComponentInChildren<Door>();
                Bounds opening = new(door.leafCollider.center, door.leafCollider.size);   // leaf space == root space
                float thickness = 0.3f;   // the kit wall's thickness; the 0.4 m frame overlaps the panel edges
                float z0 = opening.center.z - width * 0.5f, z1 = opening.center.z + width * 0.5f;

                var panels = new GameObject("BulkheadPanels");
                panels.transform.SetParent(root.transform, false);
                Panel(panels.transform, "Left", z0, opening.min.z, 0f, height, thickness, wallMat);
                Panel(panels.transform, "Right", opening.max.z, z1, 0f, height, thickness, wallMat);   // leaf pocket side
                Panel(panels.transform, "Header", opening.min.z, opening.max.z, opening.max.y, height, thickness, wallMat);

                PrefabUtility.SaveAsPrefabAsset(root, BulkheadPrefabPath, out bool ok);   // instance root -> prefab variant
                if (ok) Debug.Log($"[InteractablePrefabBuilder] Door_Bulkhead sized to tunnel interior {width:F3} x {height:F3} m.");
                return ok;
            }
            finally { EditorSceneManager.ClosePreviewScene(preview); }
        }

        static void Panel(Transform parent, string name, float z0, float z1, float y0, float y1, float thickness, Material mat)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);   // primitive BoxCollider + cube mesh
            go.name = name;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = new Vector3(0f, (y0 + y1) * 0.5f, (z0 + z1) * 0.5f);
            go.transform.localScale = new Vector3(thickness, y1 - y0, z1 - z0);
            go.GetComponent<MeshRenderer>().sharedMaterial = mat;
            GameObjectUtility.SetStaticEditorFlags(go, StaticEditorFlags.BatchingStatic);
        }

        // ---------------------------------------------------------------------------------- helpers
        static Renderer MakeLamp(Transform parent, string name, Vector3 localPos, Vector3 scale)
        {
            var lamp = GameObject.CreatePrimitive(PrimitiveType.Cube);
            lamp.name = name;
            Object.DestroyImmediate(lamp.GetComponent<Collider>());
            lamp.transform.SetParent(parent, false);
            lamp.transform.localPosition = localPos;
            lamp.transform.localScale = scale;
            return lamp.GetComponent<Renderer>();
        }

        static Transform FindDeep(Transform t, string name)
        {
            if (t.name == name) return t;
            foreach (Transform c in t)
            {
                Transform f = FindDeep(c, name);
                if (f != null) return f;
            }
            return null;
        }
    }
}
