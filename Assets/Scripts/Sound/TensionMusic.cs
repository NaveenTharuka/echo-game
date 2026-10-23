using Echo.AI;
using UnityEngine;

namespace Echo.Sound
{
    /// Tension music without a mixer (guide §5.7 idea): a calm bed and a hunt layer crossfaded by the highest agent
    /// AlertLevel. Reading AI state here is allowed - it is presentation, not perception.
    /// Swap for the mixer-snapshot TensionDirector once EchoMixer exists.
    public class TensionMusic : MonoBehaviour
    {
        public AudioClip calmLoop, huntLoop;
        [Range(0f, 1f)] public float calmVolume = 0.25f, huntVolume = 0.45f;
        [Tooltip("Seconds to fade between layers.")]
        public float fade = 2.5f;
        AudioSource calm, hunt;

        void Awake()
        {
            calm = Layer(calmLoop); hunt = Layer(huntLoop);
            if (calm) calm.volume = calmVolume;
        }

        AudioSource Layer(AudioClip clip)
        {
            if (!clip) return null;
            var s = gameObject.AddComponent<AudioSource>();
            s.clip = clip; s.loop = true; s.spatialBlend = 0f; s.volume = 0f; s.playOnAwake = false; s.reverbZoneMix = 0f;
            s.Play();
            return s;
        }

        void Update()
        {
            float alert = 0f;
            foreach (var a in AgentRegistry.All) if (a.IsAlive) alert = Mathf.Max(alert, a.AlertLevel);
            float h = Mathf.InverseLerp(0.3f, 0.8f, alert);   // suspicious starts it, hunting is full
            float step = Time.deltaTime / Mathf.Max(0.01f, fade);
            if (hunt) hunt.volume = Mathf.MoveTowards(hunt.volume, h * huntVolume, step * huntVolume);
            if (calm) calm.volume = Mathf.MoveTowards(calm.volume, (1f - 0.6f * h) * calmVolume, step * calmVolume);
        }
    }
}
