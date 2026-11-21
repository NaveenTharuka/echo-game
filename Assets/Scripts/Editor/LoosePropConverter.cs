using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Echo.Interaction;
using Echo.Navigation;
using Unity.AI.Navigation;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AI;

namespace Echo.EditorTools
{
    /// ECHO > Level > Loose Props > ... Makes the loose props along the player's routes in SampleScene usable:
    ///   <= 5 kg: E picks up, RMB throws.  > 5 kg: hold E to drag (PhysicsCarrier picks the mode by mass).
    /// Scene overrides only - the kit prefabs are not touched. Per prop:
    ///   - Rigidbody with a realistic mass (draggables: X/Z rotation frozen, they stay upright like the barricades),
    ///   - a primitive collider instead of a mesh collider (Box or Capsule fitted to the mesh), or the kit's Box kept,
    ///   - Carryable with the impact noise of its material (MetalImpact / ObjectDrop),
    ///   - NavMeshModifier "ignore from build"; draggables also get a NavMeshObstacle that carves only when stationary.
    /// Not converted: shelf rack contents ("Shelf Variation"), loaded pallets ("Pallet Variation"), wall-mounted props
    /// (more than 0.3 m above what is below them), anything outside the route zones (the Containment ward / Lab is out).
    /// "Rebake NavMesh" then bakes SampleScene the same way ECHO > SampleScene > 1 does (player, agents and door leaves
    /// hidden), so the converted props leave no stale holes - they carve where they stand instead.
    static class LoosePropConverter
    {
        const string ScenePath = "Assets/Scenes/SampleScene.unity";
        const string NavMeshPath = "Assets/Scenes/SampleScene_NavMesh.asset";
        const float WallMountedGap = 0.3f;

        enum Shape { KeepBox, KeepConvexElseBox, Box, Capsule }

        readonly struct Rule
        {
            public readonly Regex Name;
            public readonly string What;
            public readonly float Mass;
            public readonly NoiseType Noise;
            public readonly Shape Shape;
            public Rule(string name, string what, float mass, NoiseType noise, Shape shape)
            { Name = new Regex(name); What = what; Mass = mass; Noise = noise; Shape = shape; }
        }

        // Masses: real-world figures for the object each mesh shows (looked at in the editor, 22 Nov).
        static readonly Rule[] Rules =
        {
            new(@"^SM_Garbige_00$", "trash bag", 2f, NoiseType.ObjectDrop, Shape.Box),
            new(@"^SM_Garbige_04$", "tin can", 1f, NoiseType.MetalImpact, Shape.Capsule),
            new(@"^SM_Garbige_15$", "metal lid", 0.5f, NoiseType.MetalImpact, Shape.Box),
            new(@"^SM_Bricks_Brick_\d+$", "brick", 2.5f, NoiseType.ObjectDrop, Shape.Box),
            new(@"^Fire Extinguisher$", "extinguisher bracket (2 extinguishers)", 12f, NoiseType.MetalImpact, Shape.Box),
            new(@"^Crate Short$", "metal crate", 12f, NoiseType.MetalImpact, Shape.KeepBox),
            new(@"^Create Long$", "tall metal crate", 30f, NoiseType.MetalImpact, Shape.KeepBox),
            new(@"^Crate Long$", "long metal crate", 40f, NoiseType.MetalImpact, Shape.KeepBox),
            new(@"^Barrel$", "steel drum", 25f, NoiseType.MetalImpact, Shape.KeepConvexElseBox),
            new(@"^Cart$", "two-tier metal trolley", 20f, NoiseType.MetalImpact, Shape.KeepConvexElseBox),
            new(@"^Garbage Bin$", "plastic wheelie bin", 12f, NoiseType.ObjectDrop, Shape.KeepBox),
            new(@"^Pallet$", "wooden pallet", 20f, NoiseType.ObjectDrop, Shape.KeepBox),
            new(@"^SM_Object_01$", "hospital bed", 60f, NoiseType.MetalImpact, Shape.Box),
            new(@"^SM_Object_02$", "examination chair", 35f, NoiseType.MetalImpact, Shape.Box),
            new(@"^SM_Object_04$", "bed frame", 40f, NoiseType.MetalImpact, Shape.Box),
            new(@"^SM_Object_05$", "curtain screen", 12f, NoiseType.MetalImpact, Shape.Box),
            new(@"^SM_Object_10$", "metal crib", 15f, NoiseType.MetalImpact, Shape.Box),
            new(@"^SM_Object_12$", "overbed table", 10f, NoiseType.MetalImpact, Shape.Box),
            new(@"^SM_Object_13$", "IV stand (heavy base)", 7f, NoiseType.MetalImpact, Shape.Box),
        };

