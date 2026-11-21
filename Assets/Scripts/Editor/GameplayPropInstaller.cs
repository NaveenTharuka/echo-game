using System.Collections.Generic;
using Echo.Interaction;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AI;

namespace Echo.EditorTools
{
    /// ECHO > Level > Install Throwables and Barricades. Re-runnable: puts new Throwable_ / Barricade_ prefab instances
    /// into SampleScene under one new root ("Interaction Props"), rebuilt from scratch on every run. Touches nothing
    /// else - the kit's crates and barrels stay as they are.
    ///
    /// Each spot is a rough point on the route (floor plan v5, `Docs/Map/`). The installer finds the real spot:
    ///   - a floor under it (downward ray, flat, static, within 0.6 m of the expected floor height),
    ///   - no overlap: the prop's collider bounds plus a 5 cm gap are clear of walls and other props,
    ///   - reachable: NavMesh within 1 m of the floor point.
    /// It tries the rough point first, then rings out to 1.5 m, and logs where each prop ended up. Barricades only
    /// stand next to doorways (never in one); ECHO > Dev > Run Throwing In-Game Test checks that their carving
    /// leaves every route open.
    static class GameplayPropInstaller
    {
        const string ScenePath = "Assets/Scenes/SampleScene.unity";
        const string RootName = "Interaction Props";
        const string Brick = "Assets/Prefabs/Interactables/Throwable_Brick.prefab";
        const string Basin = "Assets/Prefabs/Interactables/Throwable_MetalBasin.prefab";
        const string Barrel = "Assets/Prefabs/Interactables/Barricade_Barrel.prefab";
        const string Crate = "Assets/Prefabs/Interactables/Barricade_CrateLong.prefab";

        const float Gap = 0.05f, MaxSearch = 1.5f, SearchStep = 0.25f, FloorTolerance = 0.6f, NavReach = 1f;

        /// `at` = wanted centre of the prop on the floor (y = floor height), yaw in degrees.
        readonly struct Spot
        {
            public readonly string Name, Prefab, Reason;
            public readonly Vector3 At;
            public readonly float Yaw;
            public Spot(string name, string prefab, Vector3 at, float yaw, string reason)
            { Name = name; Prefab = prefab; At = at; Yaw = yaw; Reason = reason; }
        }

        static readonly Spot[] Spots =
        {
            // ---- throwables, in route order: spawn -> hall -> Tee -> warehouse/fusebox -> Lab stairs/Key room -> Lab
            new("Brick_EntranceCorridor", Brick, new Vector3(31f, -6f, 49.5f), 20f,
                "first pickup after the stairwell, before the Listener's hall: throw it ahead to pull the Listener"),
            new("Basin_EntranceHall_N", Basin, new Vector3(36f, -6f, 37.5f), 0f,
                "hall entrance: the loudest decoy (metal) for the Listener at its post"),
            new("Brick_EntranceHall_Ramp", Brick, new Vector3(55.5f, -6f, 25.5f), 70f,
                "beside the ramp mouth: on the way out, throw it back into the hall to cover the escape"),
            new("Brick_CorridorTee_N", Brick, new Vector3(76f, -12f, 37.5f), -30f,
                "Corridor Tee, the Ambusher's hub: a decoy to make it commit to the wrong branch"),
            new("Basin_CorridorCorner_LabDoor", Basin, new Vector3(82f, -12f, 11f), 90f,
                "next to the Lab bulkhead: throw it down the corridor to pull the Ambusher off the door"),
            new("Brick_SEBay_WarehouseDoor", Brick, new Vector3(71.5f, -24f, 67.5f), 45f,
                "beside Door_Slide_StorageHall: throw it into the warehouse to send the Tracker the wrong way"),
            new("Basin_Warehouse_Fusebox", Basin, new Vector3(50f, -24f, 80.5f), 15f,
                "near the fusebox: after the generator roars, throw it away to split the hunters"),
            new("Brick_CatwalkBay_Door", Brick, new Vector3(80.5f, -24f, 77f), -60f,
                "beside Door_Slide_CatwalkBay, on the way to the Lab stairs"),
            new("Basin_KeyRoom", Basin, new Vector3(92f, -30f, 27f), 30f,
                "Key room (dead end under the Mimic): a decoy to clear the way back out"),
            new("Brick_Lab", Brick, new Vector3(96f, -12f, 12f), 0f,
                "inside the Lab: throw it before taking the medicine, the alarm brings everyone"),

            // ---- barricades: beside a doorway, never in it. Dragging one in is the player's choice.
            new("Crate_SEBay_WestDoor", Crate, new Vector3(70f, -24f, 63.6f), 0f,
                "drag 1.5 m north into Door_Slide_StorageHall to shut the warehouse behind you; the SE bay keeps its north door and the ramp"),
            new("Barrel_SEBay_NorthDoor", Barrel, new Vector3(75.5f, -24f, 73.9f), 0f,
                "drag into Door_Slide_CatwalkBay; the warehouse stays reachable through the west door"),
            new("Crate_Lab_Door", Crate, new Vector3(94.2f, -12f, 18f), 0f,
                "inside the Lab, beside the door: drag it across while taking the medicine, pull it away to leave"),
            new("Barrel_EntranceHall_Ramp", Barrel, new Vector3(56.2f, -6f, 35.7f), 0f,
                "beside the ramp mouth: drag it in on the way out to stall a hunter coming up from the Tee"),
        };

