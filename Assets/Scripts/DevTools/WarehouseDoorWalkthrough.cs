using System.Collections;
using Echo.Interaction;
using Echo.Navigation;
using Echo.UI;
using UnityEngine;

namespace Echo.DevTools
{
    /// Scripted Play Mode walk-through of the SampleScene doors (ECHO > Dev > Run Warehouse Door Walkthrough).
    /// Spawned at runtime only; never saved into the scene. Uses the real player: PlayerInteractor.TryInteract()
    /// (the E key path) and CharacterController.Move (real collisions). Logs PASS/FAIL per check and saves
    /// Game-view screenshots to Temp/Screenshots/walk_*.png.
    /// Objective flow under test: Containment is locked -> warehouse -> catwalk bay fusebox restores power ->
    /// back to the Passthrough -> into Containment.
    public class WarehouseDoorWalkthrough : MonoBehaviour
    {
        const string HallDoorName = "Door_Slide_StorageHall";
        const string CatwalkDoorName = "Door_Slide_CatwalkBay";
        const string BulkheadName = "Door_Bulkhead_Containment";
        const string FuseboxName = "Switch_Fusebox_Power";
        const string WallDoor02Name = "Wall Door 02";
        const string PowerMessage = "Power restored";

        // A point in the SE bay (the warehouse entrance bay) - tells each warehouse door which side the player comes from.
        static readonly Vector3 SeBayPoint = new(78f, -24f, 68f);

        PlayerTestRig rig;
        int passed, failed, shot;

        public static void Run()
        {
            if (FindFirstObjectByType<WarehouseDoorWalkthrough>() != null) { Debug.LogWarning("[Walkthrough] Already running."); return; }
            new GameObject("_WarehouseDoorWalkthrough (runtime)").AddComponent<WarehouseDoorWalkthrough>();
        }