        static readonly Regex RouteZone = new(@"^(EntranceHall|EntranceCorridor|Stairwell|Ramp|RampsDown|CorridorTee|CorridorCorner|Passthrough|Bay_|Warehouse_)");
        static readonly Regex Numbered = new(@"\s*\(\d+\)$");

        /// Props that would be skipped for a reason (kept fixed): logged in the dry run. Set from the furniture check.
        static readonly HashSet<string> KeptFixed = new();

        [MenuItem("ECHO/Level/Loose Props/1. Dry Run (list only)")]
        static void DryRun() { if (Ready()) Report(Find(), false); }

        [MenuItem("ECHO/Level/Loose Props/2. Convert")]
        static void Convert()
        {
            if (!Ready()) return;
            var found = Find();
            var labels = found.Select(ShapeLabel).ToList();   // before: Apply changes what the label reads
            Undo.SetCurrentGroupName("Convert loose props");
            int group = Undo.GetCurrentGroup();
            foreach (var p in found) Apply(p);
            // My placed props (Interaction Props) are movable too: keep them out of the bake.
            foreach (var c in Object.FindObjectsByType<Carryable>(FindObjectsSortMode.None)) EnsureIgnoredByBake(c.gameObject);
            Undo.CollapseUndoOperations(group);
            var scene = EditorSceneManager.GetActiveScene();
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            Report(found, true, labels);
        }

        [MenuItem("ECHO/Level/Loose Props/3. Rebake NavMesh (movable props excluded)")]
        static void Rebake()
        {
            if (!Ready()) return;
            var surface = Object.FindFirstObjectByType<NavMeshSurface>();
            if (surface == null) { Debug.LogError("[LoosePropConverter] no NavMeshSurface in SampleScene"); return; }
            var scene = EditorSceneManager.GetActiveScene();
            // Same as SampleSceneAgentSetup / FacilitySandboxBuilder.BakeNavMesh: no player, agents or door leaves in it.
            var hidden = scene.GetRootGameObjects().Where(r => r.activeSelf && r.name is "PlayerCapsule" or "Agents").ToList();
            var leaves = Object.FindObjectsByType<Door>(FindObjectsSortMode.None).Where(d => d.leaf != null)
                               .SelectMany(d => d.leaf.GetComponentsInChildren<Collider>(true)).Where(c => c.enabled).ToList();
            foreach (var r in hidden) r.SetActive(false);
            foreach (var c in leaves) c.enabled = false;
            try { surface.BuildNavMesh(); }
            finally
            {
                foreach (var c in leaves) c.enabled = true;
                foreach (var r in hidden) r.SetActive(true);
            }
            AssetDatabase.DeleteAsset(NavMeshPath);
            AssetDatabase.CreateAsset(surface.navMeshData, NavMeshPath);
            EditorUtility.SetDirty(surface);
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            var tri = NavMesh.CalculateTriangulation();
            Debug.Log($"[LoosePropConverter] NavMesh rebaked: {tri.indices.Length / 3} triangles -> {NavMeshPath}");
        }

