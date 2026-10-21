using Echo.Navigation;
using UnityEngine;
using UnityEngine.AI;

namespace Echo.AI.Tracker
{
    /// The level as the Tracker core sees it: an ITrackerWorld view over the shared NavGraph (nodes, measured edge
    /// lengths, door state from Door). Version follows NavGraph.Version, which Door bumps on open/close and on
    /// lock/unlock, so the core's cached route fields refresh exactly when a route can change.
    /// Every member is safe while the graph is missing or not built yet (Count 0, Nearest -1).
    /// Walls come from the baked NavMesh (ITrackerWalkable): the predicted player position stops where a straight walk
    /// leaves the mesh, so it can no longer land past the warehouse's north wall (SampleScene, 21 Oct: z 103-111 vs ~94).
    public sealed class TrackerNavGraphWorld : ITrackerWorld, ITrackerWalkable
    {
        /// How far from the NavMesh the trail point may lie and still be snapped onto it (Hearing snaps within 3 m).
        const float SnapRadius = 3f;

        static NavGraph Graph => NavGraph.Instance;

        public bool Ready { get { var g = Graph; return g != null && g.Count > 0; } }
        public int Count => Ready ? Graph.Count : 0;
        public int Version { get { var g = Graph; return g != null ? g.Version : 0; } }
        public Vector3 Position(int node) => Graph.Position(node);
        public int NeighbourCount(int node) => Graph.Neighbours(node).Count;
        public int Nearest(Vector3 p) => Ready ? Graph.Nearest(p) : -1;

        public Vector3 ClampWalk(Vector3 from, Vector3 to)
        {
            if (!NavMesh.SamplePosition(from, out var start, SnapRadius, NavMesh.AllAreas)) return to;   // unknown: no clamp
            Vector3 end = new(to.x, start.position.y, to.z);
            if (!NavMesh.Raycast(start.position, end, out var hit, NavMesh.AllAreas)) return to;           // clear walk
            return new Vector3(hit.position.x, to.y, hit.position.z);
        }

        public TrackerEdge Neighbour(int node, int k)
        {
            var e = Graph.Neighbours(node)[k];
            var door = e.Door;
            if (door == null) return new TrackerEdge(e.To, e.Length);
            var state = door.isLocked ? DoorState.Locked : door.isOpen ? DoorState.Open : DoorState.Closed;
            return new TrackerEdge(e.To, e.Length, state, door.OpenDelay);
        }
    }
}
