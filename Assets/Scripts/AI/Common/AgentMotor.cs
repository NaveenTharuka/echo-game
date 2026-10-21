using System.Collections.Generic;
using Echo.Navigation;
using UnityEngine;
using UnityEngine.AI;

namespace Echo.AI
{
    /// Movement for every agent (guide §8.2): plans a node route with A* over the NavGraph, walks it with the
    /// NavMeshAgent, opens closed doors on the way and reports Arrived / Failed (no path or stuck).
    /// Without a NavGraph in the scene it falls back to plain NavMesh walking, so greybox tests still work.
    ///
    /// smoothPath: the route (agent -> nodes -> goal, NavMesh corners in between) is turned once per MoveTo into one
    /// curve with rounded corners, then followed by pure pursuit: the NavMeshAgent is steered at a point `lookAhead`
    /// metres further along the curve. smoothRotation: the motor turns the body (speed-aware, turn in place first).
    [RequireComponent(typeof(NavMeshAgent))]
    public class AgentMotor : MonoBehaviour
    {
        public enum Gait { Sneak, Walk, Run }
        public float sneakSpeed = 1.2f, walkSpeed = 2.2f, runSpeed = 5f;
        public float waypointRadius = 0.8f;
        public float stuckTimeout = 2.5f;
        [Tooltip("Slow down while the body still faces away from the way it is going, so the agent turns into a path " +
                 "instead of gliding sideways (the walk cycle only matches forward movement).")]
        public bool slowWhileTurning = true;
        [Range(0.05f, 1f)] public float minTurnSpeedFactor = 0.25f;

        [Header("Chase (hunting)")]
        [Tooltip("Run speed while hunting (Urgency >= chaseUrgency). 0 = runSpeed. Faster than the player's sprint (5.6): " +
                 "a hunted player escapes by breaking the sound trail, not by holding Shift.")]
        public float chaseSpeed = 0f;
        public float chaseUrgency = 0.6f;
        [Tooltip("While chasing: keeps more speed through turns and turns faster at a run.")]
        [Range(0.05f, 1f)] public float chaseMinTurnSpeedFactor = 0.55f;
        public float chaseTurnRate = 360f;

        [Header("Path smoothing")]
        [Tooltip("Follow the A* route as one curve with rounded corners instead of sharp node-to-node turns.")]
        public bool smoothPath = true;
        [Tooltip("Pure pursuit: how far ahead on the curve the agent steers to (m).")]
        public float lookAhead = 1.5f;
        [Tooltip("Largest corner rounding (m). Shrinks on short legs and where the curve would leave the NavMesh.")]
        public float cornerRadius = 1.5f;

        [Header("Rotation")]
        [Tooltip("The motor turns the body instead of the NavMeshAgent: faster when slow, wider arcs when running.")]
        public bool smoothRotation = true;
        public float turnRateStill = 420f, turnRateAtRun = 260f;   // deg/s
        [Tooltip("Turn on the spot before walking off when the path starts behind the agent (never while running).")]
        public bool turnInPlace = true;
        public float turnInPlaceAngle = 60f;
        [Tooltip("Turn-in-place never holds the agent longer than this, so brain timing stays the same.")]
        public float maxTurnInPlaceTime = 0.4f;

        /// Set by the brain to express this agent's preferences (see each agent).
        public AStar.CostModifier CostModifier;

        /// 0 calm .. 1 hunting. Set by AgentBrain from its AlertLevel every frame.
        public float Urgency { get; set; }
        /// Running flat out after prey.
        public bool IsChasing => CurrentGait == Gait.Run && chaseSpeed > runSpeed && Urgency >= chaseUrgency;
        public bool IsMoving { get; private set; }
        public Vector3 Goal { get; private set; }
        public Gait CurrentGait { get; private set; }
        public IReadOnlyList<int> NodePath => nodePath;
        public bool ArrivedFlag { get; private set; }      // consumed by brains on their tick
        public bool FailedFlag { get; private set; }

