using System.Collections.Generic;
using Echo.Navigation;
using UnityEngine;
using UnityEngine.AI;

namespace Echo.AI.Tracker
{
    /// Stub mover until M4's AgentMotor exists: plans a node route on the waypoint graph (door-aware costs,
    /// locked = impassable), then lets the NavMeshAgent steer node to node. Closed doors on the route are
    /// opened by the agent (Door.SetOpen(..., byAgent: true)) and waited for. Doors do not carve the NavMesh
    /// (guide §3.2.6), so the graph is what keeps the agent out of locked doorways.
    ///
    /// Route = shortest path read off a Dijkstra field from the goal (follow the neighbour that minimises
    /// edge cost + remaining distance). Same costs and result as A* with an admissible heuristic; replaced by
    /// M4's AStar.FindPath + the Tracker's CostModifier at integration.
    [RequireComponent(typeof(NavMeshAgent))]
    public class NavMeshTrackerMover : MonoBehaviour, ITrackerMover
    {
        public SandboxTrackerGraph graph;
        public float walkSpeed = 2.2f, runSpeed = 5f;
        public float waypointRadius = 1f;
        public float arriveRadius = 1f;
        public float stuckTimeout = 2.5f;
        public float doorExtraWait = 0.3f;

        NavMeshAgent nav;
        RouteService routes;
        readonly TrackerSettings routeSettings = new();
        readonly List<int> path = new(64);
        int idx;
        Vector3 target;
        bool moving, arrived, failed;
        Door waitingDoor;
        float doorWaitUntil, bestDistance, lastProgress;

        public bool IsMoving => moving;
        public IReadOnlyList<int> Path => path;
        public int PathIndex => idx;
        public Vector3 Target => target;

        void Awake()
        {
            nav = GetComponent<NavMeshAgent>();
            nav.autoBraking = true;
        }

        void Start() { if (graph != null) routes = new RouteService(graph.Graph, routeSettings); }

        public void ConsumeEvents(out bool arrivedEvent, out bool failedEvent)
        {
            arrivedEvent = arrived; failedEvent = failed;
            arrived = failed = false;
        }

        public bool MoveTo(Vector3 destination, TrackerGait gait)
        {
            arrived = failed = false;
            nav.speed = gait == TrackerGait.Run ? runSpeed : walkSpeed;
            if (routes == null || !nav.isOnNavMesh || !NavMesh.SamplePosition(destination, out var hit, 3f, NavMesh.AllAreas))
                return Fail();
            target = hit.position;

            var g = graph.Graph;
            int s = g.Nearest(transform.position), t = g.Nearest(target);
            if (!BuildPath(g, s, t)) return Fail();

            // Skip the first node if it lies behind us.
            if (path.Count > 1 &&
                Vector3.Distance(transform.position, g.Position(path[1])) < Vector3.Distance(g.Position(path[0]), g.Position(path[1])))
                path.RemoveAt(0);

            idx = 0; moving = true; waitingDoor = null;
            nav.isStopped = false;
            nav.SetDestination(CurrentWaypoint());
            ResetProgress();
            return true;
        }

        public void Stop()
        {
            moving = false; waitingDoor = null;
            if (nav.isOnNavMesh) { nav.isStopped = true; nav.ResetPath(); }
        }

        bool Fail()
        {
            failed = true;
            Stop();
            return false;
        }

        bool BuildPath(TrackerGraph g, int s, int t)
        {
            path.Clear();
            if (s < 0 || t < 0) return false;
            var dist = routes.Field(t).Dist;
            if (float.IsPositiveInfinity(dist[s])) return false;
            int cur = s;
            path.Add(cur);
            while (cur != t)
            {
                int next = -1; float best = float.PositiveInfinity;
                for (int k = 0; k < g.NeighbourCount(cur); k++)
                {
                    var e = g.Neighbour(cur, k);
                    float c = TrackerCosts.EdgeCost(e, routeSettings.maxPlayerSpeed) + dist[e.To];
                    if (c < best) { best = c; next = e.To; }
                }
                if (next < 0 || path.Count > g.Count) return false;
                path.Add(next);
                cur = next;
            }
            return true;
        }

        Vector3 CurrentWaypoint() => idx < path.Count ? graph.Graph.Position(path[idx]) : target;

        void ResetProgress()
        {
            bestDistance = float.PositiveInfinity;
            lastProgress = Time.time;
        }

        void Update()
        {
            if (!moving) return;

            if (waitingDoor != null)
            {
                if (waitingDoor.isLocked) { Fail(); return; }
                if (waitingDoor.OpenAmount < 0.95f && Time.time < doorWaitUntil) return;
                waitingDoor = null;
                nav.isStopped = false;
                nav.SetDestination(CurrentWaypoint());
                ResetProgress();
                return;
            }

            Vector3 wp = CurrentWaypoint();
            Vector3 d = wp - transform.position; d.y = 0f;
            float dist = d.magnitude;
            bool final = idx >= path.Count;

            if (dist <= (final ? arriveRadius : waypointRadius))
            {
                if (final)
                {
                    moving = false; arrived = true;
                    nav.isStopped = true; nav.ResetPath();
                    return;
                }
                // Leaving node idx: open a closed door on the next link first.
                if (idx + 1 < path.Count)
                {
                    var door = graph.DoorBetween(path[idx], path[idx + 1]);
                    if (door != null && !door.isOpen)
                    {
                        if (door.isLocked) { Fail(); return; }
                        door.SetOpen(true, gameObject, true);
                        waitingDoor = door;
                        doorWaitUntil = Time.time + door.OpenDelay + doorExtraWait;
                        nav.isStopped = true;
                        idx++;
                        return;
                    }
                }
                idx++;
                nav.SetDestination(CurrentWaypoint());
                ResetProgress();
                return;
            }

            // Stuck: no 0.2 m of progress towards the current waypoint for stuckTimeout seconds.
            if (dist < bestDistance - 0.2f) { bestDistance = dist; lastProgress = Time.time; }
            else if (Time.time - lastProgress > stuckTimeout) Fail();
        }

        void OnDrawGizmosSelected()
        {
            if (!moving || graph == null || !Application.isPlaying) return;
            Gizmos.color = Color.white;
            Vector3 prev = transform.position;
            for (int i = idx; i < path.Count; i++) { var p = graph.Graph.Position(path[i]); Gizmos.DrawLine(prev, p); prev = p; }
            Gizmos.DrawLine(prev, target);
        }
    }
}
