using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

namespace Echo.Navigation
{
    /// The planning graph (guide §7.2): child NavNodes, edges with doors, and the all-pairs sound-distance
    /// matrix used by Acoustics. Built in Start so NavMeshSurfaces (OnEnable) are loaded before edge lengths
    /// are measured on the NavMesh.
    public class NavGraph : MonoBehaviour, IGraph
    {
        public static NavGraph Instance { get; private set; }
        public const float ClosedDoorSoundPenalty = 12f;

        public NavNode[] Nodes { get; private set; } = new NavNode[0];
        List<Edge>[] adj = new List<Edge>[0];
        float[,] soundDist = new float[0, 0];
        readonly Dictionary<NavNode, int> index = new();

        public int Count => Nodes.Length;
        /// Bumped on every Build and door change, so cached routes (e.g. the Tracker's) know to refresh.
        public int Version { get; private set; }
        public Vector3 Position(int i) => Nodes[i].transform.position;
        public IReadOnlyList<Edge> Neighbours(int i) => adj[i];
        public float SoundDistance(int a, int b) => soundDist[a, b];
        public NavNode Node(int i) => Nodes[i];
        public int IndexOf(NavNode n) => index.TryGetValue(n, out var i) ? i : -1;

        void Awake() => Instance = this;
        void Start() => Build();
        void OnDestroy() { if (Instance == this) Instance = null; }

        public void Build()
        {
            Nodes = GetComponentsInChildren<NavNode>();
            index.Clear();
            for (int i = 0; i < Nodes.Length; i++) index[Nodes[i]] = i;

            var doors = FindObjectsByType<Door>(FindObjectsSortMode.None);
            adj = new List<Edge>[Nodes.Length];
            for (int i = 0; i < Nodes.Length; i++)
            {
                adj[i] = new List<Edge>();
                foreach (var other in Nodes[i].links)
                {
                    if (other == null || !index.TryGetValue(other, out int j)) continue;
                    Door door = null;
                    foreach (var d in doors)
                        if ((d.sideA == Nodes[i] && d.sideB == other) || (d.sideB == Nodes[i] && d.sideA == other))
                            door = d;
                    adj[i].Add(new Edge(j, PathLength(Position(i), Position(j)), door));
                }
            }
            RebuildSoundDistances();
            Version++;
        }

        public void OnDoorChanged() { RebuildSoundDistances(); Version++; }

        /// Floyd-Warshall, O(V^3): ~0.5 M operations for 80 nodes, run at load and on door changes.
        void RebuildSoundDistances()
        {
            int n = Nodes.Length;
            soundDist = new float[n, n];
            for (int i = 0; i < n; i++)
                for (int j = 0; j < n; j++) soundDist[i, j] = i == j ? 0f : float.PositiveInfinity;
            for (int i = 0; i < n; i++)
                foreach (var e in adj[i])
                {
                    float w = e.Length + (e.Door != null && !e.Door.isOpen ? ClosedDoorSoundPenalty : 0f);
                    if (w < soundDist[i, e.To]) soundDist[i, e.To] = w;
                }
            for (int k = 0; k < n; k++)
                for (int i = 0; i < n; i++)
                {
                    float ik = soundDist[i, k];
                    if (float.IsPositiveInfinity(ik)) continue;
                    for (int j = 0; j < n; j++)
                    {
                        float via = ik + soundDist[k, j];
                        if (via < soundDist[i, j]) soundDist[i, j] = via;
                    }
                }
        }

        /// Nearest node, preferring one with a clear line (so we don't pick a node behind a wall). -1 if empty.
        public int Nearest(Vector3 p)
        {
            int best = -1, bestBlocked = -1; float bd = float.MaxValue, bbd = float.MaxValue;
            int mask = LayerMask.GetMask("Environment");
            for (int i = 0; i < Nodes.Length; i++)
            {
                float d = (Position(i) - p).sqrMagnitude;
                if (d >= bd && d >= bbd) continue;
                bool clear = !Physics.Linecast(p + Vector3.up * 0.5f, Position(i) + Vector3.up * 0.5f,
                                               mask, QueryTriggerInteraction.Ignore);
                if (clear && d < bd) { bd = d; best = i; }
                else if (!clear && d < bbd) { bbd = d; bestBlocked = i; }
            }
            return best >= 0 ? best : bestBlocked;
        }

        static float PathLength(Vector3 a, Vector3 b)
        {
            var path = new NavMeshPath();
            if (NavMesh.SamplePosition(a, out var ha, 1.5f, NavMesh.AllAreas) &&
                NavMesh.SamplePosition(b, out var hb, 1.5f, NavMesh.AllAreas) &&
                NavMesh.CalculatePath(ha.position, hb.position, NavMesh.AllAreas, path) &&
                path.status == NavMeshPathStatus.PathComplete)
            {
                float len = 0f;
                for (int i = 1; i < path.corners.Length; i++) len += Vector3.Distance(path.corners[i - 1], path.corners[i]);
                return Mathf.Max(len, Vector3.Distance(a, b));   // never below straight line: keeps A*'s heuristic admissible
            }
            return Vector3.Distance(a, b);
        }
    }
}
