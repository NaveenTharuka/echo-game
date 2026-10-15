using System.Collections.Generic;
using Echo.Navigation;
using Echo.Sound;
using UnityEngine;

namespace Echo.AI
{
    /// AMBUSHER (guide §12): learns which way the player tends to move between zones, predicts the route,
    /// gets there first, and waits in silence.
    ///
    ///   OBSERVE (home, listening, learning) --confident prediction--> MOVE_TO_AMBUSH (run far, sneak the last 8 m)
    ///      ^                                                                  | arrived
    ///      +---- RELOCATE <-- timeout / player heard on another route ----- WAIT (silent, listening, facing the approach)
    ///                                                                         | sound <= strikeDistance, or touch
    ///                                                                       ATTACK (lunge + swing) --> OBSERVE
    ///                                                      missed? any sound within pursuitRange for pursuitWindow s
    ///                                                      --> lunge again (at most maxLunges in a row)
    /// Learning (RouteModel) runs on every tick whatever the state.
    public class AmbusherBrain : AgentBrain
    {
        enum State { Observe, MoveToAmbush, Wait, Attack, Relocate }

        [Header("Ambusher")]
        [Tooltip("Lunge at a sound this close while waiting (m).")]
        public float strikeDistance = 5f;
        [Tooltip("Reflex strike in any state for a precise sound this close (m).")]
        public float closeStrike = 2.2f;
        public float waitTimeout = 25f, minConfidence = 0.35f, minScore = 0.2f, playerSpeedGuess = 2.5f;
        [Tooltip("Commit only after the player was heard leaving the current zone this many times (decayed). " +
                 "With few neighbour zones the uniform prior alone already passes minConfidence.")]
        public float minEvidence = 1.8f;
        [Tooltip("Print every state change with its reason (decision log for the report / viva).")]
        public bool logDecisions = true;
        [Tooltip("Keep the learned routes when the player dies: \"it remembers you\".")]
        public bool keepMemoryAcrossDeaths = true;
        [Tooltip("Ignore sounds older than this when deciding (s).")]
        public float maxEstimateAge = 30f;
        [Header("Pursuit after a missed swing")]
        [Tooltip("Seconds after a swing during which a nearby sound triggers another lunge.")]
        public float pursuitWindow = 5f;
        [Tooltip("A sound this close (m) during the pursuit window triggers another lunge.")]
        public float pursuitRange = 10f;
        public int maxLunges = 3;

        RouteModel model;
        State state = State.Observe;
        float stateTime, confidence, swungAt = -1f;
        Vector3 playerEst, strikeTarget; float playerEstTime = -999f; int ambushNode = -1;
        readonly Dictionary<int, float> lastUsed = new();
        Dictionary<string, float> lastPass = new();
        float pursueUntil = -1f; int lunges;

        // Read by the Mimic (guide §11) - keep these three stable.
        public bool HasAmbushPoint => ambushNode >= 0 && state is State.MoveToAmbush or State.Wait;
        public bool IsWaiting => state == State.Wait;
        public Vector3 AmbushPoint => ambushNode >= 0 && NavGraph.Instance != null ? NavGraph.Instance.Position(ambushNode) : transform.position;

        public RouteModel Model => model;
        public override string DisplayName => "AMBUSHER";
        public override string StateName => model == null ? "NoGraph" : state.ToString();
        public override float AlertLevel => Time.time < pursueUntil ? 0.9f : state switch { State.Attack => 1f, State.Wait => 0.5f, State.MoveToAmbush => 0.35f, _ => 0.1f };
        public override float CurrentInterest => confidence;
        public override string DebugText => model == null ? "waiting for NavGraph + zones" :
            $"zone {model.CurrentZone ?? "-"}  obs {model.TotalObservations():0.0}  conf {confidence:0.00}  " +
            $"ambush {(ambushNode >= 0 ? NavGraph.Instance.Node(ambushNode).name : "-")}\n{model.TopTransitions()}";

