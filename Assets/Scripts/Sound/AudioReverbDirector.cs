using Echo.Navigation;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Echo.Sound
{
    /// Room reverb that follows the player's ears. One AudioReverbZone rides on the AudioListener (so the listener is
    /// always inside it) and is re-tuned to the ZoneVolume the listener stands in: decay from the room's size
    /// (Sabine: RT60 = 0.161 V / (S a), hard concrete with some clutter), louder and later reflections in big halls,
    /// tight and dry in small rooms. Changes glide over ~0.7 s when you walk from a corridor into the warehouse.
    /// 3D sources send to it through reverbZoneMix (AudioOcclusion: more send far away / behind walls).
    /// 2D music, ambience and UI get no reverb (they'd be "in the room" twice).
    /// Auto-created in every scene with an AudioListener (no scene edits), like the player's add-ons.
    public class AudioReverbDirector : MonoBehaviour
    {
        public static bool AutoCreate = true;

        [Tooltip("Average absorption of the surfaces (0.05 bare concrete .. 0.3 furnished).")]
        [Range(0.03f, 0.5f)] public float absorption = 0.18f;
        public Vector2 decayRange = new(0.45f, 3.2f);
        public float maxRoomHeight = 8f;
        [Tooltip("Room used outside every zone and before the first zone is found.")]
        public Vector3 defaultRoomSize = new(8f, 3.5f, 20f);
        public float glide = 1.5f;

        public string CurrentZone { get; private set; }
        public float CurrentDecay => cur.decay;

        struct Profile { public float room, reflections, reverb, decay, reflDelay, revDelay; }

        AudioReverbZone zone;
        Transform listener;
        Profile cur, target;
        float nextCheck;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Bootstrap()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            SceneManager.sceneLoaded += OnSceneLoaded;
            Ensure();
        }

        static void OnSceneLoaded(Scene s, LoadSceneMode m) => Ensure();

        static void Ensure()
        {
            if (!AutoCreate || FindFirstObjectByType<AudioReverbDirector>() != null) return;
            if (FindFirstObjectByType<AudioListener>() == null) return;
            new GameObject("Audio Reverb Director").AddComponent<AudioReverbDirector>();
        }

        void Start()
        {
            target = cur = ProfileFor(defaultRoomSize, null);
            KeepFlatSourcesDry();
            Invoke(nameof(KeepFlatSourcesDry), 1f);                    // sources other scripts create in their Start
            Invoke(nameof(KeepFlatSourcesDry), 3f);
        }

        /// 2D sources (music, ambience beds, UI) stay dry. Sources that want a little room on purpose set a value
        /// other than the default 1 (e.g. the player's breath) and are left alone.
        void KeepFlatSourcesDry()
        {
            foreach (var s in FindObjectsByType<AudioSource>(FindObjectsSortMode.None))
                if (s.spatialBlend < 0.5f && Mathf.Approximately(s.reverbZoneMix, 1f)) s.reverbZoneMix = 0f;
        }

        void Update()
        {
            if (listener == null)
            {
                var l = FindFirstObjectByType<AudioListener>();
                if (l == null) return;
                listener = l.transform;
                var go = new GameObject("Room Reverb");
                go.transform.SetParent(listener, false);
                zone = go.AddComponent<AudioReverbZone>();
                zone.reverbPreset = AudioReverbPreset.User;
                zone.minDistance = 1000f; zone.maxDistance = 1001f;   // the listener is always fully inside
                zone.roomHF = -550; zone.roomLF = 0;
                zone.decayHFRatio = 0.62f;                             // highs die first (concrete + air)
                zone.diffusion = 85f; zone.density = 100f;
                zone.HFReference = 5000f; zone.LFReference = 250f;
                Apply(cur);
            }
            if (Time.time >= nextCheck)
            {
                nextCheck = Time.time + 0.25f;
                var z = ZoneVolume.At(listener.position);
                if (z != null && z.zoneId != CurrentZone)
                {
                    CurrentZone = z.zoneId;
                    target = ProfileFor(z.Bounds.size, z.zoneId);
                }
            }
            float k = 1f - Mathf.Exp(-Time.deltaTime * glide);
            cur.room = Mathf.Lerp(cur.room, target.room, k);
            cur.reflections = Mathf.Lerp(cur.reflections, target.reflections, k);
            cur.reverb = Mathf.Lerp(cur.reverb, target.reverb, k);
            cur.decay = Mathf.Lerp(cur.decay, target.decay, k);
            cur.reflDelay = Mathf.Lerp(cur.reflDelay, target.reflDelay, k);
            cur.revDelay = Mathf.Lerp(cur.revDelay, target.revDelay, k);
            Apply(cur);
        }

        void Apply(in Profile p)
        {
            if (zone == null) return;
            zone.room = Mathf.RoundToInt(p.room);
            zone.reflections = Mathf.RoundToInt(p.reflections);
            zone.reverb = Mathf.RoundToInt(p.reverb);
            zone.decayTime = p.decay;
            zone.reflectionsDelay = p.reflDelay;
            zone.reverbDelay = p.revDelay;
        }

        Profile ProfileFor(Vector3 size, string id)
        {
            // Zone boxes are drawn tall (they span stairs and floor levels), so the height used is capped: the room
            // you hear is the space around you, not the whole shaft.
            float x = Mathf.Max(1.5f, size.x), y = Mathf.Clamp(size.y, 2.5f, maxRoomHeight), z = Mathf.Max(1.5f, size.z);
            float v = x * y * z, s = 2f * (x * y + y * z + x * z);
            float rt = 0.161f * v / (s * absorption);
            float m = Mathf.Pow(v, 1f / 3f);                           // a room's "typical" dimension
            string n = id ?? "";
            // A few names carry extra character (tiles and furniture in the lab, steel in the warehouse).
            if (n.Contains("Warehouse") || n.Contains("Bay")) rt *= 1.2f;
            if (n.Contains("Stair")) rt *= 1.1f;
            if (n.Contains("Lab") || n.Contains("Containment")) rt *= 0.8f;
            rt = Mathf.Clamp(rt, decayRange.x, decayRange.y);
            float big = Mathf.InverseLerp(4f, 26f, m);
            return new Profile
            {
                decay = rt,
                room = Mathf.Lerp(-1500f, -650f, big),
                reflections = Mathf.Lerp(-500f, -1300f, big),          // small rooms: strong, early slap
                reverb = Mathf.Lerp(-900f, 150f, big),
                reflDelay = Mathf.Clamp(m / 343f * 0.6f, 0.004f, 0.08f),
                revDelay = Mathf.Clamp(m / 343f, 0.01f, 0.1f),
            };
        }
    }
}
