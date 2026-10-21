using System.Collections.Generic;
using UnityEngine;

namespace Echo.AI.Tracker
{
    /// The Sandbox_TrackerGrid level as data: a 9 x 9 grid of 6 m cells. Shared by the scene builder and the
    /// weight sweep, so the sweep runs on the same topology the agent is tested in.
    public static class SandboxTrackerLayout
    {
        public const float Cell = 6f;

        // Row strings from z = 8 (top) to z = 0; '.' open, '#' solid.
        public static readonly string[] Map =
        {
            "###...###",   // 8  north room (Tracker home)
            "###...###",   // 7
            "####.####",   // 6  north corridor
            "..##.##..",   // 5  west room / east room
            ".........",   // 4  main corridor through the junction (4,4)
            "..##.##..",   // 3
            "####.###.",   // 2  south arm (door between z 3 and 2) / loop
            "###...##.",   // 1  south room / loop
            "###......",   // 0  south room + loop corridor
        };
        public static readonly Vector2Int DoorA = new(4, 3), DoorB = new(4, 2);
        public static readonly Vector2Int SpawnCell = new(0, 4), HomeCell = new(4, 7);

        public static int W => Map[0].Length;
        public static int H => Map.Length;
        public static bool Open(int x, int z) => x >= 0 && z >= 0 && x < W && z < H && Map[H - 1 - z][x] == '.';
        public static Vector3 Centre(int x, int z) => new((x + 0.5f) * Cell, 0f, (z + 0.5f) * Cell);
        public static bool IsDoorLink(Vector2Int a, Vector2Int b) => (a == DoorA && b == DoorB) || (a == DoorB && b == DoorA);

        /// One node per open cell, 4-neighbour links. `onLink(a, b, isDoor)` is called for each link in order.
        public static Dictionary<Vector2Int, int> Nodes(System.Action<Vector3> addNode, System.Action<int, int, bool> onLink)
        {
            var index = new Dictionary<Vector2Int, int>();
            for (int z = 0; z < H; z++)
            for (int x = 0; x < W; x++)
                if (Open(x, z)) { index[new Vector2Int(x, z)] = index.Count; addNode(Centre(x, z)); }
            foreach (var kv in index)
            {
                foreach (var step in new[] { Vector2Int.right, Vector2Int.up })
                    if (index.TryGetValue(kv.Key + step, out int other))
                        onLink(kv.Value, other, IsDoorLink(kv.Key, kv.Key + step));
            }
            return index;
        }

        /// The layout as a plain TrackerGraph with the door open (no Door component outside the scene).
        public static TrackerGraph BuildGraph(out int door)
        {
            var g = new TrackerGraph();
            int d = g.AddDoor(DoorState.Open, 0.6f);
            Nodes(p => g.AddNode(p), (a, b, isDoor) => g.Connect(a, b, isDoor ? d : -1));
            door = d;
            return g;
        }
    }
}
