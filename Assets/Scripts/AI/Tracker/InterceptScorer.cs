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
    ///   dir  = min(1, |v|/dirSpeedRef) * max(0, cos(v, n - p_last))   in the direction the player is going
    ///   adv  = clamp((ETA_player - ETA_tracker) / advScale, -1, 1)   the Tracker gets there first
    ///   heat = normalised heat                              where the player has been heard before
    /// ETA_player = max(0, route(p_last, n) / playerSpeed - (now - t_last)), playerSpeed per settings.playerEtaSpeed
    /// (<= 0: |v|; the shrunk |v| is biased slow and made the Tracker claim interceptions it did not have),
    /// ETA_tracker = TrackerMotion.Eta(route(me, n), gait speed, current speed): the gait the Tracker will really use
    /// (walk in the Walk band), fitted to the real mover. The phase-3 model (route / runSpeed) assumed it always ran.
    public sealed class InterceptScorer
    {
        public float[] Scores { get; private set; } = Array.Empty<float>();
        public float[] Advantage { get; private set; } = Array.Empty<float>();
        /// Per-candidate ETAs (s from now) behind adv: diagnostics for the scenarios and the overlay.
        public float[] EtaTracker { get; private set; } = Array.Empty<float>();
        public float[] EtaPlayer { get; private set; } = Array.Empty<float>();
        public bool[] IsCandidate { get; private set; } = Array.Empty<bool>();
        /// The node is one the Tracker claims to reach first: adv > 0 (Reward/Saturated) or feasible (Constraint modes).
        public bool[] Claimed { get; private set; } = Array.Empty<bool>();
        /// Constraint modes: no node was feasible, so the pick is a follow target (prox + dir over all candidates).
        public bool Fallback { get; private set; }

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
            EtaTracker = new float[n]; EtaPlayer = new float[n]; Claimed = new bool[n];
        }

        /// `banned` (may be null): nodes this trail already failed to reach. `gaitSpeed`: speed of the gait the
        /// Tracker will move at (<= 0: runSpeed); `selfSpeed`: its current speed (standing start cost).
        public bool Evaluate(RouteService routes, in TrackerObservation last, Vector3 v, Vector3 self, int selfNode,
                             float now, HeatMap heat, bool[] banned, TrackerSettings s, float gaitSpeed = 0f, float selfSpeed = 0f,
                             float playerSpeed = 0f)
        {
            float vT = gaitSpeed > 0f ? gaitSpeed : s.runSpeed;
            var w = routes.World;
            int n = w.Count;
            EnsureSize(n);
            Best = Second = -1; BestScore = SecondScore = float.NegativeInfinity; CandidateCount = 0; Margin = 0f;
            if (last.Node < 0 || last.Node >= n) return false;     // trail point not on the graph
            bool constraint = s.advMode == AdvantageMode.Constraint || s.advMode == AdvantageMode.ConstraintOnLine;
            // Constraint modes keep two pools: feasible nodes (used if any) and all candidates (the fallback).
            int fBest = -1, fSecond = -1, fCount = 0; float fBestScore = float.NegativeInfinity, fSecondScore = float.NegativeInfinity;

            // Player routes from the point itself (pointRoute) or from its nearest node + offset (phase 1-3).
            var fromLast = s.pointRoute ? routes.FieldFrom(last.Position, last.Node).Dist : routes.Field(last.Node).Dist;
            var fromSelf = routes.Field(selfNode).Dist;
            float lastOffset = s.pointRoute ? 0f : Vector3.Distance(last.Position, w.Position(last.Node));
            float selfOffset = selfNode >= 0 ? Vector3.Distance(self, w.Position(selfNode)) : 0f;

            float age = Mathf.Max(0f, now - last.Time);
            // lead = time since the sound + time the Tracker needs to get there; iterate twice because
            // the ETA depends on where the prediction lands. Route lead measures that ETA along the graph.
            float lead = age + Eta(Vector3.Distance(self, last.Position), vT, selfSpeed, s);
            Vector3 pred = last.Position + v * lead;
            for (int it = 0; it < 2; it++)
            {
                float eta = Vector3.Distance(self, pred);
                if (s.routeLead)
                {
                    int pn = w.Nearest(pred);
                    float r = pn >= 0 ? selfOffset + fromSelf[pn] + Vector3.Distance(pred, w.Position(pn)) : float.PositiveInfinity;
                    if (!float.IsPositiveInfinity(r)) eta = r;
                }
                lead = age + Eta(eta, vT, selfSpeed, s);
                pred = last.Position + v * lead;
                if (!s.routeLead) break;          // straight-line lead: the guide's two-step estimate
            }
            Predicted = pred; Lead = lead;
            Reach = s.maxPlayerSpeed * lead + s.reachSlack;

            float speed = v.magnitude;
            float etaSpeed = Mathf.Max(0.1f, playerSpeed > 0f ? playerSpeed : speed);
            Vector3 dir = speed > 0.1f ? v / speed : Vector3.zero;
            // A near-zero velocity is noise, not a heading: trust the direction in proportion to the speed.
            float dirTrust = Mathf.Clamp01(speed / Mathf.Max(0.01f, s.dirSpeedRef));
            float inv2Sigma2 = 1f / (2f * s.proxSigma * s.proxSigma);

            for (int i = 0; i < n; i++)
            {
                IsCandidate[i] = false; Claimed[i] = false; Scores[i] = 0f; Advantage[i] = 0f;
                float routeP = lastOffset + fromLast[i];
                float routeT = selfOffset + fromSelf[i];
                if (routeP > Reach || float.IsPositiveInfinity(routeT)) continue;   // unreachable or impossible
                if (banned != null && banned[i]) continue;

                Vector3 np = w.Position(i);
                float prox = Mathf.Exp(-(np - pred).sqrMagnitude * inv2Sigma2);

                Vector3 off = np - last.Position; off.y = 0f;
                float offLen = off.magnitude;
                float cos = dir != Vector3.zero && offLen > 0.5f ? Vector3.Dot(dir, off / offLen) : 0f;
                float dirTerm = dirTrust * Mathf.Max(0f, cos);

                float etaPlayer = Mathf.Max(0f, routeP / etaSpeed - age);
                float etaTracker = Eta(routeT, vT, selfSpeed, s);
                EtaPlayer[i] = etaPlayer; EtaTracker[i] = etaTracker;
                float adv = Mathf.Clamp((etaPlayer - etaTracker) / s.advScale, -1f, 1f);
                if (s.gateAdvantage && adv > 0f) adv *= dirTerm;   // being first only counts on the player's way
                if (s.advMode == AdvantageMode.Saturated) adv = Mathf.Min(adv, s.advCap);

                bool feasible = adv > 0f && (s.advMode != AdvantageMode.ConstraintOnLine || cos >= s.onLineCos);
                float score = s.wProx * prox + s.wDir * dirTerm + (constraint ? 0f : s.wAdv * adv) + s.wHeat * heat.Normalised(i);

                IsCandidate[i] = true; Scores[i] = score; Advantage[i] = adv; Claimed[i] = constraint ? feasible : adv > 0f;
                CandidateCount++;
                if (score > BestScore) { Second = Best; SecondScore = BestScore; Best = i; BestScore = score; }
                else if (score > SecondScore) { Second = i; SecondScore = score; }
                if (constraint && feasible)
                {
                    fCount++;
                    if (score > fBestScore) { fSecond = fBest; fSecondScore = fBestScore; fBest = i; fBestScore = score; }
                    else if (score > fSecondScore) { fSecond = i; fSecondScore = score; }
                }
            }

            Fallback = constraint && fBest < 0;
            if (constraint && fBest >= 0)
            {
                Best = fBest; Second = fSecond; BestScore = fBestScore; SecondScore = fSecondScore; CandidateCount = fCount;
            }
            else if (Fallback && Best >= 0) Claimed[Best] = false;
            if (Best >= 0)
                Margin = CandidateCount == 1 ? 1f
                       : BestScore > 1e-4f ? Mathf.Clamp01((BestScore - SecondScore) / BestScore) : 0f;
            return Best >= 0;
        }

        static float Eta(float route, float speed, float selfSpeed, TrackerSettings s) =>
            s.etaFromGait ? TrackerMotion.Eta(route, speed, selfSpeed, s) : route / s.runSpeed;
    }
}
