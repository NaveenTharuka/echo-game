using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Echo.AI;
using Echo.Gameplay;
using Echo.Interaction;
using Echo.Player;
using Echo.Sound;
using UnityEngine;
using UnityEngine.AI;

namespace Echo.DevTools
{
    /// ECHO > Dev > Run Throwing In-Game Test (Play Mode, SampleScene). Checks the props placed by
    /// ECHO > Level > Install Throwables and Barricades in the real level. Logs "[ThrowingInGame]" lines:
    ///   1. every prop rests on the floor where it was placed (no fall, no float, no overlap),
    ///   2. with the barricades carving, the NavMesh still links spawn, every objective and every agent,
    ///   3. pick up the throwable nearest an agent, hold it, throw it: impact noise -> an agent's Hearing,
    ///   4. drag each barricade 1.5 m (then put it back),
    ///   5. the objective chain (Phase2SelfTest "objective") still ends in Won,
    ///   6. no console errors during the run.
    /// The player is invulnerable and live input is off for the run (PlayerTestRig).
    public class ThrowingInGameTest : MonoBehaviour
    {
        const string Tag = "[ThrowingInGame] ";
        const string RootName = "Interaction Props";

        readonly StringBuilder report = new();
        int passed, failed, consoleErrors;
        readonly List<string> errorLines = new();

        PlayerTestRig rig;
        PlayerInteractor interactor;
        PhysicsCarrier carrier;
        ObjectiveDirector director;
        readonly List<Carryable> throwables = new(), barricades = new();

        /// Every Carryable's pose as saved in the scene (taken after Awake, before the first physics step), so the
        /// test can tell whether a prop fell or slid when the level started.
        static readonly Dictionary<int, (Vector3, Quaternion)> LoadPoses = new();

#if UNITY_EDITOR
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void RecordLoadPoses()
        {
            LoadPoses.Clear();
            foreach (var c in FindObjectsByType<Carryable>(FindObjectsSortMode.None))
                LoadPoses[c.GetInstanceID()] = (c.transform.position, c.transform.rotation);
        }
#endif

        public static void Launch()
        {
            if (TrackerScenarios.RefuseIfBusy("throwing in-game test")) return;
            var run = new GameObject("_ThrowingInGameTest").AddComponent<ThrowingInGameTest>();
            DontDestroyOnLoad(run.gameObject);   // a kill reloads the scene: the run must survive to report it
            TrackerScenarios.Claim(run, "throwing in-game test");
            run.StartCoroutine(run.Run());
        }

        void OnEnable() => Application.logMessageReceived += OnLog;
        void OnDisable() => Application.logMessageReceived -= OnLog;

        void OnLog(string text, string stack, LogType type)
        {
            if (type is LogType.Error or LogType.Exception or LogType.Assert && !text.StartsWith(Tag))
            {
                consoleErrors++;
                if (errorLines.Count < 5) errorLines.Add(text.Split('\n')[0]);
            }
        }

        void Check(string what, bool ok, string detail)
        {
            if (ok) passed++; else failed++;
            string line = $"{(ok ? "PASS" : "FAIL")}  {what}  ({detail})";
            report.AppendLine(line);
            if (ok) Debug.Log(Tag + line); else Debug.LogWarning(Tag + line);
        }

