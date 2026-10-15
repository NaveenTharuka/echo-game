using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Audio;

namespace Echo.Sound
{
    /// Clips per SoundType (guide §5.1). Create via Create > ECHO > Sound Library.
    [CreateAssetMenu(menuName = "ECHO/Sound Library")]
    public class SoundLibrary : ScriptableObject
    {
        [Serializable]
        public class Entry
        {
            public SoundType type;
            public AudioClip[] clips;
            [Range(0f, 1f)] public float volume = 1f;
            public Vector2 pitch = new(0.94f, 1.06f);
            public float minDistance = 1f, maxDistance = 30f;
            public AudioMixerGroup group;
        }

        public Entry[] entries;
        Dictionary<SoundType, Entry> map;

        void OnValidate() => map = null;

        public Entry Get(SoundType t)
        {
            if (map == null)
            {
                map = new();
                if (entries != null) foreach (var e in entries) map[e.type] = e;
            }
            return map.TryGetValue(t, out var entry) ? entry : null;
        }
    }
}
