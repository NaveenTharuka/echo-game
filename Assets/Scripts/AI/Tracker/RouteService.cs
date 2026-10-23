using System;
using UnityEngine;

namespace Echo.AI.Tracker
{
    /// Binary min-heap of (node, key) with lazy deletion: duplicates are pushed instead of a
    /// decrease-key and stale entries are skipped on pop. Grows by doubling, so after warm-up it
    /// never allocates.
    public sealed class NodeHeap
    {
        int[] nodes; float[] keys;
        public int Count { get; private set; }

        public NodeHeap(int capacity = 64)
        {
            nodes = new int[Mathf.Max(4, capacity)];
            keys = new float[nodes.Length];
        }

        public void Clear() => Count = 0;

        public void Push(int node, float key)
        {
            if (Count == nodes.Length)
            {
                Array.Resize(ref nodes, Count * 2);
                Array.Resize(ref keys, Count * 2);
            }
            int i = Count++;
            while (i > 0)
            {
                int parent = (i - 1) >> 1;
                if (keys[parent] <= key) break;
                nodes[i] = nodes[parent]; keys[i] = keys[parent];
                i = parent;
            }
            nodes[i] = node; keys[i] = key;
        }

        public int Pop(out float key)
        {
            int top = nodes[0]; key = keys[0];
            int lastNode = nodes[--Count]; float lastKey = keys[Count];
            int i = 0;
            while (true)
            {
                int c = 2 * i + 1;
                if (c >= Count) break;
                if (c + 1 < Count && keys[c + 1] < keys[c]) c++;
                if (keys[c] >= lastKey) break;
                nodes[i] = nodes[c]; keys[i] = keys[c];
                i = c;
            }
            if (Count > 0) { nodes[i] = lastNode; keys[i] = lastKey; }
            return top;
        }
    }

    /// Single-source shortest route distances (Dijkstra) from one node to every node.
    /// One source, many targets: the scorer needs route(p_last, n) and route(me, n) for all n, which is
    /// one Dijkstra each, O(E log V), instead of one A* per candidate.
    public sealed class RouteField
    {
        public int Source { get; private set; } = -1;
        public int Version { get; private set; } = -1;
        public float[] Dist { get; private set; } = Array.Empty<float>();
        readonly NodeHeap heap = new();

        /// Point the field was seeded from (ComputeFromPoint), for its cache key.
        public Vector3 Point { get; private set; }

        public void Compute(ITrackerWorld w, int source, float doorSpeed)
        {
            if (!Reset(w, source)) return;
            Dist[source] = 0f;
            heap.Push(source, 0f);
            Relax(w, doorSpeed);
        }

        /// Route distances from a POINT p whose nearest node is `node`: seeded at that node and at each of its
        /// neighbours with the straight-line distance from p (plus the door cost of that edge). The node-source
        /// field + |p - node| over-counts by twice the along-edge offset whenever p is already past its node
        /// towards the target (15 Oct: ETA_player -0.7 s mean in the replay, -1.8 s with the lagging fitted anchor).
        public void ComputeFromPoint(ITrackerWorld w, Vector3 p, int node, float doorSpeed)
        {
            Point = p;
            if (!Reset(w, node)) return;
            Vector3 np = w.Position(node);
            Dist[node] = Vector3.Distance(p, np);
            heap.Push(node, Dist[node]);
            int k = w.NeighbourCount(node);
            for (int j = 0; j < k; j++)
            {
                var e = w.Neighbour(node, j);
                float extra = TrackerCosts.EdgeCost(e, doorSpeed) - e.Length;       // 0 open, delay closed, inf locked
                float d = Vector3.Distance(p, w.Position(e.To)) + extra;
                if (d < Dist[e.To]) { Dist[e.To] = d; heap.Push(e.To, d); }
            }
            Relax(w, doorSpeed);
        }

        bool Reset(ITrackerWorld w, int source)
        {
            int n = w.Count;
            if (Dist.Length != n) Dist = new float[n];
            for (int i = 0; i < n; i++) Dist[i] = float.PositiveInfinity;
            Source = source; Version = w.Version;
            heap.Clear();
            return source >= 0 && source < n;
        }

        void Relax(ITrackerWorld w, float doorSpeed)
        {
            while (heap.Count > 0)
            {
                int cur = heap.Pop(out float d);
                if (d > Dist[cur]) continue;                       // stale entry
                int k = w.NeighbourCount(cur);
                for (int j = 0; j < k; j++)
                {
                    var e = w.Neighbour(cur, j);
                    float nd = d + TrackerCosts.EdgeCost(e, doorSpeed);
                    if (nd < Dist[e.To]) { Dist[e.To] = nd; heap.Push(e.To, nd); }
                }
            }
        }
    }

    /// Route distances between positions, with a small cache of Dijkstra fields keyed on
    /// (source node, world version). The graph only changes when a door does, so a field is computed
    /// at most once per source per door change - in practice twice per think tick (p_last and me).
    public sealed class RouteService
    {
        const int Slots = 4;
        readonly ITrackerWorld world;
        readonly TrackerSettings settings;
        readonly RouteField[] fields = new RouteField[Slots];
        readonly int[] lastUse = new int[Slots];
        int clock;

        /// Dijkstra runs since construction (for the cost report).
        public int Computations { get; private set; }

        public RouteService(ITrackerWorld world, TrackerSettings settings)
        {
            this.world = world; this.settings = settings;
            for (int i = 0; i < Slots; i++) fields[i] = new RouteField();
        }

        public ITrackerWorld World => world;

        readonly RouteField pointField = new();

        /// Route distances from point p (nearest node `node`) to every node; recomputed only when p, the node or
        /// the world version changes (once per new sound, not per tick).
        public RouteField FieldFrom(Vector3 p, int node)
        {
            if (pointField.Source != node || pointField.Version != world.Version || pointField.Point != p ||
                pointField.Dist.Length != world.Count)
            {
                pointField.ComputeFromPoint(world, p, node, settings.maxPlayerSpeed);
                Computations++;
            }
            return pointField;
        }

        public RouteField Field(int source)
        {
            clock++;
            int lru = 0;
            for (int i = 0; i < Slots; i++)
            {
                var f = fields[i];
                if (f.Source == source && f.Version == world.Version && f.Dist.Length == world.Count)
                { lastUse[i] = clock; return f; }
                if (lastUse[i] < lastUse[lru]) lru = i;
            }
            fields[lru].Compute(world, source, settings.maxPlayerSpeed);
            lastUse[lru] = clock;
            Computations++;
            return fields[lru];
        }

        /// Route distance between two positions whose nearest nodes are known:
        /// |a - node(a)| + Dijkstra(node(a), node(b)) + |b - node(b)|.
        public float Route(Vector3 a, int nodeA, Vector3 b, int nodeB)
        {
            if (nodeA < 0 || nodeB < 0) return float.PositiveInfinity;
            float d = Field(nodeA).Dist[nodeB];
            if (float.IsPositiveInfinity(d)) return d;
            return Vector3.Distance(a, world.Position(nodeA)) + d + Vector3.Distance(b, world.Position(nodeB));
        }
    }
}