        IEnumerator Run()
        {
            interactor = FindFirstObjectByType<PlayerInteractor>();
            director = FindFirstObjectByType<ObjectiveDirector>();
            var root = GameObject.Find(RootName);
            if (interactor == null || director == null || root == null)
            {
                Debug.LogError(Tag + $"needs the player, ObjectiveDirector and '{RootName}' (run ECHO > Level > Install Throwables and Barricades)");
                Destroy(gameObject);
                yield break;
            }
            carrier = interactor.GetComponent<PhysicsCarrier>();
            foreach (var c in root.GetComponentsInChildren<Carryable>())
                (carrier.ModeFor(c) == PhysicsCarrier.Mode.Drag ? barricades : throwables).Add(c);
#if UNITY_EDITOR
            if (interactor.TryGetComponent(out PlayerDeath death) && !death.Invulnerable) death.SetInvulnerable(true);
#endif
            while (InteractionNoise.IsMuted) yield return null;
            rig = new PlayerTestRig(interactor);
            Vector3 spawn = rig.Origin;

            yield return Resting();
            yield return Routes(spawn);
            yield return PickUpAndThrow();
            yield return Drags();
            yield return ConvertedProps();
            rig.Restore();
            yield return ObjectiveChain();

            Check("no console errors during the run", consoleErrors == 0,
                  consoleErrors == 0 ? "0" : $"{consoleErrors}: {string.Join(" | ", errorLines)}");
            string msg = $"{Tag}{passed}/{passed + failed} checks passed\n{report}";
            if (failed == 0) Debug.Log(msg); else Debug.LogError(msg);
            Destroy(gameObject);
        }

        // ------------------------------------------------------------------ 1. resting
        IEnumerator Resting()
        {
            var all = new List<Carryable>(throwables);
            all.AddRange(barricades);
            var start = new Dictionary<Carryable, Vector3>();
            foreach (var c in all) start[c] = c.Body.position;
            var level = new Dictionary<Carryable, (Vector3 pos, Quaternion rot)>();
            foreach (var c in FindObjectsByType<Carryable>(FindObjectsSortMode.None))
                if (!start.ContainsKey(c) && LoadPoses.TryGetValue(c.GetInstanceID(), out var pose)) level[c] = pose;
            yield return new WaitForSeconds(2f);
            var restless = new List<string>();
            foreach (var kv in level)
            {
                float moved = Vector3.Distance(kv.Key.Body.position, kv.Value.pos);
                float turned = Quaternion.Angle(kv.Key.Body.rotation, kv.Value.rot);
                if (moved > 0.05f || turned > 2f || !kv.Key.Body.IsSleeping() && kv.Key.Body.linearVelocity.magnitude > 0.05f)
                    restless.Add($"{kv.Key.name} moved {moved:0.00} m turned {turned:0} deg");
            }
            Check($"converted level props ({level.Count}) rest where they stood at start", restless.Count == 0,
                  restless.Count == 0 ? "all still" : string.Join(", ", restless));
            foreach (var c in all)
            {
                float moved = Vector3.Distance(c.Body.position, start[c]);
                Bounds b = Bounds(c);
                float gap = FloorGap(c, b);
                string blocker = Overlap(c);
                Check($"{c.name} rests on the floor", moved < 0.1f && Mathf.Abs(gap) < 0.06f && blocker == null && c.Body.linearVelocity.magnitude < 0.05f,
                      $"moved {moved:0.000} m, gap {gap:0.000} m, overlap {blocker ?? "none"}, v {c.Body.linearVelocity.magnitude:0.00}");
            }
        }

        /// Height of the prop's bottom above the first other collider under its centre (+inf if none within 0.5 m).
        static float FloorGap(Carryable c, Bounds b)
        {
            var hits = Physics.RaycastAll(b.center, Vector3.down, b.extents.y + 0.5f, ~0, QueryTriggerInteraction.Ignore);
            float best = float.PositiveInfinity;
            foreach (var h in hits)
                if (!h.transform.IsChildOf(c.transform) && h.distance < best) best = h.distance;
            return best - b.extents.y;
        }

        static Bounds Bounds(Carryable c)
        {
            Bounds b = c.Colliders[0].bounds;
            foreach (var col in c.Colliders) b.Encapsulate(col.bounds);
            return b;
        }