        IEnumerator Start()
        {
            var interactor = FindFirstObjectByType<PlayerInteractor>();
            var hud = FindFirstObjectByType<InteractionPromptUI>();
            Door hall = FindDoor(HallDoorName), catwalk = FindDoor(CatwalkDoorName), bulkhead = FindDoor(BulkheadName);
            var fuse = GameObject.Find(FuseboxName)?.GetComponent<SwitchInteractable>();
            Transform wall02 = GameObject.Find(WallDoor02Name)?.transform;
            if (interactor == null || hud == null || hall == null || catwalk == null || bulkhead == null || fuse == null || wall02 == null)
            {
                Check("scene set up (interactor, HUD, three doors, power fusebox, Wall Door 02)", false,
                      $"interactor={interactor != null} hud={hud != null} hall={hall != null} catwalk={catwalk != null} " +
                      $"bulkhead={bulkhead != null} fuse={fuse != null} wall02={wall02 != null}");
                Finish();
                yield break;
            }

            while (InteractionNoise.IsMuted) yield return null;
            rig = new PlayerTestRig(interactor);
            float settle = hall.openDuration + 0.6f;

            // ---------------------------------------------------------------- 1. Containment, before power
            // The bulkhead's local +X faces away from Containment (the side the player arrives from).
            Vector3 bulkFront = Approach(bulkhead, bulkhead.DoorwayCenter + bulkhead.transform.right, 1.8f);
            rig.Aim(bulkFront, bulkhead.DoorwayCenter);
            bool focused = ReferenceEquals(interactor.Focus, bulkhead);
            string verb = bulkhead.GetPrompt(interactor);
            interactor.TryInteract();
            yield return new WaitForSeconds(settle);
            yield return Shot("containment_locked");
            yield return rig.Walk(bulkhead.DoorwayCenter - bulkFront, 3.6f);
            bool held = Signed(bulkhead, rig.Player.position) > 0f;
            Check("Containment bulkhead is locked before power (prompt Locked, stays shut, blocks the player)",
                  focused && verb == "Locked" && bulkhead.isLocked && !bulkhead.isOpen && held,
                  $"focused={focused} verb={verb} state={bulkhead.CurrentState} playerSigned={Signed(bulkhead, rig.Player.position):F2}");

            // 2. No way past it: walk along both tunnel walls, and cast a ray grid over the whole cross-section.
            yield return NoGapsCheck(bulkhead, bulkFront);

            // ---------------------------------------------------------------- 2. Warehouse
            // Storage hall door is a normal door now.
            Vector3 hallSe = Approach(hall, SeBayPoint, 1.8f);
            rig.Aim(hallSe, hall.DoorwayCenter);
            focused = ReferenceEquals(interactor.Focus, hall);
            verb = hall.GetPrompt(interactor);
            bool used = interactor.TryInteract();
            yield return WaitFor(() => hall.CurrentState == Door.State.Open, settle);
            bool hallOpened = hall.CurrentState == Door.State.Open;
            rig.Aim(hallSe, hall.DoorwayCenter);
            interactor.TryInteract();
            yield return WaitFor(() => hall.CurrentState == Door.State.Closed, settle + hall.retryInterval);
            Check("storage hall door is a normal door (prompt Open, opens, closes)",
                  focused && verb == "Open" && used && hallOpened && hall.CurrentState == Door.State.Closed && !hall.isLocked,
                  $"focused={focused} verb={verb} opened={hallOpened} state={hall.CurrentState}");

            // Catwalk bay door: open from the SE bay, walk through, close from the NE side.
            Vector3 catSe = Approach(catwalk, SeBayPoint, 1.8f);
            rig.Aim(catSe, catwalk.DoorwayCenter);
            focused = ReferenceEquals(interactor.Focus, catwalk);
            verb = catwalk.GetPrompt(interactor);
            used = interactor.TryInteract();
            yield return WaitFor(() => catwalk.CurrentState == Door.State.Open, settle);
            yield return Shot("catwalk_open");
            Check("catwalk door opens (prompt Open)", focused && verb == "Open" && used && catwalk.CurrentState == Door.State.Open,
                  $"focused={focused} verb={verb} used={used} state={catwalk.CurrentState}");

            yield return rig.Walk(catwalk.DoorwayCenter - catSe, 3.6f);
            bool inNe = SideOf(catwalk, rig.Player.position) != SideOf(catwalk, SeBayPoint) && Mathf.Abs(Signed(catwalk, rig.Player.position)) > 1f;
            Check("player walks through the open catwalk doorway", inNe, $"signedDistance={Signed(catwalk, rig.Player.position):F2}");

            rig.Look(catwalk.DoorwayCenter);
            focused = ReferenceEquals(interactor.Focus, catwalk);
            verb = catwalk.GetPrompt(interactor);
            used = interactor.TryInteract();
            yield return WaitFor(() => catwalk.CurrentState == Door.State.Closed, settle + catwalk.retryInterval);
            Check("catwalk door closes from the NE side (prompt Close)", focused && verb == "Close" && used && catwalk.CurrentState == Door.State.Closed,
                  $"focused={focused} verb={verb} state={catwalk.CurrentState}");

            // Fusebox visible from the catwalk doorway (clear line of sight at eye height).
            Collider fuseCol = fuse.GetComponentInChildren<Collider>();
            Vector3 doorwayEye = Floor(catwalk) + Vector3.up * rig.EyeOffset.y;
            Vector3 toFuse = fuseCol.bounds.center - doorwayEye;
            bool visible = Physics.Raycast(doorwayEye, toFuse.normalized, out RaycastHit los, toFuse.magnitude + 0.5f, ~0, QueryTriggerInteraction.Ignore)
                           && los.collider == fuseCol;
            Check("fusebox visible from the catwalk doorway", visible, $"firstHit={(los.collider != null ? los.collider.name : "none")} at {los.distance:F2}/{toFuse.magnitude:F2} m");

            // ---------------------------------------------------------------- 3. Restore power
            Vector3 fuseFront = fuse.transform.position + fuse.transform.right * 1.4f;
            fuseFront.y = Floor(catwalk).y;
            rig.Aim(fuseFront, fuseCol.bounds.center);
            focused = ReferenceEquals(interactor.Focus, fuse);
            verb = fuse.GetPrompt(interactor);
            used = interactor.TryInteract();
            yield return new WaitForSeconds(0.3f);   // let the message fade in
            string message = hud.CurrentMessage;
            yield return Shot("power_restored");
            Check($"fusebox restores power: Containment unlocks, \"{PowerMessage}\" on the HUD (prompt Restore power)",
                  focused && verb == "Restore power" && used && fuse.Activated && !bulkhead.isLocked && message == PowerMessage,
                  $"focused={focused} verb={verb} activated={fuse.Activated} bulkhead={bulkhead.CurrentState} message='{message}'");
            yield return new WaitForSeconds(fuse.messageSeconds);
            Check("power message hides after ~2 s", hud.CurrentMessage.Length == 0, $"message='{hud.CurrentMessage}'");

            // ---------------------------------------------------------------- 4. Into Containment
            rig.Aim(bulkFront, bulkhead.DoorwayCenter);
            focused = ReferenceEquals(interactor.Focus, bulkhead);
            verb = bulkhead.GetPrompt(interactor);
            used = interactor.TryInteract();
            yield return WaitFor(() => bulkhead.CurrentState == Door.State.Open, settle);
            yield return Shot("containment_open");
            yield return rig.Walk(bulkhead.DoorwayCenter - bulkFront, 6.5f);
            float beyond = -Signed(bulkhead, rig.Player.position);   // metres past the bulkhead, towards Containment
            yield return Shot("inside_containment");
            Check("bulkhead opens after power (prompt Open) and the player walks into Containment",
                  focused && verb == "Open" && used && bulkhead.CurrentState == Door.State.Open && beyond > 3.5f,
                  $"focused={focused} verb={verb} state={bulkhead.CurrentState} metresPastBulkhead={beyond:F2} (tunnel ends 3 m past it)");

            // ---------------------------------------------------------------- 5. Wall Door 02
            yield return WallDoor02(wall02);

            Finish();
        }