        /// The smoothed route (empty while smoothPath is off); CurveIndex = segment the agent is on.
        public IReadOnlyList<Vector3> PathPoints => curve;
        public int CurveIndex => seg;
        public int NextNodeIndex => idx;
        public Vector3 LookAheadPoint { get; private set; }
        public bool IsTurningInPlace => turnUntil > 0f;
        public Vector3 Velocity => nav != null ? nav.velocity : Vector3.zero;

        NavMeshAgent nav;
        readonly List<int> nodePath = new();
        int idx; float lastProgressTime, bestRemaining, doorWaitUntil;
        Vector3 lookTarget; bool hasLook;

        // Curve buffers, reused by every MoveTo (no per-call allocations once they have grown).
        readonly List<Vector3> ctrl = new(), curve = new();
        readonly List<int> ctrlNode = new();
        readonly List<float> curveS = new(), nodeS = new();
        readonly Vector3[] corners = new Vector3[64], arc = new Vector3[13];
        NavMeshPath legPath;
        int seg; float s, total, doorStopS, turnUntil, nextRebuild;
        Vector3 lastDest, turnDir; bool rebuildAfterDoor, defaultAutoBraking;
        int externalFaceFrame = -10;

        void Awake()
        {
            nav = GetComponent<NavMeshAgent>();
            legPath = new NavMeshPath();
            defaultAutoBraking = nav.autoBraking;
        }

        public float SpeedFor(Gait g) => g == Gait.Run ? (chaseSpeed > runSpeed && Urgency >= chaseUrgency ? chaseSpeed : runSpeed)
                                       : g == Gait.Walk ? walkSpeed : sneakSpeed;

        public bool MoveTo(Vector3 destination, Gait gait)
        {
            bool wasMoving = IsMoving;
            ArrivedFlag = FailedFlag = false; hasLook = false;
            if (!nav.isOnNavMesh || !NavMesh.SamplePosition(destination, out var hit, 3f, NavMesh.AllAreas))
            { IsMoving = false; FailedFlag = true; return false; }
            Goal = hit.position; CurrentGait = gait; nav.speed = SpeedFor(gait);

            nodePath.Clear();
            var g = NavGraph.Instance;
            if (g != null && g.Count > 0)
            {
                int st = g.Nearest(transform.position), t = g.Nearest(Goal);
                if (st != t && !AStar.FindPath(g, st, t, nodePath, CostModifier))
                { IsMoving = false; FailedFlag = true; return false; }

                // Skip the first node if it lies behind us.
                if (nodePath.Count > 1 &&
                    Vector3.Distance(transform.position, g.Position(nodePath[1])) <
                    Vector3.Distance(g.Position(nodePath[0]), g.Position(nodePath[1])))
                    nodePath.RemoveAt(0);
                // The final node is only a hint: walk straight to the goal if it is closer than that node.
                if (nodePath.Count > 0 &&
                    Vector3.Distance(transform.position, Goal) < Vector3.Distance(transform.position, g.Position(nodePath[0])))
                    nodePath.Clear();
            }

            idx = 0; IsMoving = true; ResetProgress();
            rebuildAfterDoor = false; turnUntil = 0f;
            nav.isStopped = false;
            if (smoothPath)
            {
                BuildCurve();
                Steer();
                if (!wasMoving) BeginTurnInPlace();
            }
            else { curve.Clear(); nav.autoBraking = defaultAutoBraking; nav.SetDestination(CurrentTarget()); }
            return true;
        }

        public void SetGait(Gait gait) { CurrentGait = gait; nav.speed = SpeedFor(gait); }

        public void Stop()
        {
            IsMoving = false; nodePath.Clear(); hasLook = false;
            curve.Clear(); turnUntil = 0f; rebuildAfterDoor = false;
            if (nav != null && nav.isOnNavMesh) { nav.isStopped = true; nav.ResetPath(); nav.autoBraking = defaultAutoBraking; }
        }

