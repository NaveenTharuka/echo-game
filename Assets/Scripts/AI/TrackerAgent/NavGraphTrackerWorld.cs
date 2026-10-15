using Echo.AI.Tracker;
using Echo.Navigation;
using UnityEngine;

namespace Echo.AI
{
    /// The level as the Tracker core sees it: an ITrackerWorld view over the shared NavGraph (nodes, edge lengths,
    /// door states). Version follows NavGraph.Version, so the core's cached route fields refresh on door changes.
    public sealed class NavGraphTrackerWorld : ITrackerWorld
    {
        public static NavGraph Graph => NavGraph.Instance;
        public bool Ready => Graph != null && Graph.Count > 0;

        public int Count => Ready ? Graph.Count : 0;
        public int Version => Graph != null ? Graph.Version : 0;
        public Vector3 Position(int node) => Graph.Position(node);
        public int NeighbourCount(int node) => Graph.Neighbours(node).Count;
        public int Nearest(Vector3 p) => Ready ? Graph.Nearest(p) : -1;

        public TrackerEdge Neighbour(int node, int k)
        {
            var e = Graph.Neighbours(node)[k];
            if (e.Door == null) return new TrackerEdge(e.To, e.Length);
            var state = e.Door.isLocked ? DoorState.Locked : e.Door.isOpen ? DoorState.Open : DoorState.Closed;
            return new TrackerEdge(e.To, e.Length, state, e.Door.OpenDelay);
        }
    }
}
