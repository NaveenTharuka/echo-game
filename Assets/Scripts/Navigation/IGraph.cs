using System.Collections.Generic;
using UnityEngine;

namespace Echo.Navigation
{
    public readonly struct Edge
    {
        public readonly int To; public readonly float Length; public readonly Door Door;
        public Edge(int to, float length, Door door) { To = to; Length = length; Door = door; }

        /// Path cost for planning: length, + time to open a closed door, infinite if locked.
        public float Cost => Door == null ? Length
                           : Door.isLocked ? float.PositiveInfinity
                           : Door.isOpen ? Length : Length + 3f;
    }

    /// Kept abstract so A* can be unit-tested on a tiny fake graph.
    public interface IGraph
    {
        int Count { get; }
        Vector3 Position(int node);
        IReadOnlyList<Edge> Neighbours(int node);
    }
}
