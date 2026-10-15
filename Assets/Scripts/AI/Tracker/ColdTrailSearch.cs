using System;
using UnityEngine;

namespace Echo.AI.Tracker
{
    /// Greedy best-first search for a player who has gone quiet. Starts at node(p_last); the frontier
    /// is a heap ordered by
    ///   h(n) = dir + searchHeatWeight * heat - route(p_last, n) / reach(t),   reach(t) = v_max * (now - t_last)
    /// i.e. "in the direction they were going, where they have been before, and not further than they
    /// could have walked". Each step visits the best frontier node, then pushes its unvisited
    /// neighbours (never through a locked door). Stops after searchMaxNodes visits or searchMaxTime.
    /// Unlike BFS/DFS the expansion order is decided by evidence, not by graph shape; unlike A* there
    /// is no known goal - the search chooses *which* goal is worth checking.
    public sealed class ColdTrailSearch
    {
        readonly NodeHeap frontier = new();
        bool[] queued = Array.Empty<bool>(), visited = Array.Empty<bool>();
        float[] priority = Array.Empty<float>();

        ITrackerWorld world; RouteService routes; HeatMap heat; TrackerSettings s;
        TrackerObservation last; Vector3 dir; float dirTrust, startTime;

        public bool Active { get; private set; }
        /// The time budget runs from the first arrival, not from Begin: walking a long detour to where the
        /// trail went cold is not searching.
        public bool ClockStarted { get; private set; }
        public float StartTime => startTime;
        public int Visits { get; private set; }
        public int FrontierCount => frontier.Count;
        public bool IsQueued(int n) => queued[n];
        public bool IsVisited(int n) => visited[n];
        public float Priority(int n) => priority[n];

        public void Begin(RouteService routes, in TrackerObservation last, Vector3 velocity, HeatMap heat,
                          float now, TrackerSettings settings, bool startClock = true)
        {
            this.routes = routes; world = routes.World; this.heat = heat; s = settings; this.last = last;
            int n = world.Count;
            if (queued.Length != n) { queued = new bool[n]; visited = new bool[n]; priority = new float[n]; }
            else { Array.Clear(queued, 0, n); Array.Clear(visited, 0, n); }
            frontier.Clear();
            Visits = 0; Active = true;
            ClockStarted = startClock; startTime = startClock ? now : float.PositiveInfinity;
            dir = velocity.sqrMagnitude > 0.01f ? new Vector3(velocity.x, 0f, velocity.z).normalized : Vector3.zero;
            dirTrust = Mathf.Clamp01(new Vector3(velocity.x, 0f, velocity.z).magnitude / Mathf.Max(0.01f, s.dirSpeedRef));
            if (last.Node >= 0) Enqueue(last.Node, now);
        }

        public void Stop() { Active = false; frontier.Clear(); }

        public void StartClock(float now)
        {
            if (ClockStarted) return;
            ClockStarted = true; startTime = now;
        }

        public float H(int node, float now)
        {
            float reach = Mathf.Max(1f, s.maxPlayerSpeed * (now - last.Time));
            Vector3 off = world.Position(node) - last.Position; off.y = 0f;
            float len = off.magnitude;
            float d = dir != Vector3.zero && len > 0.5f ? dirTrust * Mathf.Max(0f, Vector3.Dot(dir, off / len)) : 0f;
            float route = routes.Route(last.Position, last.Node, world.Position(node), node);
            return d + s.searchHeatWeight * heat.Normalised(node) - route / reach;
        }

        void Enqueue(int node, float now)
        {
            queued[node] = true;
            priority[node] = H(node, now);
            frontier.Push(node, -priority[node]);           // min-heap on -h = max-heap on h
        }

        /// Next node to check, or -1 when the search is over (budget spent or frontier empty).
        public int Next(float now)
        {
            if (!Active) return -1;
            if (Visits >= s.searchMaxNodes || (ClockStarted && now - startTime > s.searchMaxTime) || frontier.Count == 0)
            { Stop(); return -1; }

            int node = frontier.Pop(out _);
            visited[node] = true;
            Visits++;
            int k = world.NeighbourCount(node);
            for (int j = 0; j < k; j++)
            {
                var e = world.Neighbour(node, j);
                if (e.Door == DoorState.Locked || queued[e.To]) continue;
                Enqueue(e.To, now);
            }
            return node;
        }
    }
}
