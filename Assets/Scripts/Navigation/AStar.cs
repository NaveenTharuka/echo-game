using System.Collections.Generic;
using UnityEngine;

namespace Echo.Navigation
{
    /// A* over the waypoint graph (guide §7.5). f = g + h, h = straight-line distance to the goal.
    public static class AStar
    {
        /// Per-agent cost shaping. MUST return >= baseCost (multipliers >= 1, additions >= 0)
        /// so the Euclidean heuristic stays admissible. Return +infinity to forbid an edge.
        public delegate float CostModifier(int from, int to, float baseCost);

        public static bool FindPath(IGraph g, int start, int goal, List<int> result,
                                    CostModifier modify = null)
        {
            result.Clear();
            if (start < 0 || goal < 0) return false;
            int n = g.Count;
            var gScore = new float[n];
            var parent = new int[n];
            var closed = new bool[n];
            for (int i = 0; i < n; i++) { gScore[i] = float.PositiveInfinity; parent[i] = -1; }

            Vector3 goalPos = g.Position(goal);
            var open = new MinHeap();
            gScore[start] = 0f;
            open.Push(start, Vector3.Distance(g.Position(start), goalPos));

            while (open.Count > 0)
            {
                int cur = open.Pop();
                if (closed[cur]) continue;                       // stale entry (lazy deletion)
                if (cur == goal)
                {
                    for (int c = goal; c != -1; c = parent[c]) result.Add(c);
                    result.Reverse();
                    return true;
                }
                closed[cur] = true;

                var edges = g.Neighbours(cur);
                for (int k = 0; k < edges.Count; k++)
                {
                    var e = edges[k];
                    if (closed[e.To]) continue;
                    float cost = e.Cost;
                    if (modify != null) cost = modify(cur, e.To, cost);
                    if (float.IsPositiveInfinity(cost)) continue;

                    float tentative = gScore[cur] + cost;
                    if (tentative < gScore[e.To])
                    {
                        gScore[e.To] = tentative;
                        parent[e.To] = cur;
                        open.Push(e.To, tentative + Vector3.Distance(g.Position(e.To), goalPos));
                    }
                }
            }
            return false;   // goal unreachable (e.g. locked doors)
        }
    }
}
