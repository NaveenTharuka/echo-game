using Echo.Navigation;
using UnityEngine;

namespace Echo.AI.Tracker
{
    /// The level as the Tracker core sees it: an ITrackerWorld view over the shared NavGraph (nodes, measured edge
    /// lengths, door state from Door). Version follows NavGraph.Version, which Door bumps on open/close and on
    /// lock/unlock, so the core's cached route fields refresh exactly when a route can change.
    /// Every member is safe while the graph is missing or not built yet (Count 0, Nearest -1).
    public sealed class TrackerNavGraphWorld : ITrackerWorld
    {
        static NavGraph Graph => NavGraph.Instance;

        public bool Ready { get { var g = Graph; return g != null && g.Count > 0; } }
        public int Count => Ready ? Graph.Count : 0;
        public int Version { get { var g = Graph; return g != null ? g.Version : 0; } }
        public Vector3 Position(int node) => Graph.Position(node);
        public int NeighbourCount(int node) => Graph.Neighbours(node).Count;
        public int Nearest(Vector3 p) => Ready ? Graph.Nearest(p) : -1;

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
