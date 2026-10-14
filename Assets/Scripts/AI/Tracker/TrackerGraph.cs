using System.Collections.Generic;
using UnityEngine;

namespace Echo.AI.Tracker
{
    /// Plain adjacency-list ITrackerWorld. Used by the editor checks and the sandbox graph until
    /// M4's NavGraph exists. Edges are undirected; a door is shared by both directions of its edge.
    public sealed class TrackerGraph : ITrackerWorld
    {
        struct Link { public int To; public float Length; public int Door; }
        struct DoorData { public DoorState State; public float OpenDelay; }

        readonly List<Vector3> positions = new();
        readonly List<List<Link>> links = new();
        readonly List<DoorData> doors = new();

        public int Count => positions.Count;
        public int Version { get; private set; }

        public int AddNode(Vector3 p)
        {
            positions.Add(p);
            links.Add(new List<Link>(4));
            Version++;
            return positions.Count - 1;
        }

        public int AddDoor(DoorState state, float openDelay)
        {
            doors.Add(new DoorData { State = state, OpenDelay = openDelay });
            Version++;
            return doors.Count - 1;
        }

        public void SetDoor(int door, DoorState state)
        {
            var d = doors[door];
            if (d.State == state) return;
            d.State = state; doors[door] = d;
            Version++;
        }

        public DoorState GetDoor(int door) => doors[door].State;

        /// Undirected edge; length defaults to the straight-line distance.
        public void Connect(int a, int b, int door = -1, float length = -1f)
        {
            if (length < 0f) length = Vector3.Distance(positions[a], positions[b]);
            links[a].Add(new Link { To = b, Length = length, Door = door });
            links[b].Add(new Link { To = a, Length = length, Door = door });
            Version++;
        }

        public Vector3 Position(int node) => positions[node];
        public int NeighbourCount(int node) => links[node].Count;

        public TrackerEdge Neighbour(int node, int k)
        {
            var l = links[node][k];
            if (l.Door < 0) return new TrackerEdge(l.To, l.Length);
            var d = doors[l.Door];
            return new TrackerEdge(l.To, l.Length, d.State, d.OpenDelay);
        }

        public int Nearest(Vector3 p)
        {
            int best = -1; float bd = float.MaxValue;
            for (int i = 0; i < positions.Count; i++)
            {
                float d = (positions[i] - p).sqrMagnitude;
                if (d < bd) { bd = d; best = i; }
            }
            return best;
        }
    }
}