        /// The kit placed some props slightly inside each other (barrel clusters, crate stacks) or balanced on a mesh
        /// corner: as dynamic bodies they would jump or tip in the first second of play. This runs the physics in edit
        /// mode for the converted props only (every other body held still) and keeps the poses they come to rest in.
        [MenuItem("ECHO/Level/Loose Props/5. Settle Converted Props")]
        static void Settle()
        {
            if (!Ready()) return;
            var placed = GameObject.Find("Interaction Props");
            var props = Object.FindObjectsByType<Carryable>(FindObjectsSortMode.None)
                              .Where(c => placed == null || !c.transform.IsChildOf(placed.transform)).ToList();
            var bodies = props.Select(c => c.GetComponent<Rigidbody>()).Where(b => b != null).ToList();
            var held = Object.FindObjectsByType<Rigidbody>(FindObjectsSortMode.None)
                             .Where(b => !b.isKinematic && !bodies.Contains(b)).ToList();
            var start = props.ToDictionary(c => c, c => (c.transform.position, c.transform.rotation));

            Undo.RecordObjects(props.Select(c => (Object)c.transform).ToArray(), "Settle props");
            var mode = Physics.simulationMode;
            foreach (var b in held) b.isKinematic = true;
            try
            {
                Physics.simulationMode = SimulationMode.Script;
                Physics.SyncTransforms();
                for (int i = 0; i < 150; i++) Physics.Simulate(0.02f);   // 3 s
            }
            finally
            {
                Physics.simulationMode = mode;
                foreach (var b in held) b.isKinematic = false;
            }
            foreach (var b in bodies) { b.linearVelocity = Vector3.zero; b.angularVelocity = Vector3.zero; }

            var lines = new List<string>();
            int nudged = 0;
            foreach (var c in props)
            {
                float moved = Vector3.Distance(c.transform.position, start[c].position);
                float turned = Quaternion.Angle(c.transform.rotation, start[c].rotation);
                if (moved < 0.005f && turned < 0.5f) continue;
                nudged++;
                lines.Add($"{(moved > 0.3f ? "FELL? " : "")}{c.name}: moved {moved:0.000} m, turned {turned:0.0} deg");
            }
            var scene = EditorSceneManager.GetActiveScene();
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            Debug.Log($"[LoosePropConverter] Settled {props.Count} props, {nudged} moved:\n" + string.Join("\n", lines));
        }

        const string RimMaterialPath = "Assets/Art/Materials/Interaction/InteractRim.mat";

        /// Rim glow on the object under the crosshair: the material (Echo/InteractRim) and InteractionHighlight on the HUD.
        [MenuItem("ECHO/Level/Loose Props/4. Install Crosshair Highlight")]
        static void InstallHighlight()
        {
            if (!Ready()) return;
            var mat = AssetDatabase.LoadAssetAtPath<Material>(RimMaterialPath);
            if (mat == null)
            {
                var shader = Shader.Find("Echo/InteractRim");
                if (shader == null) { Debug.LogError("[LoosePropConverter] shader Echo/InteractRim not found"); return; }
                mat = new Material(shader) { name = "InteractRim" };
                AssetDatabase.CreateAsset(mat, RimMaterialPath);
            }
            var hud = Object.FindFirstObjectByType<Echo.UI.InteractionPromptUI>();
            if (hud == null) { Debug.LogError("[LoosePropConverter] no InteractionHUD in SampleScene"); return; }
            var hl = hud.GetComponent<Echo.UI.InteractionHighlight>();
            if (hl == null) hl = Undo.AddComponent<Echo.UI.InteractionHighlight>(hud.gameObject);
            Undo.RecordObject(hl, "Highlight");
            hl.interactor = hud.interactor != null ? hud.interactor : Object.FindFirstObjectByType<PlayerInteractor>();
            hl.rimMaterial = mat;
            var scene = EditorSceneManager.GetActiveScene();
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            Debug.Log($"[LoosePropConverter] Crosshair highlight on {hud.name} with {RimMaterialPath}");
        }

