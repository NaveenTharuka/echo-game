using System.Collections.Generic;
using UnityEngine;

namespace Echo.Sound
{
    /// Pooled 3D one-shots (guide §5.2). Silent (but harmless) until a SoundLibrary is assigned.
    /// Every pooled source has AudioOcclusion (walls, doorway leaks, air absorption, reverb send). Types with far clips
    /// (*_Far.wav) play two layers: the close recording fades out and the distant one (dull, roomy) fades in with distance,
    /// so a gunshot 50 m away is a rolling boom down the corridors, not a quiet copy of the close crack.
    public class AudioService : MonoBehaviour
    {
        public static AudioService Instance { get; private set; }
        public SoundLibrary library;
        public int poolSize = 32;
        [Tooltip("Far layer: starts at this fraction of maxDistance, is the whole sound at the second value.")]
        public Vector2 farBlend = new(0.18f, 0.6f);
        [Tooltip("The far layer carries further than the close one (tails travel down corridors).")]
        public float farRangeScale = 1.8f;
        [Range(0f, 1.5f)] public float farGain = 1f;

        readonly List<AudioSource> pool = new();
        readonly List<AudioOcclusion> occ = new();
        int next;

        void Awake()
        {
            Instance = this;
            int n = Mathf.Max(poolSize, 32);                 // scenes saved with the old 24: near + far layers need more
            for (int i = 0; i < n; i++)
            {
                var go = new GameObject("SFX " + i);
                go.transform.SetParent(transform);
                var s = go.AddComponent<AudioSource>();
                s.playOnAwake = false;
                s.spatialBlend = 1f;                         // fully 3D
                s.rolloffMode = AudioRolloffMode.Logarithmic;
                s.dopplerLevel = 0f;
                go.AddComponent<AudioLowPassFilter>();
                var o = go.AddComponent<AudioOcclusion>();
                o.trackWhenSilent = false;
                pool.Add(s); occ.Add(o);
            }
        }

        void OnDestroy() { if (Instance == this) Instance = null; }

        public static void Play(SoundType type, Vector3 pos, float intensity)
        {
            if (Instance != null) Instance.PlayAt(type, pos, intensity);
        }

        /// Any clip as a pooled 3D one-shot (with occlusion). Returns false without an AudioService.
        public static bool PlayClip(AudioClip clip, Vector3 pos, float volume, float minDistance = 1f, float maxDistance = 20f,
                                    float pitch = 1f)
        {
            if (Instance == null || clip == null) return false;
            Instance.Fire(clip, pos, volume, minDistance, maxDistance, pitch, null);
            return true;
        }

        void PlayAt(SoundType type, Vector3 pos, float intensity)
        {
            var e = library ? library.Get(type) : null;
            if (e == null || e.clips == null || e.clips.Length == 0) return;
            int i = Random.Range(0, e.clips.Length);
            float pitch = Random.Range(e.pitch.x, e.pitch.y);
            float vol = e.volume * Mathf.Lerp(0.6f, 1f, intensity);

            float w = 0f;
            bool hasFar = e.farClips != null && e.farClips.Length > 0;
            var ears = Camera.main ? Camera.main.transform : null;
            if (hasFar && ears != null)
            {
                float d = Vector3.Distance(ears.position, pos);
                w = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(farBlend.x * e.maxDistance, farBlend.y * e.maxDistance, d));
            }
            if (w < 0.98f) Fire(e.clips[i], pos, vol * (1f - 0.85f * w), e.minDistance, e.maxDistance, pitch, e.group);
            if (w > 0.02f)
            {
                var far = e.farClips[i % e.farClips.Length];   // same order as the near clips (Name_01 / Name_01_Far)
                Fire(far, pos, vol * w * farGain, e.minDistance * 2f, e.maxDistance * farRangeScale, pitch, e.group);
            }
        }

        void Fire(AudioClip clip, Vector3 pos, float volume, float min, float max, float pitch, UnityEngine.Audio.AudioMixerGroup group)
        {
            int k = next; next = (next + 1) % pool.Count;
            var s = pool[k];
            s.Stop();
            s.transform.position = pos;
            s.clip = clip;
            s.pitch = pitch;
            s.minDistance = min;
            s.maxDistance = max;
            s.outputAudioMixerGroup = group;
            occ[k].SetBaseVolume(volume);
            occ[k].Snap();
            s.Play();
        }
    }
}