        [MenuItem("ECHO/Level/Install Throwables and Barricades")]
        static void Install()
        {
            var scene = EditorSceneManager.GetActiveScene();
            if (scene.path != ScenePath) { Debug.LogError($"[GameplayPropInstaller] Open {ScenePath} first."); return; }
            if (EditorApplication.isPlayingOrWillChangePlaymode) return;

            Undo.SetCurrentGroupName("Install Throwables and Barricades");
            int undoGroup = Undo.GetCurrentGroup();

            var old = GameObject.Find(RootName);
            if (old != null) Undo.DestroyObjectImmediate(old);
            var root = new GameObject(RootName);
            Undo.RegisterCreatedObjectUndo(root, "Interaction Props root");
            Physics.SyncTransforms();

            var log = new List<string>();
            int placed = 0;
            foreach (var spot in Spots)
            {
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(spot.Prefab);
                if (prefab == null) { log.Add($"ERROR {spot.Name}: prefab missing {spot.Prefab}"); continue; }
                var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab, root.transform);
                Undo.RegisterCreatedObjectUndo(go, "Place prop");
                go.name = spot.Name;
                if (Place(go, spot, out string result)) placed++;
                else Undo.DestroyObjectImmediate(go);
                log.Add(result);
            }

            Undo.CollapseUndoOperations(undoGroup);
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            string msg = $"[GameplayPropInstaller] {placed}/{Spots.Length} props placed:\n- " + string.Join("\n- ", log);
            if (placed == Spots.Length) Debug.Log(msg); else Debug.LogError(msg);
        }

        /// Play Mode only: pick up, throw, hearing, drag, routes, objective chain (see DevTools/ThrowingInGameTest).
        [MenuItem("ECHO/Dev/Run Throwing In-Game Test")]
        static void RunTest() => Echo.DevTools.ThrowingInGameTest.Launch();

        [MenuItem("ECHO/Dev/Run Throwing In-Game Test", true)]
        static bool CanRunTest() => EditorApplication.isPlaying && EditorSceneManager.GetActiveScene().path == ScenePath;

        /// Play Mode only: Rigidbody counts and physics time per frame (idle and all-awake), for the optimisation notes.
        [MenuItem("ECHO/Dev/Measure Physics Cost")]
        static void MeasurePhysics() => Echo.DevTools.PhysicsCostProbe.Launch();

        [MenuItem("ECHO/Dev/Measure Physics Cost", true)]
        static bool CanMeasurePhysics() => EditorApplication.isPlaying;