        /// Teleports the agent (respawn / reset). Use this, never transform.position, while a NavMeshAgent is on.
        public void Warp(Vector3 position)
        {
            Stop();
            nav.Warp(position);
            ArrivedFlag = FailedFlag = false;
        }

        /// Attack lunge: moves the body by `delta` along the NavMesh this frame (no pathing; used during the wind-up).
        public void Lunge(Vector3 delta)
        {
            if (nav != null && nav.isOnNavMesh) nav.Move(delta);
        }

        /// Keep turning towards `p` every frame while standing still (brains think at 5 Hz, too slow to turn).
        /// Cleared by the next MoveTo / Stop.
        public void LookAt(Vector3 p) { lookTarget = p; hasLook = true; }

        /// Called by others (e.g. the claw wind-up): the motor's own turning steps aside for that frame.
        public void FaceTowards(Vector3 p, float degPerSec = 240f)
        {
            externalFaceFrame = Time.frameCount;
            Rotate(p - transform.position, degPerSec);
        }

        void Rotate(Vector3 d, float degPerSec)
        {
            d.y = 0f;
            if (d.sqrMagnitude < 0.01f) return;
            transform.rotation = Quaternion.RotateTowards(transform.rotation,
                                    Quaternion.LookRotation(d), degPerSec * Time.deltaTime);
        }

        Vector3 CurrentTarget() => idx < nodePath.Count ? NavGraph.Instance.Position(nodePath[idx]) : Goal;
        void ResetProgress() { lastProgressTime = Time.time; bestRemaining = float.MaxValue; }

        void Update()
        {
            if (nav.updateRotation == smoothRotation) nav.updateRotation = !smoothRotation;
            if (!IsMoving) { if (hasLook) Rotate(lookTarget - transform.position, 240f); return; }

            if (smoothPath)
            {
                if (curve.Count == 0) BuildCurve();   // switched on mid-move
                UpdateCurve();
            }
            else
            {
                if (curve.Count > 0) { curve.Clear(); nav.autoBraking = defaultAutoBraking; nav.SetDestination(CurrentTarget()); }
                UpdateNodeByNode();
            }
            if (IsMoving && smoothRotation && turnUntil <= 0f) SteerRotation();
        }

        // ------------------------------------------------------------------ original node-to-node following
        void UpdateNodeByNode()
        {
            if (nav.pathPending) return;
            if (slowWhileTurning) ScaleSpeedForTurn();
            if (Time.time < doorWaitUntil) { ResetProgress(); return; }

            bool last = idx >= nodePath.Count;
            if (nav.remainingDistance <= (last ? nav.stoppingDistance + 0.15f : waypointRadius))
            {
                if (last) { IsMoving = false; ArrivedFlag = true; return; }

                // About to traverse edge nodePath[idx] -> nodePath[idx+1]: open a closed door first.
                if (!OpenDoorAhead()) return;
                idx++; ResetProgress();
                nav.SetDestination(CurrentTarget());
                return;
            }

            if (nav.remainingDistance < bestRemaining - 0.2f) { bestRemaining = nav.remainingDistance; lastProgressTime = Time.time; }
            else if (Time.time - lastProgressTime > stuckTimeout) { Stop(); FailedFlag = true; }
        }

        /// Opens a closed door on edge idx -> idx+1. False if it is locked (the motor has then failed).
        bool OpenDoorAhead()
        {
            if (idx + 1 < nodePath.Count && TryGetDoor(nodePath[idx], nodePath[idx + 1], out var door) && !door.isOpen)
            {
                if (door.isLocked) { Stop(); FailedFlag = true; return false; }
                door.SetOpen(true, gameObject, byAgent: true);   // makes a door sound the player hears
                doorWaitUntil = Time.time + door.OpenDelay;
                rebuildAfterDoor = smoothPath;
            }
            return true;
        }