        /// Name of a collider inside the prop's bounds (shrunk 3 cm, lifted off the floor), or null.
        static string Overlap(Carryable c, Transform ignore = null)
        {
            Bounds b = Bounds(c);
            Vector3 half = b.extents - new Vector3(0.03f, 0.03f, 0.03f);
            foreach (var hit in Physics.OverlapBox(b.center + Vector3.up * 0.03f, half, Quaternion.identity, ~0, QueryTriggerInteraction.Ignore))
                if (System.Array.IndexOf(c.Colliders, hit) < 0 && !hit.transform.IsChildOf(c.transform) &&
                    (ignore == null || !hit.transform.IsChildOf(ignore)) &&
                    hit.GetComponentInParent<AgentBrain>() == null)   // an agent walking into it is not a wall
                    return hit.name;
            return null;
        }

        // ------------------------------------------------------------------ 2. routes
        IEnumerator Routes(Vector3 spawn)
        {
            float carveWait = 0f;
            foreach (var b in barricades)
                if (b.TryGetComponent(out NavMeshObstacle o)) carveWait = Mathf.Max(carveWait, o.carvingTimeToStationary);
            yield return new WaitForSeconds(carveWait + 0.5f);

            var points = new List<(string, Vector3)>
            {
                ("fusebox", director.powerSwitch.transform.position), ("key card", director.accessPass.transform.position),
                ("Lab door", director.labDoor.DoorwayCenter), ("medicine", director.medicine.transform.position),
                ("exit", director.exit.position),
            };
            foreach (var a in AgentRegistry.All) points.Add((a.DisplayName, a.transform.position));

            var path = new NavMeshPath();
            foreach (var (name, p) in points)
            {
                bool a = OnNav(spawn, out var from), b = OnNav(p, out var to);
                bool ok = a && b && NavMesh.CalculatePath(from, to, NavMesh.AllAreas, path) && path.status == NavMeshPathStatus.PathComplete;
                Check($"route spawn -> {name} open with barricades carving", ok, $"status {(a && b ? path.status.ToString() : "off NavMesh")}");
            }
        }

        static bool OnNav(Vector3 p, out Vector3 on)
        {
            // Floor below first (wall-mounted switches, pickups on tables).
            if (NavMesh.SamplePosition(p + Vector3.down * 1.5f, out var h, 2.5f, NavMesh.AllAreas)) { on = h.position; return true; }
            on = p;
            return false;
        }

