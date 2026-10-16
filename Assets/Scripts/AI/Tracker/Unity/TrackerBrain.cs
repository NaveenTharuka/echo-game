using System;
using System.Collections.Generic;
using System.Diagnostics;
using Echo.Player;
using Echo.Sound;
using UnityEngine;
using UnityEngine.AI;

namespace Echo.AI.Tracker
{
    /// The Tracker on the shared agent framework (guide §8, §10). Every decision is made by TrackerDecider (plain C#);
    /// this class only translates:
    ///   Hearing.Drain (AgentBrain, 5 Hz or urgent) -> TrackerPercept: estimated position, error radius, time, kind.
    ///   ContactSense -> touch. Mover events and ground speed -> TrackerTickInput.
    ///   TrackerCommand -> AgentMotor (A* over NavGraph, opens doors) / AgentBody (claw hit) / animator cues.
    /// Blind rule: nothing here reads the player. Being shot adds no percept - the shooter's position is not something
    /// the Tracker can know; the gunshot itself arrives through Hearing like every other sound.
    public class TrackerBrain : AgentBrain, ITrackerDebugSource
    {
        /// Not serialized on purpose: the code defaults are the tuned design (sweeps / A-B of 14-15 Oct), and a stale
        /// prefab or scene copy (e.g. wHeat 0.4 from the phase-1 design) must never override them silently.
        /// Speeds are taken from the AgentMotor so the ETA model matches the body that really moves.
        [NonSerialized] public readonly TrackerSettings Settings = new();

        readonly TrackerNavGraphWorld world = new();
        readonly List<TrackerPercept> percepts = new(32);
        readonly Stopwatch watch = new();
        TrackerDecider decider;
        NavMeshAgent nav;
        PlayerDeath subscribed;
        bool touched, calm = true;
        Vector3 touchPoint;
        TrackerState lastState = TrackerState.Idle;

        static readonly HeardKind[] KindOf = BuildKindMap();

        /// SoundType -> HeardKind by name, once (the core mirrors the frozen enum so it never depends on Echo.Sound).
        /// A name the core does not know maps to AgentVoice, which the core ignores.
        static HeardKind[] BuildKindMap()
        {
            var names = Enum.GetNames(typeof(SoundType));
            var map = new HeardKind[names.Length];
            for (int i = 0; i < names.Length; i++)
                map[i] = Enum.TryParse(names[i], out HeardKind k) ? k : HeardKind.AgentVoice;
            return map;
        }

        public TrackerDecider Decider => decider;
        public int Ticks { get; private set; }
        public float LastThinkMs { get; private set; }
        public float AvgThinkMs { get; private set; }
        public float MaxThinkMs { get; private set; }

        public override string DisplayName => "TRACKER";
        public override string StateName => decider == null ? "NoGraph" : decider.State.ToString();
        public override float AlertLevel => decider == null ? 0f : decider.State switch
        {
            TrackerState.Attack => 1f,
            TrackerState.Intercept => decider.Band == ConfidenceBand.Run ? 0.9f : 0.75f,
            TrackerState.Hold => 0.6f,
            TrackerState.Search => 0.5f,
            TrackerState.Analyse => 0.4f,
            _ => 0f
        };
        public override float CurrentInterest => decider == null ? 0f : decider.Confidence.Value;
        public override string DebugText => decider == null ? "waiting for NavGraph" :
            $"C={decider.Confidence.Value:0.00} band={decider.Band} mem={decider.Memory.Count} " +
            $"acc/sus/filt={decider.Accepted}/{decider.Suspects}/{decider.Filtered}" +
            (decider.HasVelocity ? $" v={decider.Velocity.magnitude:0.0}m/s" : "");

        protected override void Awake()
        {
            base.Awake();
            nav = GetComponent<NavMeshAgent>();
            Settings.walkSpeed = Motor.walkSpeed;
            Settings.runSpeed = Motor.runSpeed;
        }

        protected override void OnDisable()
        {
            base.OnDisable();
            if (subscribed != null) subscribed.Died -= OnPlayerDied;
            subscribed = null;
        }

        /// Player death = the core's Reset (memory and heat cleared, go home). Not perception: the run is over.
        void Subscribe()
        {
            var p = PlayerDeath.Current;
            if (subscribed == p || p == null) return;
            if (subscribed != null) subscribed.Died -= OnPlayerDied;
            subscribed = p;
            p.Died += OnPlayerDied;
        }