        // ---------------------------------------------------------------------------------- placement
        static bool Place(GameObject go, Spot spot, out string result)
        {
            var rot = Quaternion.Euler(0f, spot.Yaw, 0f);
            var own = go.GetComponentsInChildren<Collider>();
            go.transform.SetPositionAndRotation(Vector3.zero, rot);
            Physics.SyncTransforms();
            Bounds local = OwnBounds(own);                         // world bounds with the pivot at the origin
            Vector3 pivotToCentre = new(local.center.x, 0f, local.center.z);
            float pivotToBottom = local.min.y;

            for (float r = 0f; r <= MaxSearch + 0.001f; r += SearchStep)
            {
                int tries = r == 0f ? 1 : Mathf.Max(8, Mathf.RoundToInt(2f * Mathf.PI * r / SearchStep));
                for (int i = 0; i < tries; i++)
                {
                    float a = i * Mathf.PI * 2f / tries;
                    Vector3 centre = spot.At + new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)) * r;
                    if (!FindFloor(centre, spot.At.y, own, out RaycastHit floor)) continue;

                    Vector3 pivot = new Vector3(centre.x, floor.point.y + 0.01f - pivotToBottom, centre.z) - pivotToCentre;
                    go.transform.SetPositionAndRotation(pivot, rot);
                    Physics.SyncTransforms();
                    if (!Clear(own, out string blocker)) continue;
                    if (!NavMesh.SamplePosition(floor.point, out var nav, NavReach, NavMesh.AllAreas) ||
                        Mathf.Abs(nav.position.y - floor.point.y) > 0.5f) continue;

                    Vector3 c = OwnBounds(own).center;
                    result = $"{spot.Name} at ({c.x:0.00}, {floor.point.y:0.00}, {c.z:0.00}), {r:0.00} m from the rough spot, " +
                             $"floor '{floor.collider.name}' - {spot.Reason}";
                    return true;
                }
            }
            result = $"ERROR {spot.Name}: no clear floor within {MaxSearch} m of {spot.At} - not placed";
            return false;
        }

        /// Flat static floor under `xz`, near the expected height, not the prop itself and not a loose prop.
        static bool FindFloor(Vector3 xz, float floorY, Collider[] own, out RaycastHit floor)
        {
            var from = new Vector3(xz.x, floorY + 1.2f, xz.z);
            var hits = Physics.RaycastAll(from, Vector3.down, 1.2f + FloorTolerance, ~0, QueryTriggerInteraction.Ignore);
            System.Array.Sort(hits, (p, q) => p.distance.CompareTo(q.distance));
            foreach (var h in hits)
            {
                if (System.Array.IndexOf(own, h.collider) >= 0) continue;
                floor = h;
                return h.rigidbody == null && h.normal.y > 0.95f && Mathf.Abs(h.point.y - floorY) <= FloorTolerance;
            }
            floor = default;
            return false;
        }

        /// The prop's bounds grown by Gap sideways and on top, lifted off the floor, must touch no other collider.
        static bool Clear(Collider[] own, out string blocker)
        {
            Bounds b = OwnBounds(own);
            Vector3 half = b.extents + new Vector3(Gap, 0f, Gap);
            half.y = b.extents.y - 0.02f + Gap * 0.5f;
            Vector3 centre = b.center + Vector3.up * (0.02f + Gap * 0.5f);
            foreach (var c in Physics.OverlapBox(centre, half, Quaternion.identity, ~0, QueryTriggerInteraction.Ignore))
            {
                if (System.Array.IndexOf(own, c) >= 0) continue;
                blocker = c.name;
                return false;
            }
            blocker = null;
            return true;
        }

        static Bounds OwnBounds(Collider[] own)
        {
            Bounds b = own[0].bounds;
            for (int i = 1; i < own.Length; i++) b.Encapsulate(own[i].bounds);
            return b;
        }
    }
}