        /// The player cannot get round the closed bulkhead: walking along each tunnel wall stops at the bulkhead,
        /// and every ray in a grid over the tunnel cross-section (inset 5 cm) hits the bulkhead itself.
        IEnumerator NoGapsCheck(Door bulkhead, Vector3 front)
        {
            Transform root = bulkhead.transform.parent;
            Bounds local = new();
            bool any = false;
            foreach (BoxCollider b in root.GetComponentsInChildren<BoxCollider>())
            {
                if (b.isTrigger || !b.enabled || !b.gameObject.activeInHierarchy) continue;
                foreach (Vector3 corner in Corners(b))
                {
                    Vector3 p = root.InverseTransformPoint(corner);
                    if (!any) { local = new Bounds(p, Vector3.zero); any = true; } else local.Encapsulate(p);
                }
            }

            Vector3 lateral = root.forward;
            float half = local.extents.z, margin = rig.Radius + 0.05f;
            bool sidesHeld = true;
            string sideInfo = "";
            foreach (float s in new[] { -1f, 1f })
            {
                Vector3 start = front + lateral * (s * (half - margin) + Vector3.Dot(root.TransformPoint(local.center) - bulkhead.DoorwayCenter, lateral));
                rig.Aim(start, start - root.right + Vector3.up * rig.EyeOffset.y);
                yield return rig.Walk(-root.right, 3.6f);
                float signed = Signed(bulkhead, rig.Player.position);
                sideInfo += $" side{s:+0;-0}={signed:F2}";
                sidesHeld &= signed > 0f;
            }

            // Step back out of the ray grid first, so the rays can only meet the bulkhead (or a gap).
            rig.Aim(front, bulkhead.DoorwayCenter);
            int rays = 0, misses = 0;
            var missedBy = new System.Collections.Generic.HashSet<string>();
            for (float y = local.min.y + 0.05f; y <= local.max.y - 0.05f; y += 0.25f)
                for (float z = local.min.z + 0.05f; z <= local.max.z - 0.05f; z += 0.25f)
                {
                    rays++;
                    Vector3 origin = root.TransformPoint(new Vector3(local.max.x + 1f, y, z));
                    // Cast through everything (props may stand in front of the bulkhead): a ray is only a miss if
                    // it never meets the bulkhead, i.e. there is a gap.
                    bool reachedBulkhead = false;
                    foreach (RaycastHit h in Physics.RaycastAll(origin, -root.right, 2f, ~0, QueryTriggerInteraction.Ignore))
                        if (h.collider.transform.IsChildOf(root)) { reachedBulkhead = true; break; }
                    if (!reachedBulkhead)
                    {
                        misses++;
                        missedBy.Add($"gap at y={y:F2} z={z:F2}");
                    }
                }
            Check("no way past the bulkhead at the sides or top (walk along both walls + cross-section ray grid)",
                  sidesHeld && misses == 0,
                  $"{sideInfo} rays={rays} misses={misses} missedBy=[{string.Join(", ", missedBy)}] crossSection={local.size.z:F2}x{local.size.y:F2} m");
        }

