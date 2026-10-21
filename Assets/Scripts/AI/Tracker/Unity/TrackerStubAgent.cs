using System.Collections.Generic;
using System.Diagnostics;
using Echo.Player;
using UnityEngine;

namespace Echo.AI.Tracker
{
    /// Thin body for the Tracker until M4's AgentBrain exists: a ~5 Hz think tick that feeds perceived
    /// sounds, mover events and touch into TrackerDecider and executes its command. No decision logic here.
    /// Touch = a small overlap sphere polled on the tick (stand-in for M4's ContactSense).
    /// Attack = a short-range overlap in front of the agent; only a body actually there is hit.
    [RequireComponent(typeof(NavMeshTrackerMover), typeof(TrackerPerceptionStub))]
    public class TrackerStubAgent : MonoBehaviour, ITrackerDebugSource
    {
        public SandboxTrackerGraph graph;
        public TrackerSettings settings = new();
        public float thinkInterval = 0.2f;
        public float touchRadius = 0.9f;
        public float attackReach = 1.1f, attackRadius = 0.9f;

        public TrackerDecider Decider { get; private set; }
        public int Ticks { get; private set; }
        public TrackerCommand LastCommand { get; private set; }
        public float LastThinkMs { get; private set; }
        public float AvgThinkMs { get; private set; }
        public float MaxThinkMs { get; private set; }
        public int Kills { get; private set; }

        ITrackerMover mover;
        TrackerPerceptionStub ears;
        UnityEngine.AI.NavMeshAgent nav;
        readonly List<TrackerPercept> percepts = new(64);
        readonly Collider[] overlap = new Collider[16];
        readonly Stopwatch watch = new();
        float nextThink;
        PlayerDeath subscribed;

        void Awake()
        {
            mover = GetComponent<ITrackerMover>();
            ears = GetComponent<TrackerPerceptionStub>();
            nav = GetComponent<UnityEngine.AI.NavMeshAgent>();
        }

        void Start()
        {
            if (graph == null) { UnityEngine.Debug.LogError("[TrackerStubAgent] No SandboxTrackerGraph assigned.", this); enabled = false; return; }
            Decider = new TrackerDecider(graph.Graph, settings, transform.position);
            if (mover is NavMeshTrackerMover m) { m.walkSpeed = settings.walkSpeed; m.runSpeed = settings.runSpeed; }
            Subscribe();
        }

        void OnDisable()
        {
            if (subscribed != null) subscribed.Died -= OnPlayerDied;
            subscribed = null;
        }

        void Subscribe()
        {
            if (subscribed != null || PlayerDeath.Current == null) return;
            subscribed = PlayerDeath.Current;
            subscribed.Died += OnPlayerDied;
        }

        /// Scripted tests: stop, teleport onto the NavMesh, forget everything. `asHome` also moves Home there.
        public void ResetAgent(Vector3 position, bool asHome = false)
        {
            mover.Stop();
            nav.Warp(position);
            if (Decider == null) return;
            if (asHome) Decider.Home = position;
            Decider.Reset();
        }

        void OnPlayerDied()
        {
            Decider?.Reset();
            mover.Stop();
        }

        void Update()
        {
            if (Decider == null || Time.time < nextThink) return;
            nextThink = Time.time + thinkInterval * Random.Range(0.85f, 1.15f);   // de-synchronise agents
            Subscribe();

            watch.Restart();
            percepts.Clear();
            ears.Drain(percepts);
            mover.ConsumeEvents(out bool arrived, out bool failed);
            var input = new TrackerTickInput
            {
                Now = Time.time, Self = transform.position, SelfSpeed = nav.velocity.magnitude, Arrived = arrived, Failed = failed
            };
            input.Touched = Touching(out input.TouchPoint);
            var cmd = Decider.Tick(input, percepts);
            watch.Stop();

            float ms = (float)watch.Elapsed.TotalMilliseconds;
            LastThinkMs = ms;
            AvgThinkMs = Ticks == 0 ? ms : Mathf.Lerp(AvgThinkMs, ms, 0.05f);
            if (Ticks > 10) MaxThinkMs = Mathf.Max(MaxThinkMs, ms);   // skip warm-up (first Dijkstra fields, JIT)
            Ticks++;
            LastCommand = cmd;
            Execute(cmd);
        }

        void Execute(in TrackerCommand cmd)
        {
            switch (cmd.Kind)
            {
                case TrackerCommandKind.MoveTo: mover.MoveTo(cmd.Target, cmd.Gait); break;
                case TrackerCommandKind.Stop: mover.Stop(); break;
                case TrackerCommandKind.Attack: Strike(cmd.Target); break;
            }
        }

        bool Touching(out Vector3 point)
        {
            point = default;
            int n = Physics.OverlapSphereNonAlloc(transform.position + Vector3.up, touchRadius, overlap, ~0, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < n; i++)
            {
                if (overlap[i] is CharacterController) { point = overlap[i].transform.position; return true; }
            }
            return false;
        }

        void Strike(Vector3 towards)
        {
            mover.Stop();
            Vector3 dir = towards - transform.position; dir.y = 0f;
            if (dir.sqrMagnitude > 1e-4f) transform.rotation = Quaternion.LookRotation(dir.normalized);
            Vector3 centre = transform.position + Vector3.up + transform.forward * attackReach;
            int n = Physics.OverlapSphereNonAlloc(centre, attackRadius, overlap, ~0, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < n; i++)
            {
                if (overlap[i] is CharacterController && PlayerDeath.Current != null &&
                    overlap[i].transform.IsChildOf(PlayerDeath.Current.transform) && PlayerDeath.Current.Kill(gameObject))
                { Kills++; return; }
            }
        }
    }
}
