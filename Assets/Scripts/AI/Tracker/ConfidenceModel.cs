using UnityEngine;

namespace Echo.AI.Tracker
{
    public enum ConfidenceBand { Search = 0, Walk = 1, Run = 2 }

    public struct ConfidenceParts
    {
        public float Freshness, Evidence, Fit, Margin, Value;
    }

    /// How much the Tracker trusts its prediction:
    ///   C = freshness * (0.4 evidence + 0.3 fit + 0.3 margin)
    ///   freshness = exp(-age / 8)                  stale evidence is worth less, whatever its quality
    ///   evidence  = min(1, (credibleObs - 1) / 3)  1 sound = no direction, 4+ = a trail
    ///   fit       = exp(-weighted RMS residual / 3 m)   the trail really is a line
    ///   margin    = (best - second) / best         the scorer had one clear answer
    /// Freshness multiplies (it gates everything); the rest add, so one weak factor lowers C but
    /// does not zero it.
    public static class ConfidenceModel
    {
        public static ConfidenceParts Compute(float age, int credibleObs, bool hasFit, float rms, float margin, TrackerSettings s)
        {
            var c = new ConfidenceParts
            {
                Freshness = Mathf.Exp(-Mathf.Max(0f, age) / s.freshnessTau),
                Evidence = Mathf.Clamp01((credibleObs - 1) / s.evidenceObs),
                Fit = hasFit ? Mathf.Exp(-rms / s.fitScale) : 0f,
                Margin = Mathf.Clamp01(margin)
            };
            c.Value = c.Freshness * (0.4f * c.Evidence + 0.3f * c.Fit + 0.3f * c.Margin);
            return c;
        }

        static float Threshold(ConfidenceBand b, TrackerSettings s) =>
            b == ConfidenceBand.Run ? s.runThreshold : s.walkThreshold;

        /// Band without hysteresis (first decision on a new trail).
        public static ConfidenceBand Classify(float c, TrackerSettings s) =>
            c >= s.runThreshold ? ConfidenceBand.Run : c >= s.walkThreshold ? ConfidenceBand.Walk : ConfidenceBand.Search;

        /// Band with +/- hysteresis around each threshold, so C hovering near 0.55 does not
        /// flip run/walk (and re-plan) every tick.
        public static ConfidenceBand Classify(float c, ConfidenceBand current, TrackerSettings s)
        {
            var b = current;
            while (b < ConfidenceBand.Run && c >= Threshold(b + 1, s) + s.hysteresis) b++;
            while (b > ConfidenceBand.Search && c < Threshold(b, s) - s.hysteresis) b--;
            return b;
        }
    }
}
