using Echo.Sound;
using UnityEngine;
using UnityEngine.AI;

namespace Echo.AI
{
    /// The agent's own footsteps (guide §5.6): one AgentBody sound per stride travelled. The player hears them,
    /// other agents ignore them (Hearing drops SoundSource.AgentBody). Distance-based like PlayerNoise, so it needs
    /// no Animation Events and stays in step with any speed.
    /// With `clips` + `source` set, the agent plays its own steps (heavier, muffled behind walls via AudioOcclusion);
    /// otherwise the shared SoundLibrary Footstep clips are used.
    [RequireComponent(typeof(NavMeshAgent))]
    public class AgentFootsteps : MonoBehaviour
    {
        public float stride = 1.4f;
        public float walkIntensity = 0.15f, runIntensity = 0.3f;
        [Tooltip("m/s. Faster than this counts as running.")]
        public float runSpeed = 3.5f;

        [Header("Own audio (optional)")]
        public AudioSource source;
        public AudioClip[] clips;
        public Vector2 pitch = new(0.8f, 0.95f);
        [Range(0f, 1f)] public float walkVolume = 0.6f, runVolume = 1f;

        NavMeshAgent nav; float travelled; int last = -1;

        void Awake() => nav = GetComponent<NavMeshAgent>();

        void Update()
        {
            float v = nav.velocity.magnitude;
            if (v < 0.3f) return;
            travelled += v * Time.deltaTime;
            if (travelled < stride) return;
            travelled = 0f;
            bool running = v > runSpeed;
            bool own = source != null && clips != null && clips.Length > 0;
            if (own) PlayOwn(running);
            Noise.Make(SoundType.Footstep, transform.position, running ? runIntensity : walkIntensity,
                       SoundSource.AgentBody, gameObject, playAudio: !own);
        }

        void PlayOwn(bool running)
        {
            int i = Random.Range(0, clips.Length);
            if (clips.Length > 1 && i == last) i = (i + 1) % clips.Length;   // never the same step twice in a row
            last = i;
            source.pitch = Random.Range(pitch.x, pitch.y);
            source.PlayOneShot(clips[i], running ? runVolume : walkVolume);
        }
    }
}
