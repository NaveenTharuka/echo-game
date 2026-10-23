using System.Collections.Generic;
using Echo.Navigation;
using UnityEngine;

namespace Echo.AI
{
    /// Idle behaviour shared by every agent (Phase 2): instead of standing at one post, prowl the territory - walk to a
    /// NavGraph node in one of `zones`, stop and listen for a few seconds, pick another. Its footsteps are real sounds
    /// the player hears, so a patrol is both a threat and something to time your moves against.
    /// The brain owns the decision: it calls Tick() only while it has nothing better to do, and Interrupt() when it
    /// leaves that state. Without zones it roams the nodes within `radius` of where it started.
    [DisallowMultipleComponent]
    public class AgentPatrol : MonoBehaviour
    {
        public bool patrolEnabled = true;
        [Tooltip("Territory: NavNode zone ids (ZoneVolume names without 'Zone_'). Empty = nodes within radius of home.")]
        public string[] zones = new string[0];
        public float radius = 18f;
        public AgentMotor.Gait gait = AgentMotor.Gait.Walk;
        [Tooltip("Seconds standing and listening at each stop (min, max).")]
        public Vector2 pause = new(2f, 6f);
        [Tooltip("Next stop at least this far away (m), so it really moves through the territory.")]
        public float minLeg = 7f;
        [Tooltip("Random chance per stop to turn and look somewhere else while listening.")]
        [Range(0f, 1f)] public float lookAround = 0.6f;

        public bool IsPatrolling => active;
        public Vector3 CurrentStop { get; private set; }

        readonly List<int> stops = new();
        readonly Queue<int> recent = new();
        Vector3 home;
        bool active, moving, built;
        float pauseUntil;
        int builtForVersion = -1;

        void Awake() => home = transform.position;

        /// Call every think tick while idle. `arrived` / `failed` = the brain's motor snapshot for this tick.
        public void Tick(AgentMotor motor, AgentAnimatorBridge anim, bool arrived, bool failed)
        {
            if (!patrolEnabled || motor == null) return;
            if (!EnsureStops()) return;

            if (!active) { active = true; moving = false; pauseUntil = Time.time + Random.Range(0.5f, pause.y); }

            if (moving)
            {
                if (arrived || failed || (!motor.IsMoving && Time.time > pauseUntil))
                {
                    moving = false;
                    pauseUntil = Time.time + Random.Range(pause.x, pause.y);
                    if (anim != null) anim.SetListening(true);
                    if (Random.value < lookAround)
                    {
                        var g = NavGraph.Instance;
                        motor.LookAt(g.Position(stops[Random.Range(0, stops.Count)]));
                    }
                }
                return;
            }

            if (Time.time < pauseUntil) return;
            int next = PickStop();
            if (next < 0) return;
            CurrentStop = NavGraph.Instance.Position(next);
            if (anim != null) anim.SetListening(false);
            if (motor.MoveTo(CurrentStop, gait)) { moving = true; pauseUntil = Time.time + 2f; }
            else pauseUntil = Time.time + 1f;
        }

        /// The brain took over (heard something, attacks, ...). The next Tick starts a fresh leg.
        public void Interrupt()
        {
            active = false; moving = false;
        }

        bool EnsureStops()
        {
            var g = NavGraph.Instance;
            if (g == null || g.Count == 0) return false;
            if (built && builtForVersion == g.Version) return stops.Count > 0;
            built = true; builtForVersion = g.Version;
            stops.Clear();
            for (int i = 0; i < g.Count; i++)
            {
                var n = g.Node(i);
                bool inside = zones != null && zones.Length > 0
                    ? System.Array.IndexOf(zones, n.zone) >= 0
                    : Vector3.Distance(n.transform.position, home) <= radius;
                if (inside) stops.Add(i);
            }
            if (stops.Count == 0) Debug.LogWarning($"[Patrol] {name}: no NavGraph nodes in its territory", this);
            return stops.Count > 0;
        }

        int PickStop()
        {
            if (stops.Count == 0) return -1;
            var g = NavGraph.Instance;
            Vector3 here = transform.position;
            // Prefer stops it has not been to lately and that are a proper walk away; random among the good ones.
            int best = -1; float bestScore = -1f;
            for (int k = 0; k < 6; k++)
            {
                int i = stops[Random.Range(0, stops.Count)];
                float d = Vector3.Distance(g.Position(i), here);
                float score = Mathf.Min(d, 25f) * (recent.Contains(i) ? 0.2f : 1f) * (d < minLeg ? 0.3f : 1f) * Random.Range(0.8f, 1.2f);
                if (score > bestScore) { bestScore = score; best = i; }
            }
            recent.Enqueue(best);
            while (recent.Count > 3) recent.Dequeue();
            return best;
        }

        void OnDrawGizmosSelected()
        {
            if (!Application.isPlaying || !active) return;
            Gizmos.color = new Color(0.4f, 1f, 0.4f, 0.8f);
            Gizmos.DrawLine(transform.position + Vector3.up, CurrentStop + Vector3.up);
            Gizmos.DrawWireSphere(CurrentStop, 0.4f);
        }
    }
}
