using System;
using UnityEngine;

namespace Echo.AI.Tracker
{
    /// Long-term memory: per-node count of credible sounds, decayed x heatDecay per think tick
    /// (0.995 at 5 Hz = half-life ~28 s). Normalised by the current maximum so it is 0..1.
    public sealed class HeatMap
    {
        float[] values = Array.Empty<float>();
        float max;

        public void EnsureSize(int n) { if (values.Length != n) { values = new float[n]; max = 0f; } }
        public void Clear() { Array.Clear(values, 0, values.Length); max = 0f; }

        public void Add(int node)
        {
            if (node < 0 || node >= values.Length) return;
            values[node] += 1f;
            if (values[node] > max) max = values[node];
        }

        public void Decay(float factor)
        {
            max = 0f;
            for (int i = 0; i < values.Length; i++) { values[i] *= factor; if (values[i] > max) max = values[i]; }
        }

        public float Normalised(int node) => max > 1e-4f ? values[node] / max : 0f;
    }

    /// Picks the interception node. Candidates are nodes the player could have reached:
    ///   route(p_last, n) <= v_max * lead + reachSlack,   reachable by the Tracker (no locked door).
    /// Each candidate is scored
    ///   S(n) = wProx*prox + wDir*dir + wAdv*adv + wHeat*heat
    ///   prox = exp(-|n - p_pred|^2 / (2 sigma^2))          close to the extrapolated point
    ///   dir  = max(0, cos(v, n - p_last))                    in the direction the player is going
    ///   adv  = clamp((ETA_player - ETA_tracker) / advScale, -1, 1)   the Tracker gets there first
    ///   heat = normalised heat                              where the player has been heard before
    /// ETA_player = max(0, route(p_last, n) / |v| - (now - t_last)), ETA_tracker = route(me, n) / runSpeed.
    public sealed class InterceptScorer
    {
        public float[] Scores { get; private set; } = Array.Empty<float>();
        public float[] Advantage { get; private set; } = Array.Empty<float>();
        public bool[] IsCandidate { get; private set; } = Array.Empty<bool>();

        public int Best { get; private set; } = -1;
        public int Second { get; private set; } = -1;
        public float BestScore { get; private set; }
        public float SecondScore { get; private set; }
        public int CandidateCount { get; private set; }
        /// (best - second) / best: 0 = two equally good guesses, 1 = one obvious answer.
        public float Margin { get; private set; }
        public Vector3 Predicted { get; private set; }
        public float Lead { get; private set; }
        public float Reach { get; private set; }

        void EnsureSize(int n)
        {
            if (Scores.Length == n) return;
            Scores = new float[n]; Advantage = new float[n]; IsCandidate = new bool[n];
        }

        /// `banned` (may be null): nodes this trail already failed to reach.
        public bool Evaluate(RouteService routes, in TrackerObservation last, Vector3 v, Vector3 self, int selfNode,
                             float now, HeatMap heat, bool[] banned, TrackerSettings s)
        {
            var w = routes.World;
            int n = w.Count;
            EnsureSize(n);
            Best = Second = -1; BestScore = SecondScore = float.NegativeInfinity; CandidateCount = 0; Margin = 0f;

            float age = Mathf.Max(0f, now - last.Time);
            // lead = time since the sound + time the Tracker needs to get there; iterate twice because
            // the ETA depends on where the prediction lands.
            float lead = age + Vector3.Distance(self, last.Position) / s.runSpeed;
            Vector3 pred = last.Position + v * lead;
            lead = age + Vector3.Distance(self, pred) / s.runSpeed;
            pred = last.Position + v * lead;
            Predicted = pred; Lead = lead;
            Reach = s.maxPlayerSpeed * lead + s.reachSlack;

            var fromLast = routes.Field(last.Node).Dist;
            var fromSelf = routes.Field(selfNode).Dist;
            float lastOffset = Vector3.Distance(last.Position, w.Position(last.Node));
            float selfOffset = selfNode >= 0 ? Vector3.Distance(self, w.Position(selfNode)) : 0f;

            float speed = v.magnitude;
            Vector3 dir = speed > 0.1f ? v / speed : Vector3.zero;
            float inv2Sigma2 = 1f / (2f * s.proxSigma * s.proxSigma);

            for (int i = 0; i < n; i++)
            {
                IsCandidate[i] = false; Scores[i] = 0f; Advantage[i] = 0f;
                float routeP = lastOffset + fromLast[i];
                float routeT = selfOffset + fromSelf[i];
                if (routeP > Reach || float.IsPositiveInfinity(routeT)) continue;   // unreachable or impossible
                if (banned != null && banned[i]) continue;

                Vector3 np = w.Position(i);
                float prox = Mathf.Exp(-(np - pred).sqrMagnitude * inv2Sigma2);

                Vector3 off = np - last.Position; off.y = 0f;
                float offLen = off.magnitude;
                float dirTerm = dir != Vector3.zero && offLen > 0.5f ? Mathf.Max(0f, Vector3.Dot(dir, off / offLen)) : 0f;

                float etaPlayer = Mathf.Max(0f, routeP / Mathf.Max(speed, 0.1f) - age);
                float etaTracker = routeT / s.runSpeed;
                float adv = Mathf.Clamp((etaPlayer - etaTracker) / s.advScale, -1f, 1f);

                float score = s.wProx * prox + s.wDir * dirTerm + s.wAdv * adv + s.wHeat * heat.Normalised(i);

                IsCandidate[i] = true; Scores[i] = score; Advantage[i] = adv;
                CandidateCount++;
                if (score > BestScore) { Second = Best; SecondScore = BestScore; Best = i; BestScore = score; }
                else if (score > SecondScore) { Second = i; SecondScore = score; }
            }

            if (Best >= 0)
                Margin = CandidateCount == 1 ? 1f
                       : BestScore > 1e-4f ? Mathf.Clamp01((BestScore - SecondScore) / BestScore) : 0f;
            return Best >= 0;
        }
    }
}
