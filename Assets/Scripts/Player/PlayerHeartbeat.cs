using Echo.Sound;
using StarterAssets;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Echo.Player
{
    /// Your own heart, in your head (presentation only). Silent when calm; with fear (a hunting agent close by, the
    /// camera's Fear value) or after a hard sprint it fades in and speeds up: 66 -> 100 -> 142 bpm, crossfading three
    /// recorded loops (Heartbeat_Calm/Fast/Pound_Loop from the SoundLibrary). Stops when you die.
    /// Auto-attached to the player on scene load like PlayerExertion (no scene edits).
    public class PlayerHeartbeat : MonoBehaviour
    {
        public static bool AutoCreate = true;

        [Range(0f, 1f)] public float volume = 0.75f;
        [Tooltip("Heart level where it starts to be heard / is at full volume.")]
        public Vector2 audible = new(0.15f, 0.45f);
        [Tooltip("Level where the fast / pounding loops take over.")]
        public Vector2 tiers = new(0.45f, 0.75f);
        [Tooltip("How fast the heart settles (1/s). It calms down much slower than it jumps.")]
        public float rise = 1.5f, fall = 0.18f;

        public float Level { get; private set; }

        readonly AudioSource[] loops = new AudioSource[3];
        PlayerCameraMotion cameraMotion;
        PlayerExertion exertion;
        PlayerDeath death;
        bool dead, built;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Boot()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            SceneManager.sceneLoaded += OnSceneLoaded;
            Attach();
        }

        static void OnSceneLoaded(Scene scene, LoadSceneMode mode) => Attach();

        static void Attach()
        {
            if (!AutoCreate) return;
            var player = FindFirstObjectByType<FirstPersonController>();
            if (player != null && !player.TryGetComponent(out PlayerHeartbeat _))
                player.gameObject.AddComponent<PlayerHeartbeat>();
        }

        void Awake()
        {
            TryGetComponent(out cameraMotion); TryGetComponent(out exertion); TryGetComponent(out death);
        }

        void OnEnable() { if (death != null) death.Died += OnDied; }
        void OnDisable() { if (death != null) death.Died -= OnDied; }

        void OnDied()
        {
            dead = true;
            foreach (var l in loops) if (l != null) l.Stop();
        }

        void Build()
        {
            built = true;
            string[] names = { "Heartbeat_Calm_Loop", "Heartbeat_Fast_Loop", "Heartbeat_Pound_Loop" };
            for (int i = 0; i < 3; i++)
            {
                var clips = SoundLibrary.ActiveSet(names[i]);
                if (clips.Length == 0) continue;
                var s = gameObject.AddComponent<AudioSource>();
                s.clip = clips[0]; s.loop = true; s.spatialBlend = 0f; s.volume = 0f;
                s.playOnAwake = false; s.reverbZoneMix = 0f;   // inside your chest, not in the room
                s.priority = 32;
                s.Play();
                loops[i] = s;
            }
        }

        void Update()
        {
            if (dead) return;
            if (!built)
            {
                if (cameraMotion == null) TryGetComponent(out cameraMotion);
                if (exertion == null) TryGetComponent(out exertion);
                if (SoundLibrary.Active == null) return;
                Build();
            }
            float fear = cameraMotion != null ? cameraMotion.Fear : 0f;
            float effort = exertion != null ? Mathf.Clamp01((exertion.Exertion - 0.45f) * 1.4f) : 0f;
            float want = Mathf.Max(fear, effort);
            Level = Mathf.MoveTowards(Level, want, Time.deltaTime * (want > Level ? rise : fall));

            float loud = volume * Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(audible.x, audible.y, Level));
            float fast = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(tiers.x - 0.08f, tiers.x + 0.08f, Level));
            float pound = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(tiers.y - 0.08f, tiers.y + 0.08f, Level));
            Set(0, loud * (1f - fast));
            Set(1, loud * fast * (1f - pound));
            Set(2, loud * pound);
        }

        void Set(int i, float v) { if (loops[i] != null) loops[i].volume = v; }
    }
}