        // ------------------------------------------------------------------ smoothed curve following
        void UpdateCurve()
        {
            if (turnUntil > 0f && TurnInPlace()) { ResetProgress(); return; }
            if (slowWhileTurning) ScaleSpeedForTurn();

            bool doorWait = Time.time < doorWaitUntil;
            if (!doorWait && rebuildAfterDoor) { rebuildAfterDoor = false; BuildCurve(); }

            if (Track() && Time.time >= nextRebuild) { nextRebuild = Time.time + 0.5f; BuildCurve(); }   // pushed off the route

            // Passed a node: same door rule as node-to-node (open a closed door on the next edge).
            while (idx < nodePath.Count && s + waypointRadius >= nodeS[idx])
            {
                if (!OpenDoorAhead()) return;
                idx++;
            }

            doorStopS = DoorStop();   // cheap; also catches doors opened / closed by others meanwhile
            Steer();

            if (idx >= nodePath.Count && LookAheadPoint == Goal && !nav.pathPending &&
                nav.remainingDistance <= nav.stoppingDistance + 0.15f && total - s <= lookAhead + nav.stoppingDistance)
            { IsMoving = false; ArrivedFlag = true; nav.autoBraking = defaultAutoBraking; return; }

            if (doorWait) { ResetProgress(); return; }
            float remaining = total - s;
            if (remaining < bestRemaining - 0.2f) { bestRemaining = remaining; lastProgressTime = Time.time; }
            else if (Time.time - lastProgressTime > stuckTimeout) { Stop(); FailedFlag = true; }
        }

        /// Pure pursuit: aim the NavMeshAgent at the point lookAhead metres ahead (never past a closed door).
        void Steer()
        {
            float target = Mathf.Min(s + lookAhead, total, doorStopS);
            bool braking = target >= total - 0.01f || target >= doorStopS - 0.01f;
            LookAheadPoint = target >= total - 0.01f ? Goal : PointAt(target);
            nav.autoBraking = braking ? defaultAutoBraking : false;
            if ((LookAheadPoint - lastDest).sqrMagnitude > 0.09f || (braking && LookAheadPoint != lastDest))
            {
                nav.SetDestination(LookAheadPoint);
                lastDest = LookAheadPoint;
            }
        }

        /// Builds the curve from where the agent stands. Called by MoveTo, after a door, or when pushed off the route.
        void BuildCurve()
        {
            ctrl.Clear(); ctrlNode.Clear();
            ctrl.Add(transform.position); ctrlNode.Add(-1);
            for (int k = idx; k < nodePath.Count; k++) AddLeg(NavGraph.Instance.Position(nodePath[k]), k);
            AddLeg(Goal, -1);

            nodeS.Clear();
            for (int k = 0; k < nodePath.Count; k++) nodeS.Add(-1f);
            curve.Clear(); curveS.Clear();
            AddCurvePoint(ctrl[0]); MarkNode(0);
            for (int i = 1; i < ctrl.Count - 1; i++) RoundCorner(i);
            if (ctrl.Count > 1) { AddCurvePoint(ctrl[ctrl.Count - 1]); MarkNode(ctrl.Count - 1); }

            total = curveS[curveS.Count - 1]; s = 0f; seg = 0;
            doorStopS = DoorStop();
            lastDest = new Vector3(float.MaxValue, 0f, 0f);
        }

        /// One leg = straight line to `to`, or the NavMesh corners if something is in the way.
        void AddLeg(Vector3 to, int node)
        {
            Vector3 from = ctrl[ctrl.Count - 1];
            if (NavMesh.CalculatePath(from, to, NavMesh.AllAreas, legPath) && legPath.status != NavMeshPathStatus.PathInvalid)
            {
                int n = legPath.GetCornersNonAlloc(corners);
                for (int c = 1; c < n - 1; c++) AddCtrl(corners[c], -1);
            }
            AddCtrl(to, node);
        }

