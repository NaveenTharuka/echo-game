using System;
using UnityEngine;

namespace Echo.AI.Tracker
{
    /// Mirrors Echo.Sound.SoundType (guide §4.1, frozen). Names must match exactly: the Hearing
    /// adapter maps by name, so the Tracker core never depends on the sound core.
    public enum HeardKind
    {
        Footstep, Run, Jump, Land, Gunshot, Reload, BulletImpact, Door, MetalImpact,
        GlassBreak, ObjectDrop, Machinery, Generator, Switch, AgentVoice
    }

    /// What the Tracker is allowed to know about a sound (blind rule): an estimated position with
    /// an error radius, when it was heard and what kind it was. Never the true source or emitter.
    public struct TrackerPercept
    {
        public Vector3 Position;
        public float Error;     // metres, radius of uncertainty
        public float Time;
        public HeardKind Kind;

        public TrackerPercept(Vector3 position, float error, float time, HeardKind kind)
        {
            Position = position; Error = error; Time = time; Kind = kind;
        }
    }

    /// Which heard kinds are evidence of where the player is. Copied here on purpose (no dependency
    /// on the Listener's ListenerPriority).
    public static class TrackerSoundFilter
    {
        /// Machinery/Generator are ambient loops, AgentVoice is another agent, and BulletImpact
        /// marks where a bullet landed, not where the shooter stands (the Gunshot already does).
        public static bool IsTrail(HeardKind kind) => kind switch
        {
            HeardKind.Machinery or HeardKind.Generator or HeardKind.AgentVoice or HeardKind.BulletImpact => false,
            _ => true
        };
    }

    public enum DoorState : byte { None, Open, Closed, Locked }

    public struct TrackerEdge
    {
        public int To;
        public float Length;        // metres
        public DoorState Door;
        public float OpenDelay;     // seconds to open a closed door on this edge

        public TrackerEdge(int to, float length, DoorState door = DoorState.None, float openDelay = 0f)
        {
            To = to; Length = length; Door = door; OpenDelay = openDelay;
        }
    }

    /// Everything the Tracker core knows about the level: a waypoint graph with door state.
    /// Implemented by TrackerGraph (sandbox / checks) and later by a NavGraph adapter.
    public interface ITrackerWorld
    {
        int Count { get; }
        /// Incremented whenever any door changes state, so cached route fields can be invalidated.
        int Version { get; }
        Vector3 Position(int node);
        int NeighbourCount(int node);
        TrackerEdge Neighbour(int node, int k);
        /// Nearest node to a world position, or -1 if the graph is empty.
        int Nearest(Vector3 p);
    }

    /// How the Tracker's body moves. NavMeshTrackerMover now; an AgentMotor adapter when M4's code lands.
    public interface ITrackerMover
    {
        /// False if the target cannot be reached at all (Failed is also raised).
        bool MoveTo(Vector3 target, TrackerGait gait);
        void Stop();
        /// One-shot events since the last call.
        void ConsumeEvents(out bool arrived, out bool failed);
    }

    public static class TrackerCosts
    {
        /// Edge cost in metres. A closed door costs the distance the traveller could have covered while
        /// opening it (OpenDelay x speed); a locked door is impassable. Every cost >= Length, so a
        /// Euclidean heuristic stays admissible.
        public static float EdgeCost(in TrackerEdge e, float speed) => e.Door switch
        {
            DoorState.Locked => float.PositiveInfinity,
            DoorState.Closed => e.Length + e.OpenDelay * speed,
            _ => e.Length
        };
    }

    /// Every Tracker tunable in one place. Defaults are the approved design (CLAUDE.md, Tracker).
    [Serializable]
    public class TrackerSettings
    {
        [Header("Memory & credibility")]
        public int capacity = 8;
        public float window = 25f;
        public float maxPlayerSpeed = 6.5f;
        public float credibilityMargin = 2f;
        public float credibilityFalloff = 4f;
        public float suspectThreshold = 0.3f;

        [Header("Velocity")]
        public float tau = 8f;
        public float minSpan = 1f;

        [Header("Agent")]
        public float runSpeed = 5f;
        public float walkSpeed = 2.2f;

        [Header("Score S(n)")]
        public float wProx = 1.2f;
        public float wDir = 1f;
        public float wAdv = 1f;
        public float wHeat = 0.4f;
        public float proxSigma = 6f;
        public float advScale = 3f;
        public float reachSlack = 5f;
        public float heatDecay = 0.995f;

        [Header("Confidence")]
        public float freshnessTau = 8f;
        public float evidenceObs = 3f;
        public float fitScale = 3f;
        public float runThreshold = 0.55f;
        public float walkThreshold = 0.25f;
        public float hysteresis = 0.1f;

        [Header("Behaviour")]
        public float replanDistance = 6f;
        public float holdTime = 4f;
        public float attackTime = 1.8f;
        public float strikeDistance = 2.2f;
        public float strikeMaxError = 1.5f;

        [Header("Cold-trail search")]
        public float searchHeatWeight = 0.5f;
        public float searchHold = 2f;
        public float searchMaxTime = 15f;
        public int searchMaxNodes = 6;
    }
}
