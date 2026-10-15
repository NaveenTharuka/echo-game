using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

namespace Echo.AI
{
    public static class SearchPattern
    {
        /// Reachable points on a ring around `centre`, ordered so the agent sweeps around it (guide §8.6).
        public static List<Vector3> Ring(Vector3 centre, float radius, int count)
        {
            var pts = new List<Vector3>();
            float start = Random.value * 360f;
            for (int i = 0; i < count; i++)
            {
                float a = (start + i * 360f / count) * Mathf.Deg2Rad;
                Vector3 p = centre + new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)) * radius * Random.Range(0.6f, 1f);
                if (NavMesh.SamplePosition(p, out var hit, 2f, NavMesh.AllAreas) &&
                    !NavMesh.Raycast(centre, hit.position, out _, NavMesh.AllAreas))   // same room: no wall between
                    pts.Add(hit.position);
            }
            if (pts.Count == 0) pts.Add(centre);
            return pts;
        }

        /// `p` snapped to the NavMesh, if it can be reached from `centre` without passing a wall.
        public static bool TryReachable(Vector3 centre, Vector3 p, out Vector3 result)
        {
            result = p;
            if (!NavMesh.SamplePosition(p, out var hit, 2f, NavMesh.AllAreas)) return false;
            if (NavMesh.Raycast(centre, hit.position, out var block, NavMesh.AllAreas))
                result = block.position;                      // stop at the wall: still the right direction
            else result = hit.position;
            return (result - centre).sqrMagnitude > 4f;       // at least 2 m ahead, or it is pointless
        }

        /// The ways out a fleeing player would take: Doorway nodes of the NavGraph near `centre`, the ones farthest
        /// from the agent first (it came from its own side, so the prey more likely left by the other exits).
        public static List<Vector3> Exits(Vector3 centre, Vector3 agent, float radius, int max)
        {
            var exits = new List<Vector3>();
            var g = Echo.Navigation.NavGraph.Instance;
            if (g == null) return exits;
            for (int i = 0; i < g.Count; i++)
            {
                if ((g.Node(i).tags & Echo.Navigation.NodeTag.Doorway) == 0) continue;
                Vector3 p = g.Position(i);
                if (Vector3.Distance(p, centre) <= radius) exits.Add(p);
            }
            exits.Sort((a, b) => Vector3.Distance(b, agent).CompareTo(Vector3.Distance(a, agent)));
            if (exits.Count > max) exits.RemoveRange(max, exits.Count - max);
            return exits;
        }
    }
}
