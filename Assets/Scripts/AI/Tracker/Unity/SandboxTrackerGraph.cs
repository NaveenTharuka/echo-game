using System;
using System.Collections.Generic;
using Echo.Navigation;
using UnityEngine;

namespace Echo.AI.Tracker
{
    /// Stub waypoint graph for Sandbox_TrackerGrid until M4's NavGraph exists. Nodes and links are baked by
    /// ECHO > Tracker > Build Sandbox_TrackerGrid; at runtime they become a TrackerGraph whose door states follow
    /// the scene's Door components (Door.Changed).
    [DefaultExecutionOrder(-100)]
    public class SandboxTrackerGraph : MonoBehaviour
    {
        [Serializable]
        public struct Link
        {
            public int a, b;
            public Door door;
        }

        public List<Vector3> nodes = new();
        public List<Link> links = new();

        readonly List<Door> doors = new();
        TrackerGraph graph;

        public TrackerGraph Graph
        {
            get { if (graph == null) Build(); return graph; }
        }

        void Awake() { if (graph == null) Build(); }
        void OnEnable() => Door.Changed += OnDoorChanged;
        void OnDisable() => Door.Changed -= OnDoorChanged;
        void Start() { for (int i = 0; i < doors.Count; i++) Sync(i); }   // doors apply startLocked in their own Awake

        void Build()
        {
            graph = new TrackerGraph();
            doors.Clear();
            foreach (var p in nodes) graph.AddNode(p);
            foreach (var l in links)
            {
                int id = -1;
                if (l.door != null)
                {
                    id = doors.IndexOf(l.door);
                    if (id < 0) { doors.Add(l.door); id = graph.AddDoor(StateOf(l.door), l.door.OpenDelay); }
                }
                graph.Connect(l.a, l.b, id);
            }
        }

        static DoorState StateOf(Door d) => d.isLocked ? DoorState.Locked : d.isOpen ? DoorState.Open : DoorState.Closed;

        void OnDoorChanged(Door d)
        {
            int id = doors.IndexOf(d);
            if (id >= 0) Sync(id);
        }

        void Sync(int id) => graph.SetDoor(id, StateOf(doors[id]));

        /// The Door on the link between two nodes, or null.
        public Door DoorBetween(int a, int b)
        {
            for (int i = 0; i < links.Count; i++)
            {
                var l = links[i];
                if (l.door != null && ((l.a == a && l.b == b) || (l.a == b && l.b == a))) return l.door;
            }
            return null;
        }

        void OnDrawGizmos()
        {
            Gizmos.color = new Color(0.3f, 0.8f, 1f, 0.6f);
            foreach (var p in nodes) Gizmos.DrawWireSphere(p + Vector3.up * 0.1f, 0.25f);
            foreach (var l in links)
            {
                if (l.a >= nodes.Count || l.b >= nodes.Count) continue;
                Gizmos.color = l.door == null ? new Color(0.3f, 0.8f, 1f, 0.6f)
                             : l.door.isLocked ? Color.red : l.door.isOpen ? Color.green : Color.yellow;
                Gizmos.DrawLine(nodes[l.a] + Vector3.up * 0.1f, nodes[l.b] + Vector3.up * 0.1f);
            }
        }
    }
}