        // ------------------------------------------------------------------ 3. pick up, hold, throw, heard
        IEnumerator PickUpAndThrow()
        {
            // The throwable closest to any agent (but not on top of it): its impact should be heard by that agent.
            Carryable prop = null; AgentBrain near = null; float best = float.MaxValue;
            foreach (var c in throwables)
                foreach (var a in AgentRegistry.All)
                {
                    float d = Vector3.Distance(c.Body.position, a.transform.position);
                    if (d > 5f && d < best) { best = d; prop = c; near = a; }
                }
            if (prop == null) { Check("throwable near an agent", false, $"{throwables.Count} throwables, {AgentRegistry.All.Count} agents"); yield break; }

            if (!StandBy(prop, 1.1f, out Vector3 stand)) { Check($"stand next to {prop.name}", false, "no clear spot on the NavMesh"); yield break; }
            rig.Aim(stand, prop.Body.worldCenterOfMass);
            yield return null;
            bool focused = ReferenceEquals(interactor.Focus, prop);
            bool used = interactor.TryInteract();
            Check($"pick up {prop.name} (E)", focused && used && carrier.Held == prop && carrier.HeldMode == PhysicsCarrier.Mode.Lift,
                  $"focus {(interactor.Focus is Object f && f != null ? f.name : "none")}, used {used}, mode {carrier.HeldMode}");
            if (carrier.Held != prop) yield break;

            // Look up towards the agent (the hold point follows the view; looking at the floor would press it down).
            Vector3 target = near.transform.position + Vector3.up * 2f;
            rig.Look(target);
            yield return new WaitForSeconds(1f);
            float holdError = Vector3.Distance(carrier.HoldPoint, prop.Body.worldCenterOfMass);
            Check($"hold {prop.name} in front of the camera", carrier.Held == prop && holdError < 0.3f, $"{holdError:0.00} m from the hold point");

            // Hearing: every perception of an impact made by this prop, per agent.
            var heard = new Dictionary<AgentBrain, PerceivedSound>();
            var ears = new List<(Hearing, System.Action<PerceivedSound>)>();
            foreach (var a in AgentRegistry.All)
                if (a.TryGetComponent(out Hearing ear))
                {
                    var agent = a;
                    System.Action<PerceivedSound> h = s => { if (s.Type is SoundType.ObjectDrop or SoundType.MetalImpact) heard[agent] = s; };
                    ear.Heard += h;
                    ears.Add((ear, h));
                }
            NoiseRecord impact = default; bool hit = false;
            void OnNoise(NoiseRecord r) { if (r.Emitter == prop.gameObject && !hit) { hit = true; impact = r; } }
            InteractionNoise.Emitted += OnNoise;

            carrier.Throw(0.6f);   // along the view: towards the agent, a little up
            Vector3 from = prop.Body.position;
            for (float t = 0f; t < 4f && !hit; t += Time.deltaTime) yield return null;
            yield return new WaitForSeconds(0.3f);   // hearing is synchronous, but a bounce may follow
            InteractionNoise.Emitted -= OnNoise;
            foreach (var (ear, h) in ears) ear.Heard -= h;

            Check($"throw {prop.name} (RMB): it flies and lands", carrier.Held == null && hit,
                  $"flew {Vector3.Distance(from, prop.Body.position):0.0} m, impact {(hit ? $"{impact.Type} {impact.Intensity:0.00} delivered {impact.Delivered}" : "none")}");
            var who = new StringBuilder();
            foreach (var kv in heard) who.Append($"{kv.Key.DisplayName} (loud {kv.Value.Loudness:0.00}, err {kv.Value.PositionError:0.0} m) ");
            Check("impact noise reaches an agent's Hearing", heard.Count > 0,
                  heard.Count > 0 ? who.ToString() : $"nearest agent {near.DisplayName} {best:0} m away heard nothing");
            AgentRegistry.ResetAll();
        }

        // ------------------------------------------------------------------ 4. drag
        IEnumerator Drags()
        {
            foreach (var crate in barricades)
            {
                Vector3 home = crate.Body.position; Quaternion homeRot = crate.Body.rotation;
                bool pull = false;
                if (!PushLine(crate, out Vector3 stand, out Vector3 dir) && !(pull = PullLine(crate, out stand, out dir)))
                { Check($"drag {crate.name}", false, "no free 2 m line to push or pull along"); continue; }

                rig.Aim(stand, Bounds(crate).center);
                yield return null;
                carrier.SimulateDragHold = true;
                bool used = ReferenceEquals(interactor.Focus, crate) && interactor.TryInteract();
                Vector3 before = Bounds(crate).center;
                if (pull) yield return WalkBackwards(dir, 1.5f, 2f);   // S key: keep facing the prop
                else yield return rig.Walk(dir, 1.5f, 2f);
                Vector3 moved = Bounds(crate).center - before;
                bool dragging = carrier.Held == crate && carrier.HeldMode == PhysicsCarrier.Mode.Drag;
                carrier.SimulateDragHold = false;
                yield return new WaitForFixedUpdate();
                yield return new WaitForFixedUpdate();
                float horizontal = new Vector2(moved.x, moved.z).magnitude;
                Check($"drag {crate.name} (hold E, {(pull ? "pull" : "push")})", used && dragging && horizontal > 1f && Mathf.Abs(moved.y) < 0.05f && carrier.Held == null,
                      $"used {used}, moved {horizontal:0.00} m, dy {moved.y:0.000}, released {carrier.Held == null}");

                // Put it back so the objective run and the next player see the level as installed.
                crate.Body.position = home; crate.Body.rotation = homeRot;
                crate.Body.linearVelocity = Vector3.zero; crate.Body.angularVelocity = Vector3.zero;
                crate.transform.SetPositionAndRotation(home, homeRot);
                Physics.SyncTransforms();
                yield return new WaitForSeconds(0.2f);
            }
        }

