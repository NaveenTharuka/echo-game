using UnityEngine;

namespace Echo.AI.Tracker
{
    /// Weighted least-squares fit of position as a linear function of time, on the XZ plane, shrunk towards
    /// zero by its own uncertainty (MAP estimate with a Gaussian prior v ~ N(0, velocityPrior^2)).
    /// With errorWeighting (inverse-variance weights; tested 21 Oct, not adopted - see TrackerSettings):
    ///   u_i    = cred_i * exp(-(now - t_i) / tau) / (err_i^2 + errorFloor^2)
    ///   v      = sum u_i (t_i - t_bar)(p_i - p_bar) / (sum u_i (t_i - t_bar)^2 + 1 / velocityPrior^2),   |v| capped at v_max
    /// A 6 m-error sound counts ~1/9 of a 2 m one. When every error is the same this is exactly the shipped
    /// estimator below (u = w / sigma^2); it only differs on trails that mix near and far sounds.
    /// Without (phase 3, shipped):
    ///   w_i    = cred_i * exp(-(now - t_i) / tau)
    ///   lambda = sigma^2 / velocityPrior^2,  sigma^2 = weighted mean of err_i^2 (position noise)
    ///   v      = sum w_i (t_i - t_bar)(p_i - p_bar) / (sum w_i (t_i - t_bar)^2 + lambda)
    /// Few sounds over a short span with metres of error give a slow, humble velocity instead of a 6.5 m/s guess
    /// that throws the prediction off the map; a long clean trail is unaffected (lambda << sum w dt^2).
    /// Recent, credible sounds dominate, so a player who doubles back flips v within a few sounds.
    /// The weighted RMS residual of the fitted line measures how well "straight line at constant speed" explains
    /// the trail (the `fit` term of the confidence); noiseRms is the RMS residual the hearing error alone explains
    /// (a percept is uniform in a disc of radius err: E|e|^2 = err^2 / 2), so the confidence can count only the excess.
    /// Speeds for ETA_player (the shrinkage towards 0 is right for the heading and prediction, but makes the player's
    /// ETA too long: Scenario a x10, runner ETA -4 s mean error):
    ///   rawSpeed    = |unshrunk WLS velocity|, capped (unbiased, noisy)
    ///   movingSpeed = (rawSpeed * den + lambda * velocityPrior) / (den + lambda): the same shrinkage, but towards a
    ///                 moving player's typical speed instead of 0 - a player making step noise is not standing still.
    /// Anchors (the trail position at the latest sound's time, per TrackerAnchor):
    ///   anchorLine   = pBar + v_raw * (t_last - t_bar)    unshrunk line: unbiased, but noisy with few sounds
    ///   anchorShrunk = pBar + v * (t_last - t_bar)        shrunk line: stable, but lags a moving runner
    public static class VelocityEstimator
    {
        public static bool TryEstimate(TrackerMemory m, float now, TrackerSettings s, out Vector3 v, out float rms) =>
            TryEstimate(m, now, s, out v, out rms, out _, out _, out _, out _, out _);

        public static bool TryEstimate(TrackerMemory m, float now, TrackerSettings s, out Vector3 v, out float rms,
                                       out float rawSpeed, out float movingSpeed, out Vector3 anchorLine, out Vector3 anchorShrunk,
                                       out float noiseRms)
        {
            v = Vector3.zero; rms = noiseRms = 0f; rawSpeed = movingSpeed = 0f; anchorLine = anchorShrunk = default;
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
            // Inverse-variance weights already carry 1/sigma^2, so the prior term is 1/prior^2 alone.
            float lambda = s.velocityPrior <= 0f ? 0f
                         : s.errorWeighting ? 1f / (s.velocityPrior * s.velocityPrior)
                         : (se / sw) / (s.velocityPrior * s.velocityPrior);
            v = Vector3.ClampMagnitude(num / (den + lambda), s.maxPlayerSpeed);
            float raw = (num / den).magnitude;
            rawSpeed = Mathf.Min(raw, s.maxPlayerSpeed);
            movingSpeed = Mathf.Min((raw * den + lambda * s.velocityPrior) / (den + lambda), s.maxPlayerSpeed);

            float sr = 0f, sn = 0f;
            for (int i = 0; i < n; i++)
            {
                var o = m[i];
                float w = Weight(o, now, s);
                Vector3 r = Flat(o.Position) - (pBar + v * (o.Time - tBar));
                sr += w * r.sqrMagnitude; sn += w * 0.5f * o.Error * o.Error;
            }
            rms = Mathf.Sqrt(sr / sw);
            noiseRms = Mathf.Sqrt(sn / sw);
            anchorLine = pBar + Vector3.ClampMagnitude(num / den, s.maxPlayerSpeed) * (m.Last.Time - tBar);
            anchorShrunk = pBar + v * (m.Last.Time - tBar);
            return true;
        }

        static float Weight(in TrackerObservation o, float now, TrackerSettings s)
        {
            float w = o.Credibility * Mathf.Exp(-(now - o.Time) / s.tau);
            return s.errorWeighting ? w / (o.Error * o.Error + s.errorFloor * s.errorFloor) : w;
        }

        static Vector3 Flat(Vector3 p) => new(p.x, 0f, p.z);
    }
}
