using UnityEngine;

namespace Echo.AI.Tracker
{
    /// Weighted least-squares fit of position as a linear function of time, on the XZ plane, shrunk towards
    /// zero by its own uncertainty (MAP estimate with a Gaussian prior v ~ N(0, velocityPrior^2)):
    ///   w_i    = cred_i * exp(-(now - t_i) / tau)
    ///   lambda = sigma^2 / velocityPrior^2,  sigma^2 = weighted mean of err_i^2 (position noise)
    ///   v      = sum w_i (t_i - t_bar)(p_i - p_bar) / (sum w_i (t_i - t_bar)^2 + lambda),   |v| capped at v_max
    /// Few sounds over a short span with metres of error (sum w dt^2 small, sigma large) give a slow, humble
    /// velocity instead of a 6.5 m/s guess that throws the prediction off the map; a long clean trail is
    /// unaffected (lambda << sum w dt^2).
    /// Recent, credible sounds dominate, so a player who doubles back flips v within a few sounds.
    /// The weighted RMS residual of the fitted line measures how well "straight line at constant
    /// speed" explains the trail (used as the `fit` term of the confidence).
    /// Speeds for ETA_player (the shrinkage towards 0 is right for the heading and prediction, but makes the player's
    /// ETA too long: Scenario a x10, runner ETA -4 s mean error):
    ///   rawSpeed    = |unshrunk WLS velocity|, capped (unbiased, noisy)
    ///   movingSpeed = (rawSpeed * den + lambda * velocityPrior) / (den + lambda): the same shrinkage, but towards a
    ///                 moving player's typical speed instead of 0 - a player making step noise is not standing still.
    public static class VelocityEstimator
    {
        public static bool TryEstimate(TrackerMemory m, float now, TrackerSettings s, out Vector3 v, out float rms) =>
            TryEstimate(m, now, s, out v, out rms, out _, out _);

        public static bool TryEstimate(TrackerMemory m, float now, TrackerSettings s, out Vector3 v, out float rms,
                                       out float rawSpeed, out float movingSpeed)
        {
            v = Vector3.zero; rms = 0f; rawSpeed = movingSpeed = 0f;
            int n = m.Count;
            if (n < 2 || m.Last.Time - m[0].Time < s.minSpan) return false;

            float sw = 0f, st = 0f, se = 0f; Vector3 sp = Vector3.zero;
            for (int i = 0; i < n; i++)
            {
                var o = m[i];
                float w = Weight(o, now, s);
                sw += w; st += w * o.Time; sp += w * Flat(o.Position); se += w * o.Error * o.Error;
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
            float lambda = s.velocityPrior > 0f ? (se / sw) / (s.velocityPrior * s.velocityPrior) : 0f;
            v = Vector3.ClampMagnitude(num / (den + lambda), s.maxPlayerSpeed);
            float raw = (num / den).magnitude;
            rawSpeed = Mathf.Min(raw, s.maxPlayerSpeed);
            movingSpeed = Mathf.Min((raw * den + lambda * s.velocityPrior) / (den + lambda), s.maxPlayerSpeed);

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
