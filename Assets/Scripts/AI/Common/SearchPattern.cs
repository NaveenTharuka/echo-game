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
    }
}