        protected override void Think()
        {
            if (model == null)
            {
                var g = NavGraph.Instance;
                if (g == null || g.Count == 0) return;                       // NavGraph builds in Start
                model = new RouteModel(BuildZoneAdjacency(g));
                Motor.CostModifier = (from, to, c) =>                          // don't run into the player on the way (>= c: admissible)
                    Time.time - playerEstTime < 20f &&
                    Vector3.Distance(NavGraph.Instance.Position(to), playerEst) < 10f ? c * 3f : c;
            }

            // PERCEPTION -> MEMORY (learning happens continuously, whatever the state)
            foreach (var s in NewSounds)
            {
                if (ListenerPriority.TypeWeight(s.Type) < 0.3f) continue;     // ignore machinery etc.
                model.Observe(ZoneVolume.ZoneAt(s.EstimatedPosition), s.TimeHeard);
                playerEst = s.EstimatedPosition; playerEstTime = s.TimeHeard;

                float d = Vector3.Distance(s.EstimatedPosition, transform.position);
                bool pursuing = Time.time < pursueUntil && d <= pursuitRange;
                if (state != State.Attack &&
                    ((state == State.Wait && d <= strikeDistance) || pursuing ||
                     (d <= closeStrike && s.PositionError < 1.5f)))
                { BeginAttack(s.EstimatedPosition); }
            }

            stateTime += thinkInterval;
            switch (state)
            {
                case State.Observe:
                case State.Relocate:
                    if (stateTime > 1f && TrySelectAmbush()) BeginMove();
                    else if (state == State.Relocate && (Arrived || Failed)) Enter(State.Observe, "back home");
                    break;

                case State.MoveToAmbush:
                    if (Vector3.Distance(transform.position, AmbushPoint) < 8f) Motor.SetGait(AgentMotor.Gait.Sneak);
                    if (Arrived) BeginWait();
                    else if (Failed) Relocate(AgentMotor.Gait.Sneak, "could not reach the ambush point");
                    break;

                case State.Wait:
                    Motor.LookAt(LikelyApproach());
                    bool predictionBroken = model.CurrentZone != null && lastPass.Count > 0 &&
                                            Time.time - playerEstTime < 3f &&
                                            !lastPass.ContainsKey(model.CurrentZone) &&
                                            model.CurrentZone != NavGraph.Instance.Node(ambushNode).zone;
                    if (stateTime > waitTimeout) Relocate(AgentMotor.Gait.Sneak, $"nobody came in {waitTimeout:0} s");
                    else if (predictionBroken) Relocate(AgentMotor.Gait.Sneak, $"player heard in {model.CurrentZone}, off the predicted route");
                    break;

                case State.Attack:
                    // Lunge to where the sound came from, swing once when close / arrived / out of time, then reset.
                    if (swungAt < 0f && (Arrived || Failed || stateTime > 2.5f ||
                                         Vector3.Distance(transform.position, strikeTarget) <= Body.reach + 1f))
                    { Body.TryAttack(strikeTarget); swungAt = stateTime; }
                    else if (swungAt >= 0f && stateTime - swungAt > 1.8f && !Body.IsAttacking)
                    {
                        // Missed (the player is still alive): keep listening hard and lunge again at the next sound.
                        pursueUntil = lunges < maxLunges ? Time.time + pursuitWindow : -1f;
                        Enter(State.Observe, lunges < maxLunges ? $"swing done, pursuing for {pursuitWindow:0} s" : "swing done, giving up the chase");
                    }
                    break;
            }
        }

        bool TrySelectAmbush()
        {
            var g = NavGraph.Instance;
            if (model.CurrentZone == null || Time.time - playerEstTime > maxEstimateAge) return false;
            if (model.Evidence(model.CurrentZone) < minEvidence) return false;   // still learning this zone
            lastPass = model.PassProbability(model.CurrentZone, 3);
            confidence = 0f; foreach (var p in lastPass.Values) confidence = Mathf.Max(confidence, p);
            if (confidence < minConfidence) return false;

            int me = g.Nearest(transform.position), player = g.Nearest(playerEst);
            float age = Time.time - playerEstTime, bestScore = minScore; int best = -1;
            for (int n = 0; n < g.Count; n++)
            {
                var node = g.Node(n);
                if (!lastPass.TryGetValue(node.zone ?? "", out float pass) || pass < 0.25f) continue;
                float quality = (node.tags & NodeTag.AmbushSpot) != 0 ? 1f
                              : (node.tags & NodeTag.Doorway) != 0 ? 0.7f
                              : (node.tags & NodeTag.Junction) != 0 ? 0.5f : 0f;
                if (quality == 0f) continue;
                float playerEta = g.SoundDistance(player, n) / playerSpeedGuess - age;
                float myEta = g.SoundDistance(me, n) / Motor.runSpeed + 2f;        // + settle in
                float feasible = 1f / (1f + Mathf.Exp(-(playerEta - myEta) / 2f));  // soft "do I get there first?"
                float novelty = lastUsed.TryGetValue(n, out var t) && Time.time - t < 60f ? 0.6f : 1f;
                float score = pass * quality * feasible * novelty;
                if (score > bestScore) { bestScore = score; best = n; }
            }
            if (best < 0) return false;
            ambushNode = best; lastUsed[best] = Time.time;
            return true;
        }

