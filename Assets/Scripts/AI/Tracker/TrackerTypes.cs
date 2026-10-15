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

    /// Player speed behind ETA_player (VelocityEstimator): |shrunk v| (phase 3, biased slow), the unshrunk WLS speed,
    /// or the speed shrunk towards a moving player's typical speed (velocityPrior) instead of 0.
    public enum PlayerEtaSpeed { Shrunk, Raw, Moving }

    /// How the time advantage enters the choice of node (InterceptScorer):
    ///   Reward     S = prox + dir + adv' (phase 3)
    ///   Saturated  adv' capped at advCap, so a big lead on a node the player reaches late or never stops paying
    ///   Constraint adv > 0 is required; prox + dir choose among the feasible nodes
    ///   ConstraintOnLine  adv > 0 and on the player's line (cos >= onLineCos); prox + dir choose
    /// Constraint modes fall back to prox + dir over all candidates when no node is feasible (follow, no claim).
    public enum AdvantageMode { Reward, Saturated, Constraint, ConstraintOnLine }

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
        [Tooltip("Prior std of the player's speed (m/s) for the shrinkage of noisy velocity estimates.")]
        public float velocityPrior = 3f;
        [Tooltip("Speed (m/s) at which the direction of v is fully trusted; dir terms scale by min(1, |v| / this).")]
        public float dirSpeedRef = 1f;

        [Header("Agent")]
        public float runSpeed = 5f;
        public float walkSpeed = 2.2f;

        [Header("ETA model (TrackerMotion; fitted by ECHO > Tracker > Calibrate Mover, 15 Oct)")]
        [Tooltip("ETA_tracker at the speed of the gait the Tracker will use (else always runSpeed, the phase-3 model).")]
        public bool etaFromGait = true;
        [Tooltip("Run to a scored interception node the Tracker reaches first (adv > 0) whatever the band, and score at run speed.")]
        public bool runToIntercept = true;    // adopted 15 Oct: catch 64.4 % vs 54.5 % (McNemar +100/-1, z = 9.85).
                                              // Scoring at the band's gait instead (walk in Walk) lost: 52.7 %, z = -2.41.
        [Tooltip("Seconds for a standing start (turn in place + accelerate), scaled by 1 - v0/v.")]
        public float etaStartup = 0.15f;
        [Tooltip("Metres short of the node at which the mover reports arrival (its arrive radius).")]
        public float etaArrive = 1f;
        [Tooltip("runToIntercept runs to a scored goal whose adv is above this (0 = only claimed interceptions; -1 = every scored goal).")]
        public float runAdvMin = -1f;         // 15 Oct: running only when adv > 0 made the gait hang on adv's sign
        [Tooltip("Player speed behind ETA_player. Heading and prediction always use the shrunk v.")]
        public PlayerEtaSpeed playerEtaSpeed = PlayerEtaSpeed.Moving;   // 15 Oct, sweep P4 (z = -1.0 vs P0, n.s.):
                                              // Unity runner ETA error -2.28 +- 2.36 s (shrunk) -> -0.80 +- 1.23 s

        [Header("Score S(n)")]
        public float wProx = 1.2f;
        public float wDir = 1f;
        public float wAdv = 1f;
        public float wHeat = 0f;            // 0.4 in the phase-1 design; dropped by the phase 3 sweep (z = 2.89,
                                            // Bonferroni 2.50). Heat still drives the cold-trail search.
        public float proxSigma = 6f;
        public float advScale = 3f;
        public float reachSlack = 5f;
        [Tooltip("Lead time from the Tracker's route distance to the predicted point (else straight line).")]
        public bool routeLead = false;        // rejected by the phase 3 sweep (z = -7.3)
        [Tooltip("Count a positive interception advantage only in the player's direction: adv * dir.")]
        public bool gateAdvantage = true;     // adopted by the phase 3 sweep (McNemar z = 5.2)
        [Tooltip("Time advantage as a reward, a capped reward or a feasibility constraint (see AdvantageMode).")]
        public AdvantageMode advMode = AdvantageMode.Saturated;   // 15 Oct sweep: vs reward +17/-4, z = 2.84 (Bonferroni 2.39);
                                              // constraint z = 0.52, constraint + on-line z = 1.83 (n.s.)
        [Tooltip("Saturated mode: cap on adv' (0.2 = 0.6 s at advScale 3).")]
        public float advCap = 0.2f;
        [Tooltip("ConstraintOnLine: min cos(v, n - p_last) for a node to count as on the player's line (0.9 ~ 25 deg).")]
        public float onLineCos = 0.9f;
        public float heatDecay = 0.995f;
        [Tooltip("Below this best S(n) the prediction explains nothing: investigate the latest sound instead.")]
        public float minInterceptScore = 0.5f;

        [Header("Confidence")]
        public float freshnessTau = 8f;
        public float evidenceObs = 3f;
        public float fitScale = 3f;
        public float runThreshold = 0.55f;
        public float walkThreshold = 0.25f;
        public float hysteresis = 0.1f;

        [Header("Behaviour")]
        public float replanDistance = 6f;
        [Tooltip("Commitment: keep the current scored goal unless the new best beats its CURRENT score by this much " +
                 "(or it stops being a candidate, loses its interception claim, or a door changes). 0 = phase-3 rule " +
                 "(switch on any > replanDistance move).")]
        public float switchMargin = 0.5f;           // 15 Oct replay, 40 seeds x 2 layouts: 0.25 / 0.5 / 1.0 all remove A-B-A flips in a2;
                                                    // 0.5 = fewest flips in the far-home layout without the 1.0 loss of interceptions
        [Tooltip("Anchor the prediction and candidate reach on the fitted trail position at the latest sound's time " +
                 "instead of the raw latest percept (which carries its full error).")]
        public bool fitAnchor = true;
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