        IEnumerator WallDoor02(Transform wall)
        {
            // Wall plane = the piece's local YZ; room side = towards the warehouse interior.
            Vector3 normal = wall.right;
            if (Vector3.Dot(SeBayPoint - wall.position, normal) < 0f) normal = -normal;
            float faceOffset = 0.15f;   // half the 0.3 m wall
            Vector3 solidSection = wall.TransformPoint(new Vector3(0f, 0f, 1.0f));   // left of the doorway (doorway z 2.26..3.71)

            Vector3 start = solidSection + normal * 1.6f;
            if (!Physics.Raycast(start + Vector3.up * 3f, Vector3.down, out RaycastHit floor, 6f, ~0, QueryTriggerInteraction.Ignore))
            {
                Check("player stops at the face of Wall Door 02", false, $"no walkable floor found in front of it at {start}");
                yield break;
            }
            start = floor.point + Vector3.up * 0.05f;
            rig.Aim(start, solidSection + Vector3.up * 1.4f);
            yield return null;
            yield return rig.Walk(-normal, 3f);
            float gap = Vector3.Dot(rig.Player.position - wall.position, normal) - faceOffset;   // capsule centre to wall face
            yield return Shot("walldoor02_face");
            Check("player stops at the face of Wall Door 02 (not inside the wall mesh)",
                  gap > rig.Radius - 0.05f && gap < rig.Radius + 0.25f,
                  $"capsuleCentreToFace={gap:F2} m (radius {rig.Radius:F2}) floorY={floor.point.y:F2} hit={floor.collider.name}");

            yield return ThrowAtWall(start, normal, solidSection, faceOffset, wall);
        }

