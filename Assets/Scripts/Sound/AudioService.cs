using System.Collections.Generic;
using UnityEngine;

namespace Echo.Sound
{
    /// Pooled 3D one-shots (guide §5.2). Silent (but harmless) until a SoundLibrary is assigned.
    public class AudioService : MonoBehaviour
    {
        public static AudioService Instance { get; private set; }
        public SoundLibrary library;
        public int poolSize = 24;

        readonly List<AudioSource> pool = new();
        int next;

        void Awake()
        {
            Instance = this;
            for (int i = 0; i < poolSize; i++)
            {
                var go = new GameObject("SFX " + i);
                go.transform.SetParent(transform);
                var s = go.AddComponent<AudioSource>();
                s.playOnAwake = false;
                s.spatialBlend = 1f;                         // fully 3D
                s.rolloffMode = AudioRolloffMode.Logarithmic;
                s.dopplerLevel = 0f;
                pool.Add(s);
            }
        }

        void OnDestroy() { if (Instance == this) Instance = null; }

        public static void Play(SoundType type, Vector3 pos, float intensity)
        {
            if (Instance != null) Instance.PlayAt(type, pos, intensity);
        }

        void PlayAt(SoundType type, Vector3 pos, float intensity)
        {
            var e = library ? library.Get(type) : null;
            if (e == null || e.clips == null || e.clips.Length == 0) return;
            var s = pool[next]; next = (next + 1) % pool.Count;
            s.transform.position = pos;
            s.clip = e.clips[Random.Range(0, e.clips.Length)];
            s.pitch = Random.Range(e.pitch.x, e.pitch.y);
            s.volume = e.volume * Mathf.Lerp(0.6f, 1f, intensity);
            s.minDistance = e.minDistance;
            s.maxDistance = e.maxDistance;
            s.outputAudioMixerGroup = e.group;
            s.Play();
        }
    }
}
