using System.Collections.Generic;
using Echo.Navigation;
using UnityEngine;
using UnityEngine.AI;

namespace Echo.AI
{
    /// Movement for every agent (guide §8.2): plans a node route with A* over the NavGraph, walks it with the
    /// NavMeshAgent, opens closed doors on the way and reports Arrived / Failed (no path or stuck).
    /// Without a NavGraph in the scene it falls back to plain NavMesh walking, so greybox tests still work.
    [RequireComponent(typeof(NavMeshAgent))]
    public class AgentMotor : MonoBehaviour
    {
        public enum Gait { Sneak, Walk, Run }
        public float sneakSpeed = 1.2f, walkSpeed = 2.2f, runSpeed = 5f;
        public float waypointRadius = 0.8f;
        public float stuckTimeout = 2.5f;

        /// Set by the brain to express this agent's preferences (see each agent).
        public AStar.CostModifier CostModifier;

        public bool IsMoving { get; private set; }
        public Vector3 Goal { get; private set; }
        public Gait CurrentGait { get; private set; }
        public IReadOnlyList<int> NodePath => nodePath;
        public bool ArrivedFlag { get; private set; }      // consumed by brains on their tick
        public bool FailedFlag { get; private set; }

        NavMeshAgent nav;
        readonly List<int> nodePath = new();
        int idx; float lastProgressTime, bestRemaining, doorWaitUntil;
        Vector3 lookTarget; bool hasLook;

        void Awake() { nav = GetComponent<NavMeshAgent>(); }

        public float SpeedFor(Gait g) => g == Gait.Run ? runSpeed : g == Gait.Walk ? walkSpeed : sneakSpeed;

        public bool MoveTo(Vector3 destination, Gait gait)
        {
            ArrivedFlag = FailedFlag = false; hasLook = false;
            if (!nav.isOnNavMesh || !NavMesh.SamplePosition(destination, out var hit, 3f, NavMesh.AllAreas))
            { IsMoving = false; FailedFlag = true; return false; }
            Goal = hit.position; CurrentGait = gait; nav.speed = SpeedFor(gait);

            nodePath.Clear();
            var g = NavGraph.Instance;
            if (g != null && g.Count > 0)
            {
                int s = g.Nearest(transform.position), t = g.Nearest(Goal);
                if (s != t && !AStar.FindPath(g, s, t, nodePath, CostModifier))
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
            nav.isStopped = false;
            nav.SetDestination(CurrentTarget());
            return true;
        }

        public void SetGait(Gait gait) { CurrentGait = gait; nav.speed = SpeedFor(gait); }

        public void Stop()
        {
            IsMoving = false; nodePath.Clear(); hasLook = false;
            if (nav != null && nav.isOnNavMesh) { nav.isStopped = true; nav.ResetPath(); }
        }

        /// Teleports the agent (respawn / reset). Use this, never transform.position, while a NavMeshAgent is on.
        public void Warp(Vector3 position)
        {
            Stop();
            nav.Warp(position);
            ArrivedFlag = FailedFlag = false;
        }

        /// Keep turning towards `p` every frame while standing still (brains think at 5 Hz, too slow to turn).
        /// Cleared by the next MoveTo / Stop.
        public void LookAt(Vector3 p) { lookTarget = p; hasLook = true; }

        public void FaceTowards(Vector3 p, float degPerSec = 240f)
        {
            Vector3 d = p - transform.position; d.y = 0f;
            if (d.sqrMagnitude < 0.01f) return;
            transform.rotation = Quaternion.RotateTowards(transform.rotation,
                                    Quaternion.LookRotation(d), degPerSec * Time.deltaTime);
        }

        Vector3 CurrentTarget() => idx < nodePath.Count ? NavGraph.Instance.Position(nodePath[idx]) : Goal;
        void ResetProgress() { lastProgressTime = Time.time; bestRemaining = float.MaxValue; }

        void Update()
        {
            if (!IsMoving) { if (hasLook) FaceTowards(lookTarget); return; }
            if (nav.pathPending) return;
            if (Time.time < doorWaitUntil) { ResetProgress(); return; }

            bool last = idx >= nodePath.Count;
            if (nav.remainingDistance <= (last ? nav.stoppingDistance + 0.15f : waypointRadius))
            {
                if (last) { IsMoving = false; ArrivedFlag = true; return; }

                // About to traverse edge nodePath[idx] -> nodePath[idx+1]: open a closed door first.
                if (idx + 1 < nodePath.Count && TryGetDoor(nodePath[idx], nodePath[idx + 1], out var door) && !door.isOpen)
                {
                    if (door.isLocked) { Stop(); FailedFlag = true; return; }
                    door.SetOpen(true, gameObject, byAgent: true);   // makes a door sound the player hears
                    doorWaitUntil = Time.time + door.OpenDelay;
                }
                idx++; ResetProgress();
                nav.SetDestination(CurrentTarget());
                return;
            }

            if (nav.remainingDistance < bestRemaining - 0.2f) { bestRemaining = nav.remainingDistance; lastProgressTime = Time.time; }
            else if (Time.time - lastProgressTime > stuckTimeout) { Stop(); FailedFlag = true; }
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
        }
    }
}