        void BeginMove()
        {
            Enter(State.MoveToAmbush, $"predicted route from {model.CurrentZone}, conf {confidence:0.00} -> {NavGraph.Instance.Node(ambushNode).name} ({NavGraph.Instance.Node(ambushNode).zone})");
            Motor.MoveTo(AmbushPoint, AgentMotor.Gait.Run);
        }

        void BeginWait()
        {
            Enter(State.Wait, "in position, listening");
            Motor.Stop();
            Motor.LookAt(LikelyApproach());
            if (Anim) Anim.SetListening(true);
        }

        void BeginAttack(Vector3 where)
        {
            if (Time.time >= pursueUntil) lunges = 0;
            lunges++; pursueUntil = -1f;
            Enter(State.Attack, $"lunge {lunges}: sound {Vector3.Distance(where, transform.position):0.0} m away");
            strikeTarget = where; swungAt = -1f;
            Motor.MoveTo(where, AgentMotor.Gait.Run);
        }

        void Relocate(AgentMotor.Gait gait, string why) { Enter(State.Relocate, why); Motor.MoveTo(Home, gait); }

        /// The neighbour of the ambush node the player most likely comes from.
        Vector3 LikelyApproach()
        {
            var g = NavGraph.Instance; Vector3 best = transform.position + transform.forward; float bp = -1f;
            if (ambushNode < 0) return best;
            foreach (var e in g.Neighbours(ambushNode))
            {
                string z = g.Node(e.To).zone ?? "";
                float p = z == model.CurrentZone ? 1f : lastPass.TryGetValue(z, out var v) ? v : 0f;
                if (p > bp) { bp = p; best = g.Position(e.To); }
            }
            return best;
        }

        static Dictionary<string, HashSet<string>> BuildZoneAdjacency(NavGraph g)
        {
            var adj = new Dictionary<string, HashSet<string>>();
            for (int i = 0; i < g.Count; i++)
            {
                string a = g.Node(i).zone; if (string.IsNullOrEmpty(a)) continue;
                if (!adj.ContainsKey(a)) adj[a] = new HashSet<string>();
                foreach (var e in g.Neighbours(i))
                {
                    string b = g.Node(e.To).zone;
                    if (!string.IsNullOrEmpty(b) && b != a) adj[a].Add(b);
                }
            }
            return adj;
        }

        /// Called by AcousticSensor (optional): zone-level reports from facility microphones.
        public void SensorReport(string zone, Vector3 approx, float time)
        {
            if (model == null) return;
            model.Observe(zone, time); playerEst = approx; playerEstTime = time;
        }

        public override void OnContact(Vector3 where) { BeginAttack(where); }

        public override void OnHurt(Vector3 from)
        {
            // Shot: it knows roughly where from, roars and backs off to re-plan instead of charging a gun.
            playerEst = from; playerEstTime = Time.time;
            if (Anim) Anim.Alert();
            Relocate(AgentMotor.Gait.Run, "shot: backing off");
        }

        public override void ResetAgent()
        {
            base.ResetAgent();
            if (!keepMemoryAcrossDeaths) model?.Clear();
            ambushNode = -1; confidence = 0f; pursueUntil = -1f; lunges = 0; Enter(State.Observe, "reset");
        }

        void Enter(State s, string why)
        {
            if (logDecisions) Debug.Log($"[AMBUSHER t={Time.time:0.0}] {state} -> {s}: {why}", this);
            state = s; stateTime = 0f;
            Arrived = Failed = false;
            if (Anim) Anim.SetListening(s == State.Wait);
        }

        void OnDrawGizmosSelected()
        {
            if (!Application.isPlaying || model == null || NavGraph.Instance == null) return;
            var g = NavGraph.Instance;
            // Predicted pass probability per zone: bigger orange spheres on nodes = more likely route.
            for (int i = 0; i < g.Count; i++)
                if (lastPass.TryGetValue(g.Node(i).zone ?? "", out var p))
                {
                    Gizmos.color = new Color(1f, 0.55f, 0f, 0.35f + 0.5f * p);
                    Gizmos.DrawSphere(g.Position(i) + Vector3.up * 0.2f, 0.15f + 0.45f * p);
                }
            if (ambushNode >= 0)
            {
                Gizmos.color = Color.magenta;
                Gizmos.DrawSphere(AmbushPoint + Vector3.up * 0.5f, 0.5f);
            }
            Gizmos.color = Color.cyan;
            Gizmos.DrawWireSphere(playerEst, 0.4f);
        }
    }
}
