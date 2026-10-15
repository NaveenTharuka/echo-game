using UnityEngine;

namespace Echo.AI.Tracker
{
    /// Weighted least-squares fit of position as a linear function of time, on the XZ plane:
    ///   w_i = cred_i * exp(-(now - t_i) / tau)
    ///   v   = sum w_i (t_i - t_bar)(p_i - p_bar) / sum w_i (t_i - t_bar)^2,   |v| capped at v_max
    /// Recent, credible sounds dominate, so a player who doubles back flips v within a few sounds.
    /// The weighted RMS residual of the fitted line measures how well "straight line at constant
    /// speed" explains the trail (used as the `fit` term of the confidence).
    public static class VelocityEstimator
    {
        public static bool TryEstimate(TrackerMemory m, float now, TrackerSettings s, out Vector3 v, out float rms)
        {
            v = Vector3.zero; rms = 0f;
            int n = m.Count;
            if (n < 2 || m.Last.Time - m[0].Time < s.minSpan) return false;

            float sw = 0f, st = 0f; Vector3 sp = Vector3.zero;
            for (int i = 0; i < n; i++)
            {
                var o = m[i];
                float w = Weight(o, now, s);
                sw += w; st += w * o.Time; sp += w * Flat(o.Position);
            }
            if (sw <= 1e-6f) return false;
            float tBar = st / sw; Vector3 pBar = sp / sw;

            float den = 0f; Vector3 num = Vector3.zero;
            for (int i = 0; i < n; i++)
            {
                var o = m[i];
                float w = Weight(o, now, s), dt = o.Time - tBar;
                den += w * dt * dt; num += w * dt * (Flat(o.Position) - pBar);
            }
            if (den <= 1e-6f) return false;
            v = Vector3.ClampMagnitude(num / den, s.maxPlayerSpeed);

            float sr = 0f;
            for (int i = 0; i < n; i++)
            {
                var o = m[i];
                Vector3 r = Flat(o.Position) - (pBar + v * (o.Time - tBar));
                sr += Weight(o, now, s) * r.sqrMagnitude;
            }
            rms = Mathf.Sqrt(sr / sw);
            return true;
        }

        static float Weight(in TrackerObservation o, float now, TrackerSettings s) =>
            o.Credibility * Mathf.Exp(-(now - o.Time) / s.tau);

        static Vector3 Flat(Vector3 p) => new(p.x, 0f, p.z);
    }
}