        /// Full-charge throw of a 2 kg brick at the solid part of Wall Door 02: it must hit the wall's face and end
        /// on the room side - not pass through, not lodge inside. Brick and carrier are runtime-only (nothing saved).
        IEnumerator ThrowAtWall(Vector3 feet, Vector3 normal, Vector3 target, float faceOffset, Transform wall)
        {
            PhysicsCarrier carrier = rig.Interactor.GetComponent<PhysicsCarrier>();
            bool addedCarrier = carrier == null;
            if (addedCarrier) carrier = rig.Interactor.gameObject.AddComponent<PhysicsCarrier>();

            var brickGo = GameObject.CreatePrimitive(PrimitiveType.Cube);
            brickGo.name = "_WalkthroughBrick (runtime)";
            brickGo.transform.localScale = new Vector3(0.22f, 0.07f, 0.11f);
            var rb = brickGo.AddComponent<Rigidbody>();
            rb.mass = 2f;
            var brick = brickGo.AddComponent<Carryable>();

            float WallSigned(Vector3 p) => Vector3.Dot(p - wall.position, normal);
            // Throw from 3 m if there is floor there (a longer flight), else from where the walk started.
            Vector3 back = feet + normal * 1.4f;
            if (Physics.Raycast(back + Vector3.up * 2f, Vector3.down, out RaycastHit floor, 4f, ~0, QueryTriggerInteraction.Ignore))
                feet = floor.point + Vector3.up * 0.05f;
            rig.Aim(feet, target + Vector3.up * 1.4f);
            brickGo.transform.position = carrier.HoldPoint;
            rb.position = brickGo.transform.position;
            Physics.SyncTransforms();
            carrier.Use(brick);
            yield return new WaitForSeconds(0.5f);

            bool hit = false;
            Vector3 hitPoint = default;
            void OnImpact(NoiseRecord r) { if (!hit && r.Emitter == brickGo) { hit = true; hitPoint = r.Position; } }
            InteractionNoise.Emitted += OnImpact;
            bool held = carrier.Held == brick;
            carrier.Throw(1f);
            yield return WaitFor(() => hit, 1.5f);
            yield return new WaitForSeconds(1.5f);
            InteractionNoise.Emitted -= OnImpact;

            float end = WallSigned(rb.position);
            yield return Shot("walldoor02_throw");
            Check("brick thrown at 12 m/s hits Wall Door 02 and stays on the room side (no pass-through, not stuck in it)",
                  held && hit && Mathf.Abs(WallSigned(hitPoint) - faceOffset) < 0.15f && end > faceOffset + 0.02f,
                  $"held={held} hit={hit} hitFromFace={WallSigned(hitPoint) - faceOffset:F2} endFromCentre={end:F2} (face {faceOffset:F2})");

            Destroy(brickGo);
            if (addedCarrier) Destroy(carrier);
        }

        // ------------------------------------------------------------------------------- helpers
        static Door FindDoor(string rootName)
        {
            GameObject go = GameObject.Find(rootName);
            return go != null ? go.GetComponentInChildren<Door>() : null;
        }

        static Vector3[] Corners(BoxCollider b)
        {
            var c = new Vector3[8];
            Vector3 e = b.size * 0.5f;
            for (int i = 0; i < 8; i++)
                c[i] = b.transform.TransformPoint(b.center + new Vector3((i & 1) == 0 ? -e.x : e.x, (i & 2) == 0 ? -e.y : e.y, (i & 4) == 0 ? -e.z : e.z));
            return c;
        }

        /// Feet position `distance` metres from the doorway, on the side facing `towards`.
        static Vector3 Approach(Door d, Vector3 towards, float distance)
        {
            Vector3 n = d.transform.right;
            if (Vector3.Dot(towards - d.DoorwayCenter, n) < 0f) n = -n;
            return Floor(d) + n * distance;
        }

        static Vector3 Floor(Door d)
        {
            Vector3 p = d.DoorwayCenter;
            p.y = d.transform.parent.position.y + 0.05f;   // door prefab roots sit on the floor
            return p;
        }

        static float Signed(Door d, Vector3 p) => Vector3.Dot(p - d.DoorwayCenter, d.transform.right);
        static bool SideOf(Door d, Vector3 p) => Signed(d, p) > 0f;

        static IEnumerator WaitFor(System.Func<bool> done, float timeout)
        {
            float end = Time.time + timeout;
            while (!done() && Time.time < end) yield return null;
        }

        IEnumerator Shot(string name)
        {
            yield return new WaitForEndOfFrame();
            // Unity empties Temp/ on restart and CaptureScreenshot doesn't create folders.
            System.IO.Directory.CreateDirectory("Temp/Screenshots");
            ScreenCapture.CaptureScreenshot($"Temp/Screenshots/walk_{++shot:00}_{name}.png");
            yield return null;
        }

        void Check(string name, bool ok, string details = null)
        {
            if (ok) { passed++; Debug.Log("[Walkthrough] PASS " + name, this); }
            else { failed++; Debug.LogError($"[Walkthrough] FAIL {name}" + (details != null ? "  | " + details : ""), this); }
        }

        void Finish()
        {
            rig?.Restore(backToOrigin: false);
            string summary = $"[Walkthrough] {passed} passed, {failed} failed.";
            if (failed == 0) Debug.Log(summary, this); else Debug.LogError(summary, this);
            Destroy(gameObject);
        }
    }
}
