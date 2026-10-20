using System;
using System.Collections.Generic;
using Echo.AI.Tracker;
using Echo.Sound;
using UnityEngine;

namespace Echo.AI
{
    /// TRACKER body adapter (guide §10). All decisions are made by the Tracker core (`TrackerDecider`, plain C#,
    /// Echo.AI.Tracker): memory, credibility gate, velocity estimate, interception scoring, confidence, FSM.
    /// This MonoBehaviour only feeds it what the agent perceives (PerceivedSounds, touch, being shot) and executes
    /// its commands through the shared AgentMotor / AgentBody / AgentAnimatorBridge.
    public class TrackerBrain : AgentBrain
    {
        [Tooltip("All Tracker tunables (owned by the Tracker core).")]
        public TrackerSettings settings = new();

        readonly NavGraphTrackerWorld world = new();
        readonly List<TrackerPercept> percepts = new();
        TrackerDecider decider;
        bool touched, hurt; Vector3 touchPoint, hurtFrom;
        TrackerState lastState = TrackerState.Idle;
        bool calm = true;

        static readonly HeardKind[] KindOf = BuildKindMap();

        /// SoundType -> HeardKind by name (the core mirrors the frozen enum on purpose, so it never depends on Echo.Sound).
        static HeardKind[] BuildKindMap()
        {
            var names = Enum.GetNames(typeof(SoundType));
            var map = new HeardKind[names.Length];
            for (int i = 0; i < names.Length; i++)
                map[i] = Enum.TryParse(names[i], out HeardKind k) ? k : HeardKind.AgentVoice;   // AgentVoice = ignored by the core
            return map;
        }

        public TrackerDecider Decider => decider;
        public override string DisplayName => "TRACKER";
        public override string StateName => !IsAlive ? "Dead" : decider == null ? "NoGraph" : decider.State.ToString();
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
            settings.walkSpeed = Motor.walkSpeed;
            settings.runSpeed = Motor.runSpeed;
        }

        protected override void Think()
        {
            if (decider == null)
            {
                if (!world.Ready) return;                                   // NavGraph builds in Start
                decider = new TrackerDecider(world, settings, Home);
            }

            // PERCEPTION: what the ear reported, as the core's percept type. Being shot = a precise gunshot.
            percepts.Clear();
            foreach (var s in NewSounds)
                percepts.Add(new TrackerPercept(s.EstimatedPosition, s.PositionError, s.TimeHeard, KindOf[(int)s.Type]));
            if (hurt) { percepts.Add(new TrackerPercept(hurtFrom, 2f, Time.time, HeardKind.Gunshot)); hurt = false; }

            var input = new TrackerTickInput
            {
                Now = Time.time, Self = transform.position,
                Arrived = Arrived, Failed = Failed,
                Touched = touched, TouchPoint = touchPoint
            };
            touched = false;

            var cmd = decider.Tick(input, percepts);
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

        /// Animation cues from state changes (the brain still never touches the Animator itself).
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
                Motor.LookAt(decider.Predicted);   // turn towards where it thinks the player is
            lastState = st;
        }

        public override void OnContact(Vector3 where) { touched = true; touchPoint = where; }

        public override void OnHurt(Vector3 from) { hurt = true; hurtFrom = from; }

        public override void ResetAgent()
        {
            base.ResetAgent();
            decider?.Reset();
            lastState = TrackerState.Idle; calm = true;
        }

        void OnDrawGizmosSelected()
        {
            if (!Application.isPlaying || decider == null) return;
            if (decider.Memory.HasLast)
            {
                Gizmos.color = new Color(1f, 0.5f, 0f);
                Gizmos.DrawWireSphere(decider.Memory.Last.Position, 0.4f);
                if (decider.HasVelocity) Gizmos.DrawLine(decider.Memory.Last.Position, decider.Memory.Last.Position + decider.Velocity * 2f);
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