        void AddCtrl(Vector3 p, int node)
        {
            if ((p - ctrl[ctrl.Count - 1]).sqrMagnitude < 0.04f) { if (node >= 0) ctrlNode[ctrlNode.Count - 1] = node; return; }
            ctrl.Add(p); ctrlNode.Add(node);
        }

        /// Replaces the sharp corner at ctrl[i] with a quadratic Bezier A -> P -> B (stays inside the corner,
        /// never overshoots). Tries a smaller radius, then the sharp corner, if the arc leaves the NavMesh.
        void RoundCorner(int i)
        {
            Vector3 p = ctrl[i], da = ctrl[i - 1] - p, db = ctrl[i + 1] - p;
            float r = Mathf.Min(cornerRadius, da.magnitude * 0.5f, db.magnitude * 0.5f);
            float angle = 180f - Vector3.Angle(da, db);   // how sharply the route turns here
            if (angle > 5f && r > 0.05f)
                for (int tries = 0; tries < 2; tries++, r *= 0.4f)
                {
                    Vector3 a = p + da.normalized * r, b = p + db.normalized * r;
                    int k = Mathf.Clamp(Mathf.CeilToInt(angle * Mathf.Deg2Rad * r / 0.3f), 1, 6) * 2;
                    for (int j = 0; j <= k; j++)
                    {
                        float t = j / (float)k, u = 1f - t;
                        arc[j] = u * u * a + 2f * u * t * p + t * t * b;
                    }
                    if (!ArcClear(curve[curve.Count - 1], k)) continue;
                    for (int j = 0; j <= k; j++) { AddCurvePoint(arc[j]); if (j == k / 2) MarkNode(i); }
                    return;
                }
            AddCurvePoint(p); MarkNode(i);
        }

        bool ArcClear(Vector3 from, int k)
        {
            if (NavMesh.Raycast(from, arc[0], out _, NavMesh.AllAreas)) return false;
            for (int j = 1; j <= k; j++) if (NavMesh.Raycast(arc[j - 1], arc[j], out _, NavMesh.AllAreas)) return false;
            return true;
        }

        void AddCurvePoint(Vector3 p)
        {
            if (curve.Count == 0) { curve.Add(p); curveS.Add(0f); return; }
            float d = Vector3.Distance(curve[curve.Count - 1], p);
            if (d < 0.01f) return;
            curve.Add(p); curveS.Add(curveS[curveS.Count - 1] + d);
        }

        void MarkNode(int i) { if (ctrlNode[i] >= 0) nodeS[ctrlNode[i]] = curveS[curveS.Count - 1]; }

        /// Distance along the curve of the first node whose next edge has a closed door (the agent stops there).
        float DoorStop()
        {
            for (int k = idx; k + 1 < nodePath.Count; k++)
                if (TryGetDoor(nodePath[k], nodePath[k + 1], out var d) && !d.isOpen) return Mathf.Max(nodeS[k], 0f);
            return float.MaxValue;
        }

        /// Projects the agent onto the curve (forward only). True if it is more than 2.5 m off the route.
        bool Track()
        {
            if (curve.Count < 2) return false;
            Vector3 pos = transform.position;
            float best = float.MaxValue, bestS = s; int bestSeg = seg;
            int end = Mathf.Min(curve.Count - 1, seg + 24);
            for (int i = seg; i < end; i++)
            {
                Vector3 ab = curve[i + 1] - curve[i], ap = pos - curve[i]; ab.y = 0f; ap.y = 0f;
                float len2 = ab.sqrMagnitude;
                float t = len2 > 1e-6f ? Mathf.Clamp01(Vector3.Dot(ap, ab) / len2) : 0f;
                float d = (ap - ab * t).sqrMagnitude;
                if (d < best) { best = d; bestSeg = i; bestS = Mathf.Lerp(curveS[i], curveS[i + 1], t); }
            }
            if (bestS >= s) { s = bestS; seg = bestSeg; }
            return best > 2.5f * 2.5f;
        }

