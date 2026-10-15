using System;
using System.Collections.Generic;
using UnityEngine;

namespace Echo.Sound
{
    public interface IHearer { void Hear(in SoundEvent e); }

    /// Event-driven delivery of sounds to every registered ear (guide §4.3).
    public static class SoundBus
    {
        static readonly List<IHearer> hearers = new();
        static int nextId;

        /// Anyone may observe ground truth: audio, debug drawing, HUD noise meter.
        public static event Action<SoundEvent> Emitted;

        // Required if "Enter Play Mode Options" disables domain reload.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() { hearers.Clear(); nextId = 0; Emitted = null; }

        public static void Register(IHearer h) { if (!hearers.Contains(h)) hearers.Add(h); }
        public static void Unregister(IHearer h) => hearers.Remove(h);

        public static SoundEvent Emit(SoundType type, Vector3 position, float intensity,
                                      SoundSource source, GameObject emitter = null)
        {
            var e = new SoundEvent(++nextId, type, position, Mathf.Clamp01(intensity),
                                   Time.time, source, emitter);
            Emitted?.Invoke(e);
            for (int i = hearers.Count - 1; i >= 0; i--) hearers[i].Hear(e);
            return e;
        }
    }
}