        /// A direction to push `c` `length` m with the prop's path free, a player spot behind it on the NavMesh (on the
        /// floor below - a stacked crate is pushed from the floor) and a clear view from the eye to the prop.
        bool PushLine(Carryable c, out Vector3 stand, out Vector3 dir, float length = 2f)
        {
            Bounds b = Bounds(c);
            float reach = Mathf.Max(b.extents.x, b.extents.z) + rig.Radius + 0.35f;
            for (int i = 0; i < 16; i++)
            {
                dir = Quaternion.Euler(0f, i * 22.5f, 0f) * Vector3.forward;
                if (!FloorSpot(c, b.center - dir * reach, out stand)) continue;
                if (!FloorSpot(c, b.center + dir * (reach + length), out _)) continue;
                if (!BoxCastOnlySelf(c, b, dir, length) || !CanSee(c, b, stand)) continue;
                return true;
            }
            stand = dir = Vector3.zero;
            return false;
        }

        /// Against a wall there is no room behind the prop: stand in front of it and back away (it follows).
        /// A direction where the player has `length` m of NavMesh to back into and the prop's path is free (but for the player).
        bool PullLine(Carryable c, out Vector3 stand, out Vector3 dir, float length = 2f)
        {
            Bounds b = Bounds(c);
            float reach = Mathf.Max(b.extents.x, b.extents.z) + rig.Radius + 0.35f;
            for (int i = 0; i < 16; i++)
            {
                dir = Quaternion.Euler(0f, i * 22.5f, 0f) * Vector3.forward;
                if (!FloorSpot(c, b.center + dir * reach, out stand)) continue;
                if (NavMesh.Raycast(stand, stand + dir * (length + 0.2f), out _, NavMesh.AllAreas)) continue;   // room to back into
                if (!BoxCastOnlySelf(c, b, dir, length) || !CanSee(c, b, stand)) continue;
                return true;
            }
            stand = dir = Vector3.zero;
            return false;
        }

        /// The NavMesh point on the floor below `p` (first surface under the prop's height that is not the prop).
        bool FloorSpot(Carryable c, Vector3 p, out Vector3 spot)
        {
            spot = default;
            float best = float.PositiveInfinity; Vector3 floor = default;
            foreach (var h in Physics.RaycastAll(p + Vector3.up * 0.5f, Vector3.down, 4f, ~0, QueryTriggerInteraction.Ignore))
                if (!h.transform.IsChildOf(c.transform) && !h.transform.IsChildOf(rig.Player) && h.distance < best) { best = h.distance; floor = h.point; }
            if (float.IsInfinity(best)) return false;
            if (!NavMesh.SamplePosition(floor, out var s, 0.3f, NavMesh.AllAreas)) return false;
            spot = s.position;
            return true;
        }

        /// The first thing on the line from the player's eye (standing at `feet`) to the prop is the prop.
        bool CanSee(Carryable c, Bounds b, Vector3 feet)
        {
            Vector3 eye = feet + rig.EyeOffset;
            foreach (var h in Physics.RaycastAll(eye, (b.center - eye).normalized, Vector3.Distance(eye, b.center) + 0.5f, ~0, QueryTriggerInteraction.Ignore).OrderBy(h => h.distance))
            {
                if (h.transform.IsChildOf(rig.Player)) continue;
                return h.transform.IsChildOf(c.transform);
            }
            return false;
        }