        void OnPlayerDied()
        {
            if (decider != null) decider.Reset();
            Motor.Stop();
            lastState = TrackerState.Idle; calm = true;
        }

        protected override void Think()
        {
            if (decider == null)
            {
                if (!world.Ready) return;                                   // NavGraph builds in Start
                decider = new TrackerDecider(world, Settings, Home);
            }
            Subscribe();

            watch.Restart();
            percepts.Clear();
            for (int i = 0; i < NewSounds.Count; i++)
            {
                var s = NewSounds[i];
                percepts.Add(new TrackerPercept(s.EstimatedPosition, s.PositionError, s.TimeHeard, KindOf[(int)s.Type]));
            }
            var input = new TrackerTickInput
            {
                Now = Time.time, Self = transform.position, SelfSpeed = nav.velocity.magnitude,
                Arrived = Arrived, Failed = Failed, Touched = touched, TouchPoint = touchPoint
            };
            touched = false;
            var cmd = decider.Tick(input, percepts);
            watch.Stop();

            float ms = (float)watch.Elapsed.TotalMilliseconds;
            LastThinkMs = ms;
            AvgThinkMs = Ticks == 0 ? ms : Mathf.Lerp(AvgThinkMs, ms, 0.05f);
            if (Ticks > 10) MaxThinkMs = Mathf.Max(MaxThinkMs, ms);        // skip warm-up (first Dijkstra fields, JIT)
            Ticks++;

            Execute(cmd);
            Present();
        }

        void Execute(in TrackerCommand cmd)
        {
            switch (cmd.Kind)
            {
                case TrackerCommandKind.MoveTo:
                    Motor.MoveTo(cmd.Target, cmd.Gait == TrackerGait.Run ? AgentMotor.Gait.Run : AgentMotor.Gait.Walk);
                    break;
                case TrackerCommandKind.Stop:
                    Motor.Stop();
                    break;
                case TrackerCommandKind.Attack:
                    Body.TryAttack(cmd.Target);
                    break;
            }
        }

        /// Animation cues from state changes (the brain never touches the Animator itself).
        void Present()
        {
            var st = decider.State;
            if (st != lastState)
            {
                if (st == TrackerState.Idle) calm = true;
                else if (st == TrackerState.Intercept)
                {
                    if (calm && decider.Band == ConfidenceBand.Run && Anim) Anim.Alert();   // locks on before the chase
                    calm = false;
                }
            }
            if (Anim) Anim.SetListening(st is TrackerState.Analyse or TrackerState.Hold);
            if (st is TrackerState.Analyse or TrackerState.Hold && !Motor.IsMoving && decider.Memory.HasLast)
                Motor.LookAt(decider.Predicted);                            // face where it thinks the player is
            lastState = st;
        }

        public override void OnContact(Vector3 where) { touched = true; touchPoint = where; }

        /// Blind rule: `from` (the shooter's true position) is ignored; the Gunshot is heard through Hearing.
        public override void OnHurt(Vector3 from) { }

        public override void ResetAgent()
        {
            base.ResetAgent();
            if (decider != null) { decider.Home = Home; decider.Reset(); }
            lastState = TrackerState.Idle; calm = true;
        }

        /// Scripted tests: teleport to `position` (optionally as the new home) and forget everything.
        public void ResetTo(Vector3 position, bool asHome)
        {
            if (asHome) { Home = position; HomeRotation = transform.rotation; }
            Motor.Warp(position);
            if (decider != null) { decider.Home = Home; decider.Reset(); }
            touched = false; lastState = TrackerState.Idle; calm = true;
        }

        void OnDrawGizmosSelected()
        {
            if (!Application.isPlaying || decider == null) return;
            if (decider.Memory.HasLast)
            {
                // The trail point the decider actually scored from (fitted line), not the raw latest percept.
                Vector3 a = decider.Anchor.Position;
                Gizmos.color = new Color(1f, 0.5f, 0f);
                Gizmos.DrawWireSphere(a, 0.4f);
                if (decider.HasVelocity) Gizmos.DrawLine(a, a + decider.Velocity * 2f);
                Gizmos.color = Color.red;
                Gizmos.DrawWireSphere(decider.Predicted, 0.6f);
            }
            if (decider.HasGoal)
            {
                Gizmos.color = Color.green;
                Gizmos.DrawLine(transform.position + Vector3.up, decider.Goal);
                Gizmos.DrawWireCube(decider.Goal, Vector3.one * 0.5f);
            }
        }
    }
}
