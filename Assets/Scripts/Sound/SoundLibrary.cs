using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Audio;

namespace Echo.Sound
{
    /// Clips per SoundType (guide §5.1). Create via Create > ECHO > Sound Library.
    /// Also holds named clip sets for presentation audio that is not a SoundType (player breath, heartbeat, alarm,
    /// dry fire), so runtime-created components can find their clips without scene references.
    [CreateAssetMenu(menuName = "ECHO/Sound Library")]
    public class SoundLibrary : ScriptableObject
    {
        [Serializable]
        public class Entry
        {
            public SoundType type;
            public AudioClip[] clips;
            [Tooltip("The same sounds heard from far away (*_Far.wav): AudioService fades them in with distance.")]
            public AudioClip[] farClips;
            [Range(0f, 1f)] public float volume = 1f;
            public Vector2 pitch = new(0.94f, 1.06f);
            public float minDistance = 1f, maxDistance = 30f;
            public AudioMixerGroup group;
        }

        [Serializable]
        public class ClipSet
        {
            [Tooltip("File name without the trailing _01 / .wav, e.g. Breath_Calm_In, Heartbeat_Fast_Loop.")]
            public string name;
            public AudioClip[] clips;
        }

        public Entry[] entries;
        public ClipSet[] sets;
        Dictionary<SoundType, Entry> map;
        Dictionary<string, AudioClip[]> setMap;

        void OnValidate() { map = null; setMap = null; }

        public Entry Get(SoundType t)
        {
            if (map == null)
            {
                map = new();
                if (entries != null) foreach (var e in entries) map[e.type] = e;
            }
            return map.TryGetValue(t, out var entry) ? entry : null;
        }

        /// Clips of a named set, or an empty array.
        public AudioClip[] Set(string name)
        {
            if (setMap == null)
            {
                setMap = new();
                if (sets != null) foreach (var s in sets) if (s != null && !string.IsNullOrEmpty(s.name)) setMap[s.name] = s.clips;
            }
            return setMap.TryGetValue(name, out var c) && c != null ? c : Array.Empty<AudioClip>();
        }

        /// The library the running scene uses (AudioService's), or null.
        public static SoundLibrary Active => AudioService.Instance != null ? AudioService.Instance.library : null;

        /// Convenience for runtime code: a named set from the active library.
        public static AudioClip[] ActiveSet(string name)
        {
            var lib = Active;
            return lib != null ? lib.Set(name) : Array.Empty<AudioClip>();
        }
    }
}
