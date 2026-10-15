using UnityEngine;

namespace Echo.Sound
{
    /// Muffles a 3D source when a wall is between it and the listener's ears (guide §5.5). For the player's ears only.
    [RequireComponent(typeof(AudioSource), typeof(AudioLowPassFilter))]
    public class AudioOcclusion : MonoBehaviour
    {
        public float openCutoff = 22000f, blockedCutoff = 1200f, blockedVolume = 0.6f;
        AudioLowPassFilter lowPass; AudioSource source; float baseVolume, nextCheck; bool blocked;
        static Transform ears;
        static int mask = -1;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() { ears = null; mask = -1; }

        void Awake()
        {
            lowPass = GetComponent<AudioLowPassFilter>(); source = GetComponent<AudioSource>();
            baseVolume = source.volume;
        }

        void Update()
        {
            if (ears == null && Camera.main) ears = Camera.main.transform;
            if (ears == null) return;
            if (mask < 0) mask = LayerMask.GetMask("Environment");
            if (Time.time >= nextCheck)
            {
                nextCheck = Time.time + 0.1f;   // 10 Hz is plenty
                blocked = Physics.Linecast(transform.position, ears.position, mask, QueryTriggerInteraction.Ignore);
            }
            float k = Time.deltaTime * 6f;
            lowPass.cutoffFrequency = Mathf.Lerp(lowPass.cutoffFrequency, blocked ? blockedCutoff : openCutoff, k);
            source.volume = Mathf.Lerp(source.volume, blocked ? baseVolume * blockedVolume : baseVolume, k);
        }
    }
}