        static bool Ready()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) { Debug.LogError("[LoosePropConverter] Exit Play Mode first."); return false; }
            if (EditorSceneManager.GetActiveScene().path != ScenePath) { Debug.LogError($"[LoosePropConverter] Open {ScenePath} first."); return false; }
            return true;
        }

        // ---------------------------------------------------------------------------------- finding
        readonly struct Prop
        {
            public readonly GameObject Go;
            public readonly Rule Rule;
            public readonly string Zone;
            public Prop(GameObject go, Rule rule, string zone) { Go = go; Rule = rule; Zone = zone; }
        }

        static List<Prop> Find()
        {
            // ZoneVolume registers itself at runtime only: read the zone boxes directly.
            var zones = Object.FindObjectsByType<ZoneVolume>(FindObjectsSortMode.None)
                              .Select(z => (z.zoneId, box: z.GetComponent<BoxCollider>())).Where(z => z.box != null).ToList();
            string ZoneAt(Vector3 p) { foreach (var z in zones) if (z.box.bounds.Contains(p)) return z.zoneId; return null; }

            var list = new List<Prop>();
            foreach (var t in Object.FindObjectsByType<Transform>(FindObjectsSortMode.None))
            {
                string name = Numbered.Replace(t.name, "");
                int r = System.Array.FindIndex(Rules, x => x.Name.IsMatch(name));
                if (r < 0) continue;
                // The outermost object of that name (SM_ props are a container plus a mesh child of the same name).
                if (t.parent != null && Rules[r].Name.IsMatch(Numbered.Replace(t.parent.name, ""))) continue;
                if (Under(t, "Shelf Variation") || Under(t, "Pallet Variation") || Under(t, "Interaction Props")) continue;
                if (KeptFixed.Contains(t.name)) continue;
                var renderers = t.GetComponentsInChildren<Renderer>();
                if (renderers.Length == 0) continue;
                Bounds b = WorldBounds(renderers);
                string zone = ZoneAt(b.center);
                if (zone == null || !RouteZone.IsMatch(zone)) continue;
                if (GapBelow(t, b) > WallMountedGap) continue;   // hangs on a wall
                list.Add(new Prop(t.gameObject, Rules[r], zone));
            }
            return list.OrderBy(p => p.Zone).ThenBy(p => p.Go.name).ToList();
        }

        static bool Under(Transform t, string prefix)
        {
            for (var p = t.parent; p != null; p = p.parent) if (p.name.StartsWith(prefix)) return true;
            return false;
        }

        static float GapBelow(Transform t, Bounds b)
        {
            float best = float.PositiveInfinity;
            foreach (var h in Physics.RaycastAll(b.center, Vector3.down, b.extents.y + 6f, ~0, QueryTriggerInteraction.Ignore))
                if (!h.transform.IsChildOf(t)) best = Mathf.Min(best, b.min.y - h.point.y);
            return best;
        }

        static Bounds WorldBounds(Renderer[] rs)
        {
            Bounds b = rs[0].bounds;
            foreach (var r in rs) b.Encapsulate(r.bounds);
            return b;
        }

        /// The meshes' bounds in the prop's own space (for colliders and obstacles that rotate with it).
        static Bounds LocalBounds(GameObject root)
        {
            bool any = false; Bounds b = default;
            Matrix4x4 toRoot = root.transform.worldToLocalMatrix;
            foreach (var mf in root.GetComponentsInChildren<MeshFilter>())
            {
                if (mf.sharedMesh == null || mf.GetComponent<Renderer>() == null) continue;
                Bounds m = mf.sharedMesh.bounds;
                Matrix4x4 toLocal = toRoot * mf.transform.localToWorldMatrix;
                for (int i = 0; i < 8; i++)
                {
                    var corner = m.center + Vector3.Scale(m.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                    Vector3 p = toLocal.MultiplyPoint3x4(corner);
                    if (!any) { b = new Bounds(p, Vector3.zero); any = true; } else b.Encapsulate(p);
                }
            }
            return b;
        }

        // ---------------------------------------------------------------------------------- applying
        static void Apply(Prop p)
        {
            GameObject go = p.Go;
            Rule rule = p.Rule;
            bool drag = rule.Mass > 5f;
            Bounds local = LocalBounds(go);

            // Colliders: a primitive fitted to the mesh replaces mesh colliders (non-convex ones can't be dynamic anyway).
            // Barrels and carts keep the kit's convex hull when they have one: it is valid on a moving body and, unlike a
            // capsule's round ends, stacks (barrels stand on barrels here).
            Shape shape = rule.Shape;
            if (shape == Shape.KeepConvexElseBox)
                shape = KeepableColliders(go) ? Shape.KeepBox : Shape.Box;
            if (shape != Shape.KeepBox || go.GetComponentsInChildren<Collider>().Length == 0)
            {
                foreach (var c in go.GetComponentsInChildren<Collider>(true))
                    if (!(c is BoxCollider && c.gameObject == go && shape == Shape.Box) &&
                        !(c is CapsuleCollider && c.gameObject == go && shape == Shape.Capsule)) Undo.DestroyObjectImmediate(c);
                if (shape == Shape.Capsule)
                {
                    var cap = go.GetComponent<CapsuleCollider>();
                    if (cap == null) cap = Undo.AddComponent<CapsuleCollider>(go);
                    Undo.RecordObject(cap, "Fit capsule");
                    cap.direction = 1;
                    cap.center = local.center;
                    cap.radius = Mathf.Max(local.extents.x, local.extents.z);
                    cap.height = Mathf.Max(local.size.y, cap.radius * 2f);
                }
                else
                {
                    var box = go.GetComponent<BoxCollider>();
                    if (box == null) box = Undo.AddComponent<BoxCollider>(go);
                    Undo.RecordObject(box, "Fit box");
                    box.center = local.center;
                    box.size = Vector3.Max(local.size, new Vector3(0.04f, 0.04f, 0.04f));   // thin lids still collide
                }
            }

            // Rigidbody (the kit's scene copies had 1 kg bodies; the SM_ props had none).
            foreach (var extra in go.GetComponentsInChildren<Rigidbody>(true))
                if (extra.gameObject != go) Undo.DestroyObjectImmediate(extra);
            var rb = go.GetComponent<Rigidbody>();
            if (rb == null) rb = Undo.AddComponent<Rigidbody>(go);
            Undo.RecordObject(rb, "Prop body");
            rb.mass = rule.Mass;
            rb.isKinematic = false;
            rb.useGravity = true;
            rb.interpolation = RigidbodyInterpolation.None;            // only held / thrown props need it (Carryable)
            rb.collisionDetectionMode = CollisionDetectionMode.Discrete;
            rb.constraints = drag ? RigidbodyConstraints.FreezeRotationX | RigidbodyConstraints.FreezeRotationZ
                                  : RigidbodyConstraints.None;

            var carry = go.GetComponent<Carryable>();
            if (carry == null) carry = Undo.AddComponent<Carryable>(go);
            Undo.RecordObject(carry, "Carryable");
            carry.impactNoise = rule.Noise;

            EnsureIgnoredByBake(go);
            var obstacle = go.GetComponent<NavMeshObstacle>();
            if (drag)
            {
                if (obstacle == null) obstacle = Undo.AddComponent<NavMeshObstacle>(go);
                Undo.RecordObject(obstacle, "Obstacle");
                obstacle.shape = NavMeshObstacleShape.Box;
                obstacle.center = local.center;
                obstacle.size = local.size;
                obstacle.carving = true;
                obstacle.carveOnlyStationary = true;
                obstacle.carvingTimeToStationary = 0.5f;
            }
            else if (obstacle != null) Undo.DestroyObjectImmediate(obstacle);
        }

        /// True when every collider is a primitive or a convex mesh (fine on a dynamic body), and there is at least one.
        static bool KeepableColliders(GameObject go)
        {
            var cols = go.GetComponentsInChildren<Collider>();
            return cols.Length > 0 && cols.All(c => !(c is MeshCollider m) || m.convex);
        }

        static void EnsureIgnoredByBake(GameObject go)
        {
            var mod = go.GetComponent<NavMeshModifier>();
            if (mod == null) mod = Undo.AddComponent<NavMeshModifier>(go);
            else if (mod.ignoreFromBuild) return;
            Undo.RecordObject(mod, "Ignore from bake");
            mod.ignoreFromBuild = true;
        }

        static string ShapeLabel(Prop p) => p.Rule.Shape switch
        {
            Shape.KeepBox => "kit Box kept",
            Shape.KeepConvexElseBox => KeepableColliders(p.Go) ? "kit convex mesh kept" : "Box collider",
            _ => p.Rule.Shape + " collider",
        };

        static void Report(List<Prop> props, bool applied, List<string> labels = null)
        {
            var lines = props.Select((p, i) =>
            {
                Bounds b = WorldBounds(p.Go.GetComponentsInChildren<Renderer>());
                return $"{p.Zone}\t{p.Go.name}\t{p.Rule.What}\t{p.Rule.Mass:0.#} kg\t{(p.Rule.Mass > 5f ? "drag" : "throw")}\t{p.Rule.Noise}\t" +
                       $"{(labels != null ? labels[i] : ShapeLabel(p))}\t({b.center.x:0.0}, {b.min.y:0.0}, {b.center.z:0.0})";
            });
            int throwN = props.Count(p => p.Rule.Mass <= 5f);
            Debug.Log($"[LoosePropConverter] {(applied ? "Converted" : "Would convert")} {props.Count} props ({throwN} throw, {props.Count - throwN} drag):\n" + string.Join("\n", lines));
        }
    }
}