        /// Moves the player along `direction` with the real CharacterController without turning (walking backwards).
        IEnumerator WalkBackwards(Vector3 direction, float distance, float speed)
        {
            var cc = rig.Player.GetComponent<CharacterController>();
            float moved = 0f, stuck = 0f;
            while (moved < distance && stuck < 0.3f)
            {
                Vector3 before = rig.Player.position;
                float step = Mathf.Min(speed * Time.deltaTime, distance - moved);
                cc.Move(direction * step + Vector3.down * (2f * Time.deltaTime));
                float progress = Vector3.ProjectOnPlane(rig.Player.position - before, Vector3.up).magnitude;
                moved += progress;
                stuck = progress < step * 0.1f ? stuck + Time.deltaTime : 0f;
                yield return null;
            }
        }

        /// The prop's path along `dir` (`length` m) hits nothing but itself and the player.
        bool BoxCastOnlySelf(Carryable c, Bounds b, Vector3 dir, float length = 2f)
        {
            foreach (var h in Physics.BoxCastAll(b.center + Vector3.up * 0.05f, b.extents * 0.95f, dir, Quaternion.identity, length, ~0, QueryTriggerInteraction.Ignore))
                if (System.Array.IndexOf(c.Colliders, h.collider) < 0 && !h.transform.IsChildOf(rig.Player)) return false;
            return true;
        }

        /// A NavMesh spot `gap` m from the prop with a clear line from the eye to it.
        bool StandBy(Carryable c, float gap, out Vector3 stand)
        {
            Vector3 p = c.Body.worldCenterOfMass;
            for (int i = 0; i < 16; i++)
            {
                Vector3 cand = p + Quaternion.Euler(0f, i * 22.5f, 0f) * Vector3.forward * gap;
                if (!NavMesh.SamplePosition(cand, out var h, 0.6f, NavMesh.AllAreas)) continue;
                Vector3 eye = h.position + rig.EyeOffset;
                if (Physics.Linecast(eye, p, out var block, ~0, QueryTriggerInteraction.Ignore) && !block.transform.IsChildOf(c.transform)) continue;
                if (Physics.CheckCapsule(h.position + Vector3.up * (rig.Radius + 0.05f), h.position + Vector3.up * 1.6f, rig.Radius * 0.9f, ~0, QueryTriggerInteraction.Ignore)
                    && !OnlyPlayerOrProp(h.position, c)) continue;
                stand = h.position;
                return true;
            }
            stand = default;
            return false;
        }

        bool OnlyPlayerOrProp(Vector3 feet, Carryable c)
        {
            foreach (var col in Physics.OverlapCapsule(feet + Vector3.up * (rig.Radius + 0.05f), feet + Vector3.up * 1.6f, rig.Radius * 0.9f, ~0, QueryTriggerInteraction.Ignore))
                if (!col.transform.IsChildOf(rig.Player) && !col.transform.IsChildOf(c.transform)) return false;
            return true;
        }

