using Echo.Navigation;
using UnityEngine;

namespace Echo.Sound
{
    /// Propagation model (guide §4.4): sound travels through corridors, not through walls.
    /// R = intensity x 45 m x sensitivity, loudness = (1 - d/R)^2.
    public static class Acoustics
    {
        public const float MetresPerIntensity = 45f;
        static int occluderMask = -1;
        static int OccluderMask => occluderMask >= 0 ? occluderMask
                                 : (occluderMask = LayerMask.GetMask("Environment"));

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() => occluderMask = -1;

        public static float EffectiveDistance(Vector3 source, Vector3 ear)
        {
            float straight = Vector3.Distance(source, ear);
            if (!Physics.Linecast(source + Vector3.up * 0.3f, ear, OccluderMask,
                                  QueryTriggerInteraction.Ignore))
                return straight;

            var g = NavGraph.Instance;
            if (g == null || g.Count == 0) return straight * 2f;
            int a = g.Nearest(source), b = g.Nearest(ear);
            float route = Vector3.Distance(source, g.Position(a))
                        + g.SoundDistance(a, b)
                        + Vector3.Distance(g.Position(b), ear);
            return Mathf.Max(route, straight * 1.15f);
        }

        public static float Loudness(float intensity, float sensitivity, float effectiveDistance)
        {
            float radius = intensity * MetresPerIntensity * sensitivity;
            if (radius <= 0f || effectiveDistance >= radius) return 0f;
            float x = 1f - effectiveDistance / radius;
            return x * x;
        }
    }
}