        Vector3 PointAt(float at)
        {
            for (int i = seg; i < curve.Count - 1; i++)
                if (curveS[i + 1] >= at)
                {
                    float len = curveS[i + 1] - curveS[i];
                    return Vector3.Lerp(curve[i], curve[i + 1], len > 1e-6f ? (at - curveS[i]) / len : 1f);
                }
            return curve[curve.Count - 1];
        }

        // ------------------------------------------------------------------ rotation
        void BeginTurnInPlace()
        {
            turnUntil = 0f;
            if (!smoothRotation || !turnInPlace || CurrentGait == Gait.Run || curve.Count < 2) return;
            Vector3 d = PointAt(Mathf.Min(lookAhead, total)) - transform.position; d.y = 0f;
            if (d.sqrMagnitude < 0.04f || Vector3.Angle(transform.forward, d) < turnInPlaceAngle) return;
            turnDir = d; turnUntil = Time.time + maxTurnInPlaceTime;
            nav.isStopped = true;
        }

        /// True while still turning on the spot; ends when facing the path, after maxTurnInPlaceTime, or on Run.
        bool TurnInPlace()
        {
            if (Time.time < turnUntil && CurrentGait != Gait.Run && Vector3.Angle(transform.forward, turnDir) > 10f)
            {
                Rotate(turnDir, turnRateStill);
                return true;
            }
            turnUntil = 0f; nav.isStopped = false;
            return false;
        }

        /// Face where the agent wants to go; turns slower the faster it moves (wider, natural arcs).
        void SteerRotation()
        {
            if (Time.frameCount - externalFaceFrame <= 1) return;
            Vector3 want = nav.desiredVelocity; want.y = 0f;
            if (want.sqrMagnitude < 0.04f) { want = nav.velocity; want.y = 0f; }
            if (want.sqrMagnitude < 0.04f) return;
            float k = Mathf.Clamp01(nav.velocity.magnitude / Mathf.Max(runSpeed, 0.1f));
            Rotate(want, Mathf.Lerp(turnRateStill, IsChasing ? Mathf.Max(turnRateAtRun, chaseTurnRate) : turnRateAtRun, k));
        }

        /// Full gait speed when facing the steering direction, down to minTurnSpeedFactor at 90 degrees or more.
        void ScaleSpeedForTurn()
        {
            Vector3 want = nav.desiredVelocity; want.y = 0f;
            float k = 1f;
            if (want.sqrMagnitude > 0.01f)
                k = Mathf.Clamp(Vector3.Dot(transform.forward, want.normalized),
                                IsChasing ? chaseMinTurnSpeedFactor : minTurnSpeedFactor, 1f);
            nav.speed = SpeedFor(CurrentGait) * k;
        }

        static bool TryGetDoor(int a, int b, out Door door)
        {
            foreach (var e in NavGraph.Instance.Neighbours(a))
                if (e.To == b) { door = e.Door; return door != null; }
            door = null; return false;
        }

        public void ConsumeFlags() { ArrivedFlag = FailedFlag = false; }

        void OnDrawGizmosSelected()
        {
            if (!IsMoving) return;
            Gizmos.color = Color.green;
            Vector3 prev = transform.position;
            if (NavGraph.Instance != null)
                for (int i = idx; i < nodePath.Count; i++) { var p = NavGraph.Instance.Position(nodePath[i]); Gizmos.DrawLine(prev, p); prev = p; }
            Gizmos.DrawLine(prev, Goal); Gizmos.DrawWireSphere(Goal, 0.4f);
            Gizmos.color = Color.yellow;
            for (int i = seg; i + 1 < curve.Count; i++) Gizmos.DrawLine(curve[i], curve[i + 1]);
            Gizmos.DrawWireSphere(LookAheadPoint, 0.2f);
        }
    }
}