        // ------------------------------------------------------------------ 4b. converted level props
        /// Every Carryable outside "Interaction Props" (the level's loose props made usable by ECHO > Level > Loose
        /// Props): at rest at start, highlighted with a backed prompt when aimed at, light ones lift, heavy ones drag
        /// and stay upright without ending up inside a wall. Each is put back where it was afterwards.
        IEnumerator ConvertedProps()
        {
            var props = new List<Carryable>();
            foreach (var c in FindObjectsByType<Carryable>(FindObjectsSortMode.None))
                if (!c.transform.IsChildOf(GameObject.Find(RootName).transform)) props.Add(c);
            props.Sort((a, b) => string.CompareOrdinal(a.name, b.name));
            var highlight = FindFirstObjectByType<Echo.UI.InteractionHighlight>();
            var prompt = FindFirstObjectByType<Echo.UI.InteractionPromptUI>();
            var backing = prompt != null ? prompt.transform.Find("PromptBacking") : null;

            int lifted = 0, dragged = 0, lit = 0;
            var failures = new List<string>();
            var unreachable = new List<string>();
            var tipped = new List<string>();
            var boxedIn = new List<string>();   // grabbed fine, but neighbours / walls leave no free metre to move it
            foreach (var c in props)
            {
                Vector3 home = c.Body.position; Quaternion homeRot = c.Body.rotation;
                bool drag = carrier.ModeFor(c) == PhysicsCarrier.Mode.Drag;
                bool pull = false, line = false, placed;
                Vector3 stand, dir = Vector3.zero;
                if (!drag) placed = StandBy(c, 1.1f, out stand) || GrabSpot(c, out stand);
                else if (PushLine(c, out stand, out dir, 1f)) placed = line = true;
                else if (PullLine(c, out stand, out dir, 1f)) placed = line = pull = true;
                else placed = GrabSpot(c, out stand);   // boxed in: grab only
                if (!placed) { unreachable.Add(c.name); continue; }

                rig.Aim(stand, Bounds(c).center);
                yield return null;
                yield return null;   // highlight builds in LateUpdate, the prompt in Update
                bool focused = ReferenceEquals(interactor.Focus, c);
                bool glow = highlight != null && highlight.VisibleShells > 0;
                bool backed = backing != null && backing.TryGetComponent(out UnityEngine.UI.Image img) && img.enabled;
                if (focused && glow && backed) lit++;

                if (!drag)
                {
                    bool ok = focused && interactor.TryInteract() && carrier.Held == c && carrier.HeldMode == PhysicsCarrier.Mode.Lift;
                    if (ok) lifted++; else failures.Add($"{c.name} (lift: focus {focused}, held {carrier.Held == c})");
                    carrier.Drop();
                }
                else if (!line)
                {
                    // Boxed in: it must still grab (hold E) and hold in drag mode; how far it goes is up to its neighbours.
                    carrier.SimulateDragHold = true;
                    bool used = focused && interactor.TryInteract();
                    yield return new WaitForFixedUpdate();
                    bool held = carrier.Held == c && carrier.HeldMode == PhysicsCarrier.Mode.Drag;
                    carrier.SimulateDragHold = false;
                    yield return new WaitForFixedUpdate();
                    if (used && held) { dragged++; boxedIn.Add(c.name); }
                    else failures.Add($"{c.name} (grab: focus {focused}, used {used}, held {held})");
                }
                else
                {
                    float distance = 0f, dy = 0f; bool used = false, held = false;
                    for (int attempt = 0; attempt < 2; attempt++)   // an agent walking into the path: wait and try again
                    {
                        if (attempt > 0)
                        {
                            yield return new WaitForSeconds(2f);
                            rig.Aim(stand, Bounds(c).center);
                            yield return null;
                        }
                        carrier.SimulateDragHold = true;
                        used = ReferenceEquals(interactor.Focus, c) && interactor.TryInteract();
                        Vector3 before = Bounds(c).center;
                        if (pull) yield return WalkBackwards(dir, 0.8f, 2f); else yield return rig.Walk(dir, 0.8f, 2f);
                        Vector3 moved = Bounds(c).center - before;
                        held = carrier.Held == c && carrier.HeldMode == PhysicsCarrier.Mode.Drag;
                        carrier.SimulateDragHold = false;
                        yield return new WaitForFixedUpdate();
                        yield return new WaitForFixedUpdate();
                        distance = new Vector2(moved.x, moved.z).magnitude; dy = moved.y;
                        if (used && held && distance > 0.4f) break;
                        Restore(c, home, homeRot);
                    }
                    float tilt = Vector3.Angle(c.transform.up, homeRot * Vector3.up);
                    string inside = Overlap(c, rig.Player);   // the player stands against what it pushed
                    bool ok = used && held && distance > 0.4f;
                    if (ok) dragged++; else failures.Add($"{c.name} (drag {(pull ? "pull" : "push")}: used {used}, held {held}, moved {distance:0.00} m)");
                    if (tilt > 3f || Mathf.Abs(dy) > 0.1f || inside != null)
                        tipped.Add($"{c.name} (tilt {tilt:0.0} deg, dy {dy:0.00} m, inside {inside ?? "none"})");
                }

                Restore(c, home, homeRot);
            }
            int lightN = props.FindAll(p => carrier.ModeFor(p) == PhysicsCarrier.Mode.Lift).Count;
            Check($"converted props: every light one lifts (E), every heavy one drags (hold E)",
                  failures.Count == 0 && unreachable.Count == 0,
                  $"{props.Count} props: {lifted}/{lightN} lifted, {dragged}/{props.Count - lightN} dragged " +
                  $"({dragged - boxedIn.Count} moved 0.4+ m, {boxedIn.Count} grabbed but boxed in by neighbours: {string.Join(", ", boxedIn)})" +
                  (unreachable.Count > 0 ? $"; no spot to use: {string.Join(", ", unreachable)}" : "") +
                  (failures.Count > 0 ? $"; failed: {string.Join(", ", failures)}" : ""));
            Check("dragged props stay upright, on the floor and out of walls", tipped.Count == 0,
                  tipped.Count == 0 ? "all" : string.Join(", ", tipped));
            Check("aimed-at prop: rim highlight and a backed [E] prompt", lit == props.Count - unreachable.Count,
                  $"{lit}/{props.Count - unreachable.Count} (highlight component {(highlight != null)}, backing {(backing != null)})");
        }

