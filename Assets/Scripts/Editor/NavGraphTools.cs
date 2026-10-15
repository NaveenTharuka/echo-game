using System.Collections.Generic;
using System.Linq;
using Echo.Navigation;
using UnityEditor;
using UnityEngine;
using UnityEngine.AI;

namespace Echo.EditorTools
{
    /// ECHO > Nav Graph > Auto-Link Nodes (guide §7.4). Links every pair of nodes that has a nearly straight NavMesh
    /// route, assigns zones from ZoneVolumes, and reports connectivity. Needs a baked NavMesh.
    public static class NavGraphTools
    {
        const float MaxLinkDistance = 14f;
        const float MaxDetour = 1.25f;   // link only if the walk is nearly straight

        [MenuItem("ECHO/Nav Graph/Auto-Link Nodes")]
        static void AutoLinkMenu()
        {
            var graph = Object.FindFirstObjectByType<NavGraph>();
            if (graph == null) { Debug.LogError("[NavGraphTools] No NavGraph in scene"); return; }
            AutoLink(graph);
        }

        /// Returns the number of nodes reachable from the first one (== node count when the graph is connected).
        public static int AutoLink(NavGraph graph)
        {
            var nodes = graph.GetComponentsInChildren<NavNode>();
            if (nodes.Length == 0) { Debug.LogError("[NavGraphTools] NavGraph has no NavNode children"); return 0; }
            var zones = Object.FindObjectsByType<ZoneVolume>(FindObjectsSortMode.None);

            foreach (var n in nodes) { Undo.RecordObject(n, "Auto-link"); n.links.Clear(); }

            for (int i = 0; i < nodes.Length; i++)
            for (int j = i + 1; j < nodes.Length; j++)
            {
                Vector3 a = nodes[i].transform.position, b = nodes[j].transform.position;
                float straight = Vector3.Distance(a, b);
                if (straight > MaxLinkDistance) continue;
                if (!NavMesh.SamplePosition(a, out var ha, 1.5f, NavMesh.AllAreas) ||
                    !NavMesh.SamplePosition(b, out var hb, 1.5f, NavMesh.AllAreas)) continue;
                var path = new NavMeshPath();
                if (!NavMesh.CalculatePath(ha.position, hb.position, NavMesh.AllAreas, path) ||
                    path.status != NavMeshPathStatus.PathComplete) continue;
                float len = 0f;
                for (int k = 1; k < path.corners.Length; k++) len += Vector3.Distance(path.corners[k - 1], path.corners[k]);
                if (len > straight * MaxDetour) continue;   // goes round a wall: let intermediate nodes handle it
                nodes[i].links.Add(nodes[j]); nodes[j].links.Add(nodes[i]);
            }

            foreach (var n in nodes)
            {
                var z = zones.FirstOrDefault(v => v.GetComponent<BoxCollider>().bounds.Contains(n.transform.position));
                n.zone = z ? z.zoneId : "Unzoned";
                EditorUtility.SetDirty(n);
            }

            // Connectivity report (BFS from node 0)
            var seen = new HashSet<NavNode> { nodes[0] };
            var queue = new Queue<NavNode>(); queue.Enqueue(nodes[0]);
            while (queue.Count > 0) foreach (var l in queue.Dequeue().links) if (seen.Add(l)) queue.Enqueue(l);
            int isolated = nodes.Count(n => n.links.Count == 0);
            Debug.Log($"[NavGraphTools] Auto-link: {nodes.Length} nodes, {nodes.Sum(n => n.links.Count) / 2} edges, " +
                      $"reachable from first node: {seen.Count}/{nodes.Length}, isolated: {isolated}");
            return seen.Count;
        }
    }
}
