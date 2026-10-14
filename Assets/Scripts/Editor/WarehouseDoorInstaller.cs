using System.Collections.Generic;
using Echo.Interaction;
using Echo.Navigation;
using Echo.UI;
using StarterAssets;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Echo.EditorTools
{
    /// ECHO > Level > Install Warehouse Doors. Re-runnable setup of the approved SampleScene changes
    /// (13 Oct, see CLAUDE.md): replaces two kit doorway pieces in the Warehouse (Room_3) with normal Door_Slides,
    /// places a locked Door_Bulkhead in the Passthrough into Containment, unlocked by the power fusebox in the
    /// NE catwalk bay ("Restore power"), adds the missing colliders to "Wall Door 02", and gives the player a
    /// PlayerInteractor + InteractionHUD.
    /// Touches nothing else: no lights, props, layers or settings. Skips any step that is already done.
    static class WarehouseDoorInstaller
    {
        const string ScenePath = "Assets/Scenes/SampleScene.unity";
        const string WallDoorPrefab = "Assets/SciFi Warehouse Kit/Prefabs/Structures/Walls/Wall Door.prefab";
        const string WallDoor02Prefab = "Assets/SciFi Warehouse Kit/Prefabs/Structures/Walls/Wall Door 02.prefab";
        const string HudPrefab = "Assets/Prefabs/UI/InteractionHUD.prefab";
        const string InputAsset = "Assets/Input/EchoInteraction.inputactions";

        // #1 main storage hall <-> SE bay: normal door (the hall is open to the NE bay, so a lock only gated a shortcut).
        const string HallPiece = "Wall Door", HallDoorName = "Door_Slide_StorageHall";
        // #2 SE bay <-> NE catwalk/stairs bay: normal door.
        const string CatwalkPiece = "Wall Door (1)", CatwalkDoorName = "Door_Slide_CatwalkBay";
        const string FuseboxName = "Switch_Fusebox_Power", OldFuseboxName = "Switch_Fusebox_HallPower";
        // Passthrough (Corridor Corner <-> Containment): locked bulkhead at the tunnel midpoint, unlocked by the fusebox.
        const string TunnelPrefab = "Assets/SciFi Warehouse Kit/Prefabs/Structures/Corridors/Corridor Passthrough.prefab";
        const string TunnelName = "Corridor Passthrough", ContainmentWallName = "Wall Corridor";
        const string BulkheadName = "Door_Bulkhead_Containment";
        const string WallDoor02Name = "Wall Door 02", WallDoor02Colliders = "AddedColliders (Wall Door layout)";

        // Fusebox on the NE bay's east wall face (x 86.85), above the short crates (top y -22.8) at z ~76.8,
        // under the catwalk (y -19.98): box spans y -22.57..-21.6 = eye height on the -24 m floor.
        // Yaw 180 so its front (+X) faces into the room. Visible from the #2 doorway. From Docs/Map/map_data.json.
        static readonly Vector3 FuseboxWorldPos = new(86.85f, -21.6f, 76.81f);
        static readonly Quaternion FuseboxWorldRot = Quaternion.Euler(0f, 180f, 0f);

        [MenuItem("ECHO/Level/Install Warehouse Doors")]
        static void Install()
        {
            var scene = EditorSceneManager.GetActiveScene();
            if (scene.path != ScenePath) { Debug.LogError($"[WarehouseDoorInstaller] Open {ScenePath} first."); return; }
            if (EditorApplication.isPlayingOrWillChangePlaymode) return;

            var doorPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(InteractablePrefabBuilder.DoorPrefabPath);
            var switchPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(InteractablePrefabBuilder.SwitchPrefabPath);
            var bulkheadPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(InteractablePrefabBuilder.BulkheadPrefabPath);
            if (doorPrefab == null || switchPrefab == null || bulkheadPrefab == null)
            {
                Debug.LogError("[WarehouseDoorInstaller] Door_Slide / Switch_Fusebox / Door_Bulkhead prefabs missing: run ECHO > Prefabs > Build Interactable Prefabs (in the sandbox).");
                return;
            }

            var log = new List<string>();
            Undo.SetCurrentGroupName("Install Warehouse Doors");
            int undoGroup = Undo.GetCurrentGroup();

            Door hall = ReplacePiece(HallPiece, HallDoorName, doorPrefab, log);
            Door catwalk = ReplacePiece(CatwalkPiece, CatwalkDoorName, doorPrefab, log);
            if (hall != null) { InteractablePrefabBuilder.SetStartLocked(hall, false); log.Add($"{HallDoorName}: normal door (startLocked = false)"); }
            if (catwalk != null) InteractablePrefabBuilder.SetStartLocked(catwalk, false);

            SwitchInteractable fuse = PlaceFusebox(switchPrefab, hall, log);
            if (fuse != null && hall != null && InteractablePrefabBuilder.UnwireUnlock(fuse, hall))
                log.Add($"{FuseboxName}: removed old -> {HallDoorName}.Unlock");

            Door bulkhead = PlaceBulkhead(bulkheadPrefab, log);
            if (bulkhead != null) { InteractablePrefabBuilder.SetStartLocked(bulkhead, true); log.Add($"{BulkheadName}: startLocked = true"); }
            if (fuse != null && bulkhead != null) { InteractablePrefabBuilder.WireUnlock(fuse, bulkhead); log.Add($"{FuseboxName}.onActivated -> {BulkheadName}.Unlock"); }

            AddWallDoor02Colliders(log);
            SetUpPlayer(log);

            Undo.CollapseUndoOperations(undoGroup);
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            Debug.Log("[WarehouseDoorInstaller] Done:\n- " + string.Join("\n- ", log));
        }

        /// Play Mode only: scripted check of the installed doors (see DevTools/WarehouseDoorWalkthrough).
        [MenuItem("ECHO/Dev/Run Warehouse Door Walkthrough")]
        static void RunWalkthrough() => Echo.DevTools.WarehouseDoorWalkthrough.Run();

        [MenuItem("ECHO/Dev/Run Warehouse Door Walkthrough", true)]
        static bool CanRunWalkthrough() => EditorApplication.isPlaying && EditorSceneManager.GetActiveScene().path == ScenePath;

        // ---------------------------------------------------------------------------------- doors
        /// Replaces a kit "Wall Door" instance with Door_Slide at the same parent, transform and sibling index.
        /// Returns the existing door if the replacement was already done.
        static Door ReplacePiece(string pieceName, string doorName, GameObject doorPrefab, List<string> log)
        {
            GameObject existing = FindInScene(doorName);
            if (existing != null) { log.Add($"{doorName}: already installed"); return existing.GetComponentInChildren<Door>(); }

            var source = AssetDatabase.LoadAssetAtPath<GameObject>(WallDoorPrefab);
            GameObject piece = FindPrefabInstance(pieceName, source);
            if (piece == null) { log.Add($"ERROR {pieceName}: kit Wall Door instance not found"); return null; }

            Transform t = piece.transform;
            var go = (GameObject)PrefabUtility.InstantiatePrefab(doorPrefab, t.parent);
            Undo.RegisterCreatedObjectUndo(go, "Place Door_Slide");
            go.name = doorName;
            go.transform.SetLocalPositionAndRotation(t.localPosition, t.localRotation);
            go.transform.localScale = t.localScale;
            go.transform.SetSiblingIndex(t.GetSiblingIndex());

            // Match the level's static flags for the wall/frame (M1 set the kit door pieces to non-static).
            StaticEditorFlags flags = GameObjectUtility.GetStaticEditorFlags(piece.GetComponentInChildren<MeshRenderer>().gameObject);
            foreach (string part in new[] { "Frame", "WallColliders" })
                foreach (Transform c in go.transform.Find(part).GetComponentsInChildren<Transform>(true))
                    GameObjectUtility.SetStaticEditorFlags(c.gameObject, flags);

            Vector3 pos = t.position;
            Undo.DestroyObjectImmediate(piece);
            log.Add($"{pieceName} -> {doorName} at {pos} (static flags {flags})");
            return go.GetComponentInChildren<Door>();
        }

        static SwitchInteractable PlaceFusebox(GameObject switchPrefab, Door hall, List<string> log)
        {
            GameObject go = FindInScene(FuseboxName) ?? FindInScene(OldFuseboxName);
            if (go == null)
            {
                if (hall == null) { log.Add($"ERROR {FuseboxName}: no warehouse door to place it by"); return null; }
                Transform parent = hall.transform.parent.parent;   // the Warehouse (Room_3) group
                go = (GameObject)PrefabUtility.InstantiatePrefab(switchPrefab, parent);
                Undo.RegisterCreatedObjectUndo(go, "Place fusebox");
                go.transform.SetPositionAndRotation(FuseboxWorldPos, FuseboxWorldRot);
                log.Add($"{FuseboxName} placed at {FuseboxWorldPos} under {parent.name}");
            }
            if (go.name != FuseboxName)
            {
                log.Add($"{go.name} renamed to {FuseboxName}");
                Undo.RecordObject(go, "Rename fusebox");
                go.name = FuseboxName;
            }

            var sw = go.GetComponent<SwitchInteractable>();
            Undo.RecordObject(sw, "Fusebox power");
            sw.verb = "Restore power";
            sw.activatedMessage = "Power restored";
            sw.messageSeconds = 2f;
            PrefabUtility.RecordPrefabInstancePropertyModifications(sw);
            return sw;
        }

        // ----------------------------------------------------------------------- Containment bulkhead
        /// Places Door_Bulkhead across the Passthrough tunnel at its midpoint, sized and centred from the tunnel's
        /// own colliders (walls, floor, ceiling). Sibling of the tunnel: the tunnel piece itself is not modified.
        /// The door's +X (status lamp side) faces away from Containment, i.e. towards the approaching player.
        static Door PlaceBulkhead(GameObject bulkheadPrefab, List<string> log)
        {
            GameObject existing = FindInScene(BulkheadName);
            if (existing != null) { log.Add($"{BulkheadName}: already installed"); return existing.GetComponentInChildren<Door>(); }

            GameObject tunnel = FindPrefabInstance(TunnelName, AssetDatabase.LoadAssetAtPath<GameObject>(TunnelPrefab));
            GameObject containmentWall = FindInScene(ContainmentWallName);
            if (tunnel == null || containmentWall == null) { log.Add($"ERROR {BulkheadName}: Passthrough tunnel or Containment wall not found"); return null; }

            var walls = new List<Bounds>();
            Bounds? floor = null, ceiling = null;
            foreach (BoxCollider b in tunnel.GetComponentsInChildren<BoxCollider>(true))
            {
                if (b.name.Contains("Walls")) walls.Add(b.bounds);
                else if (b.name.Contains("Floor")) floor = b.bounds;
                else if (b.name.Contains("Ceiling")) ceiling = b.bounds;
            }
            if (walls.Count != 2 || floor == null || ceiling == null) { log.Add($"ERROR {BulkheadName}: unexpected tunnel colliders"); return null; }

            // Lateral axis = the one the two walls are separated along; the tunnel runs along the other.
            Vector3 sep = walls[1].center - walls[0].center;
            bool lateralIsX = Mathf.Abs(sep.x) > Mathf.Abs(sep.z);
            int lat = lateralIsX ? 0 : 2, len = lateralIsX ? 2 : 0;
            walls.Sort((a, b) => a.center[lat].CompareTo(b.center[lat]));
            float inner0 = walls[0].max[lat], inner1 = walls[1].min[lat];
            float width = inner1 - inner0, height = ceiling.Value.min.y - floor.Value.max.y;

            Vector3 centre = Vector3.zero;   // doorway centre on the floor, at the tunnel midpoint
            centre[lat] = (inner0 + inner1) * 0.5f;
            centre[len] = floor.Value.center[len];
            centre.y = floor.Value.max.y;

            Vector3 lengthAxis = len == 0 ? Vector3.right : Vector3.forward;
            Vector3 lateralAxis = lat == 0 ? Vector3.right : Vector3.forward;
            Vector3 towardsPlayer = Vector3.Dot(containmentWall.transform.position - centre, lengthAxis) > 0f ? -lengthAxis : lengthAxis;
            // Unity: right = Cross(up, forward). Pick forward (local Z, along the wall) so local +X = towardsPlayer.
            Vector3 forward = Vector3.Dot(Vector3.Cross(Vector3.up, lateralAxis), towardsPlayer) > 0f ? lateralAxis : -lateralAxis;
            Quaternion rot = Quaternion.LookRotation(forward, Vector3.up);

            var go = (GameObject)PrefabUtility.InstantiatePrefab(bulkheadPrefab, tunnel.transform.parent);
            Undo.RegisterCreatedObjectUndo(go, "Place bulkhead");
            go.name = BulkheadName;
            go.transform.SetPositionAndRotation(Vector3.zero, rot);
            Door door = go.GetComponentInChildren<Door>();
            Vector3 leafLocal = go.transform.InverseTransformPoint(door.leafCollider.transform.TransformPoint(door.leafCollider.center));
            go.transform.position = centre - rot * new Vector3(leafLocal.x, 0f, leafLocal.z);
            GameObjectUtility.SetStaticEditorFlags(go, GameObjectUtility.GetStaticEditorFlags(tunnel));

            InteractablePrefabBuilder.TryGetTunnelInterior(out float prefabWidth, out float prefabHeight);
            bool fits = Mathf.Abs(prefabWidth - width) < 0.01f && Mathf.Abs(prefabHeight - height) < 0.01f;
            log.Add($"{BulkheadName} at {go.transform.position}, lamp side facing {towardsPlayer}; tunnel interior {width:F3} x {height:F3} m, " +
                    $"bulkhead {prefabWidth:F3} x {prefabHeight:F3} m {(fits ? "(fits)" : "MISMATCH")}");
            return door;
        }

        // ------------------------------------------------------------------------ Wall Door 02 fix
        /// "Wall Door 02" ships with no colliders. Adds the kit "Wall Door" collider layout (same 6 x 10 x 0.3 m
        /// wall, same doorway) as an added child object: a scene override, the kit prefab is untouched.
        static void AddWallDoor02Colliders(List<string> log)
        {
            var src02 = AssetDatabase.LoadAssetAtPath<GameObject>(WallDoor02Prefab);
            GameObject piece = FindPrefabInstance(WallDoor02Name, src02);
            if (piece == null) { log.Add($"ERROR {WallDoor02Name}: instance not found"); return; }
            if (piece.transform.Find(WallDoor02Colliders) != null) { log.Add($"{WallDoor02Name}: colliders already added"); return; }

            var layout = AssetDatabase.LoadAssetAtPath<GameObject>(WallDoorPrefab);
            var holder = new GameObject(WallDoor02Colliders);
            Undo.RegisterCreatedObjectUndo(holder, "Wall Door 02 colliders");
            holder.transform.SetParent(piece.transform, false);
            GameObjectUtility.SetStaticEditorFlags(holder, GameObjectUtility.GetStaticEditorFlags(piece));

            int n = 0;
            foreach (BoxCollider b in layout.GetComponentsInChildren<BoxCollider>(true))
            {
                // Express each kit collider in the kit root's space (its parts sit at identity, but be exact).
                Matrix4x4 m = layout.transform.worldToLocalMatrix * b.transform.localToWorldMatrix;
                var c = holder.AddComponent<BoxCollider>();
                c.center = m.MultiplyPoint3x4(b.center);
                c.size = Vector3.Scale(b.size, m.lossyScale);
                n++;
            }
            log.Add($"{WallDoor02Name}: added {n} BoxColliders (kit Wall Door layout)");
        }

        // -------------------------------------------------------------------------------- player
        static void SetUpPlayer(List<string> log)
        {
            var fpc = Object.FindFirstObjectByType<FirstPersonController>();
            if (fpc == null) { log.Add("ERROR player: no FirstPersonController in scene"); return; }
            GameObject player = fpc.gameObject;

            var interactor = player.GetComponent<PlayerInteractor>();
            if (interactor == null)
            {
                interactor = Undo.AddComponent<PlayerInteractor>(player);
                Camera cam = player.GetComponentInChildren<Camera>() != null ? player.GetComponentInChildren<Camera>() : Camera.main;
                interactor.viewPoint = cam != null ? cam.transform : null;
                interactor.interactableMask = 1 << 0;   // Default, until the Interactable layer is confirmed
                interactor.occluderMask = ~0;
                interactor.inputActions = AssetDatabase.LoadAssetAtPath<InputActionAsset>(InputAsset);
                PrefabUtility.RecordPrefabInstancePropertyModifications(interactor);
                log.Add($"PlayerInteractor added to {player.name} (view {(cam != null ? cam.name : "NONE")}, mask Default)");
            }
            else log.Add("PlayerInteractor: already present");

            var hud = Object.FindFirstObjectByType<InteractionPromptUI>();
            if (hud == null)
            {
                var go = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(HudPrefab), player.scene);
                Undo.RegisterCreatedObjectUndo(go, "Add InteractionHUD");
                hud = go.GetComponent<InteractionPromptUI>();
                hud.interactor = interactor;
                PrefabUtility.RecordPrefabInstancePropertyModifications(hud);
                log.Add("InteractionHUD instance added");
            }
            else log.Add("InteractionHUD: already present");
        }

        // ------------------------------------------------------------------------------- helpers
        static GameObject FindInScene(string name)
        {
            foreach (Transform t in Object.FindObjectsByType<Transform>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                if (t.name == name) return t.gameObject;
            return null;
        }

        static GameObject FindPrefabInstance(string name, GameObject source)
        {
            foreach (Transform t in Object.FindObjectsByType<Transform>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                if (t.name == name && PrefabUtility.IsAnyPrefabInstanceRoot(t.gameObject)
                    && PrefabUtility.GetCorrespondingObjectFromSource(t.gameObject) == source)
                    return t.gameObject;
            return null;
        }
    }
}