        static void Restore(Carryable c, Vector3 pos, Quaternion rot)
        {
            c.Body.linearVelocity = Vector3.zero; c.Body.angularVelocity = Vector3.zero;
            c.Body.position = pos; c.Body.rotation = rot;
            c.transform.SetPositionAndRotation(pos, rot);
            Physics.SyncTransforms();
        }

        /// Any spot on the floor 1-1.8 m out from the prop, with room for the player, from which the eye sees the prop
        /// within interaction range (stacked and boxed-in props: the player only needs to reach and see them).
        bool GrabSpot(Carryable c, out Vector3 stand)
        {
            Bounds b = Bounds(c);
            float ext = Mathf.Max(b.extents.x, b.extents.z);
            foreach (float d in new[] { 1.0f, 1.4f, 1.8f })
                for (int i = 0; i < 16; i++)
                {
                    Vector3 dir = Quaternion.Euler(0f, i * 22.5f, 0f) * Vector3.forward;
                    if (!FloorSpot(c, b.center + dir * (ext + d), out stand)) continue;
                    Vector3 eye = stand + rig.EyeOffset;
                    if (Vector3.Distance(eye, b.ClosestPoint(eye)) > interactor.range - 0.15f) continue;
                    if (Physics.CheckCapsule(stand + Vector3.up * (rig.Radius + 0.05f), stand + Vector3.up * 1.6f, rig.Radius * 0.9f, ~0, QueryTriggerInteraction.Ignore)
                        && !OnlyPlayerOrProp(stand, c)) continue;
                    if (CanSee(c, b, stand)) return true;
                }
            stand = default;
            return false;
        }

        // ------------------------------------------------------------------ 5. objective chain
        IEnumerator ObjectiveChain()
        {
            AgentRegistry.ResetAll();
            new GameObject("Phase2SelfTest").AddComponent<Phase2SelfTest>().Run("objective");
            float until = Time.time + 90f;
            while (Time.time < until && director != null && director.Current != ObjectiveDirector.Stage.Won) yield return null;
            Check("objective chain (fusebox, key card, Lab door, medicine, exit) ends in Won",
                  director != null && director.Current == ObjectiveDirector.Stage.Won,
                  director != null ? $"stage {director.Current}, power {director.PowerOn}, pass {director.HasPass}, medicine {director.HasMedicine}" : "director gone (scene reloaded?)");
        }
    }
}
