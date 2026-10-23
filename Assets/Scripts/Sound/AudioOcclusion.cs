using Echo.Navigation;
using UnityEngine;

namespace Echo.Sound
{
    /// How a 3D source sounds from where the player's ears are (guide §5.5, extended). Presentation only - the AI hears
    /// through Acoustics, never through this.
    ///   * Air:     highs fade with distance (a far gunshot is a dull thump, a near one cracks).
    ///   * Walls:   one wall muffles, two or more almost bury it.
    ///   * Portals: if the sound can travel around (a doorway, a corridor) on a route not much longer than the straight
    ///              line, it leaks through brighter and louder than through concrete.
    ///   * Room:    more reverb send the farther (and the more hidden) the source, so distance reads as space.
    /// Works for long-lived sources (agent voice/feet) and for AudioService's pooled one-shots (SetBaseVolume + Snap).
    [RequireComponent(typeof(AudioSource), typeof(AudioLowPassFilter))]
    public class AudioOcclusion : MonoBehaviour
    {
        [Tooltip("Cutoff with nothing in the way (Hz), before air absorption.")]
        public float openCutoff = 22000f;
        [Tooltip("Cutoff / volume through one wall.")]
        public float blockedCutoff = 1200f, blockedVolume = 0.6f;
        [Tooltip("Cutoff / volume through two or more walls.")]
        public float buriedCutoff = 550f, buriedVolume = 0.32f;
        [Tooltip("Air absorption: full brightness up to nearDistance, airCutoff at farDistance and beyond.")]
        public float nearDistance = 6f, farDistance = 55f, airCutoff = 2400f;
        [Tooltip("A route around the walls at most this many times the straight line counts as a leak (doorway).")]
        public float leakRouteRatio = 1.9f;
        public float leakCutoff = 3200f;
        [Tooltip("Reverb send (reverbZoneMix) close by .. far away / hidden.")]
        public Vector2 reverbSend = new(0.55f, 1.1f);
        [Tooltip("Keep tracking while silent (PlayOneShot users like feet). AudioService's pool turns this off.")]
        public bool trackWhenSilent = true;

        AudioLowPassFilter lowPass; AudioSource source;
        float baseVolume, nextCheck, targetCutoff = 22000f, targetGain = 1f, targetSend = 0.8f;
        int sourceNode = -1; Vector3 sourceNodeAt = new(1e9f, 0f, 0f);

        static Transform ears;
        static int mask = -1, earNode = -1, earNodeFrame = -1;
        static Vector3 earNodeAt = new(1e9f, 0f, 0f);
        static readonly RaycastHit[] hits = new RaycastHit[8];

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() { ears = null; mask = -1; earNode = -1; earNodeFrame = -1; earNodeAt = new(1e9f, 0f, 0f); }

        void Awake()
        {
            lowPass = GetComponent<AudioLowPassFilter>(); source = GetComponent<AudioSource>();
            baseVolume = source.volume;
        }

        /// Pooled sources: the volume this play should have before occlusion.
        public void SetBaseVolume(float v) { baseVolume = v; source.volume = v * targetGain; }

        /// Evaluate now and jump to the result (a one-shot must not fade in from the previous sound's state).
        public void Snap()
        {
            if (!Evaluate()) return;
            lowPass.cutoffFrequency = targetCutoff;
            source.volume = baseVolume * targetGain;
            source.reverbZoneMix = targetSend;
            nextCheck = Time.time + Random.Range(0.08f, 0.12f);
        }

        void Update()
        {
            if (!trackWhenSilent && !source.isPlaying) return;
            if (Time.time >= nextCheck)
            {
                nextCheck = Time.time + 0.1f;   // 10 Hz is plenty
                if (!Evaluate()) return;
            }
            float k = 1f - Mathf.Exp(-Time.deltaTime * 8f);
            lowPass.cutoffFrequency = Mathf.Lerp(lowPass.cutoffFrequency, targetCutoff, k);
            source.volume = Mathf.Lerp(source.volume, baseVolume * targetGain, k);
            source.reverbZoneMix = Mathf.Lerp(source.reverbZoneMix, targetSend, k);
        }

        bool Evaluate()
        {
            if (ears == null && Camera.main)
            {
                ears = Camera.main.transform;
                earNode = -1; earNodeAt = new(1e9f, 0f, 0f);       // new scene / camera: the old node index is stale
            }
            if (ears == null) return false;
            if (mask < 0) mask = LayerMask.GetMask("Environment");

            Vector3 from = transform.position, to = ears.position;
            Vector3 d = to - from; float straight = d.magnitude;

            // Air: brightness falls off with distance (smooth, so footsteps darken as they walk away).
            float far01 = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(nearDistance, farDistance, straight));
            float air = Mathf.Lerp(openCutoff, airCutoff, far01);

            // Walls between: count Environment colliders on the straight line.
            int walls = straight > 0.05f
                ? Physics.RaycastNonAlloc(from, d / straight, hits, straight, mask, QueryTriggerInteraction.Ignore) : 0;
            float cutoff = air, gain = 1f;
            if (walls > 0)
            {
                cutoff = walls == 1 ? blockedCutoff : buriedCutoff;
                gain = walls == 1 ? blockedVolume : buriedVolume;
                // Around the walls: a doorway route that is not much longer than the wall path leaks through.
                float route = RouteDistance(from, to);
                if (route < straight * leakRouteRatio && route < source.maxDistance)
                {
                    float leak = Mathf.Clamp01((straight * leakRouteRatio - route) / (straight * (leakRouteRatio - 1f) + 0.01f));
                    float routeGain = Mathf.Clamp(straight / Mathf.Max(route, 0.1f), 0.35f, 1f) * 0.9f;
                    cutoff = Mathf.Lerp(cutoff, Mathf.Min(leakCutoff, air), leak);
                    gain = Mathf.Lerp(gain, routeGain, leak);
                }
                cutoff = Mathf.Min(cutoff, air);
            }
            targetCutoff = cutoff;
            targetGain = gain;
            targetSend = Mathf.Lerp(reverbSend.x, reverbSend.y, Mathf.Max(far01, walls > 0 ? 0.6f : 0f));
            return true;
        }

        /// Sound route length via the NavGraph (corridors, doorways), or +inf without a graph.
        float RouteDistance(Vector3 from, Vector3 to)
        {
            var g = NavGraph.Instance;
            if (g == null || g.Count == 0) return float.PositiveInfinity;
            if (earNodeFrame != Time.frameCount && (to - earNodeAt).sqrMagnitude > 1f)
            {
                earNode = g.Nearest(to); earNodeAt = to; earNodeFrame = Time.frameCount;
            }
            if ((from - sourceNodeAt).sqrMagnitude > 1f) { sourceNode = g.Nearest(from); sourceNodeAt = from; }
            if (earNode < 0 || sourceNode < 0 || earNode >= g.Count || sourceNode >= g.Count) return float.PositiveInfinity;
            return Vector3.Distance(from, g.Position(sourceNode)) + g.SoundDistance(sourceNode, earNode)
                 + Vector3.Distance(g.Position(earNode), to);
        }
    }
}
