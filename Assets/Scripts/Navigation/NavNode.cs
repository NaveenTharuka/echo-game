using System;
using System.Collections.Generic;
using UnityEngine;

namespace Echo.Navigation
{
    [Flags]
    public enum NodeTag { None = 0, Corridor = 1, Room = 2, Doorway = 4, Junction = 8,
                          AmbushSpot = 16, DeadEnd = 32, Hub = 64 }

    /// One waypoint of the planning graph (guide §7.2). Links and zone are filled by ECHO > Nav Graph > Auto-Link Nodes.
    public class NavNode : MonoBehaviour
    {
        public string zone;                         // filled by the auto-linker from ZoneVolume
        public NodeTag tags;
        public List<NavNode> links = new();         // filled by the auto-linker

        void OnDrawGizmos()
        {
            Gizmos.color = (tags & NodeTag.AmbushSpot) != 0 ? Color.magenta
                         : (tags & NodeTag.Doorway) != 0 ? Color.yellow : Color.cyan;
            Gizmos.DrawSphere(transform.position + Vector3.up * 0.2f, 0.25f);
            Gizmos.color = new Color(0f, 1f, 1f, 0.35f);
            foreach (var l in links) if (l) Gizmos.DrawLine(transform.position + Vector3.up * 0.2f,
                                                              l.transform.position + Vector3.up * 0.2f);
        }
    }
}
