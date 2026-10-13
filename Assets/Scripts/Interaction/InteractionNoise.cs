using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Echo.Interaction
{
    /// The Echo.Sound.SoundType values my systems emit. Names must match the frozen enum exactly
    /// (guide §4.1); they are mapped by name when binding to the sound core.
    public enum NoiseType
    {
        Footstep, Run, Jump, Land, Gunshot, BulletImpact,
        Door, MetalImpact, GlassBreak, ObjectDrop, Machinery, Generator, Switch
    }

    /// Mirrors Echo.Sound.SoundSource (minus Mimic, which is never mine).
    public enum NoiseSource { Player, Environment, AgentBody }

    public readonly struct NoiseRecord
    {
        public readonly NoiseType Type;
        public readonly Vector3 Position;
        public readonly float Intensity;
        public readonly NoiseSource Source;
        public readonly GameObject Emitter;
        public readonly float Time;
        /// True if the sound core received it (agents can hear it).
        public readonly bool Delivered;

        public NoiseRecord(NoiseType type, Vector3 position, float intensity, NoiseSource source,
                           GameObject emitter, float time, bool delivered)
        {
            Type = type; Position = position; Intensity = intensity; Source = source;
            Emitter = emitter; Time = time; Delivered = delivered;
        }
    }

    /// The ONLY place interaction/player code reaches the sound core (guide `Noise.Make`).
    /// Adds what the sound core does not: per-object cooldowns and a settle mute after a scene
    /// loads, so physics props landing at start-up don't flood the agents with fake noises.
    /// Callers emit physics impacts from OnCollisionEnter only (never Stay).
    ///
    /// Binding is by reflection, once, at load: until the sound core exists the project still
    /// compiles and every noise is still raised on <see cref="Emitted"/> for debugging.
    public static class InteractionNoise
    {
        public const float SettleMuteSeconds = 1.5f;

        /// Every accepted noise, delivered or not. For debug views / self-tests.
        public static event Action<NoiseRecord> Emitted;

        public static bool SoundCoreBound => make != null;

        /// True while noises are suppressed (after a scene load, or an explicit MuteFor).
        public static bool IsMuted => Time.time < mutedUntil;

        static readonly Dictionary<long, float> nextAllowed = new();
        static readonly object[] args = new object[6];
        static MethodInfo make;
        static object[] typeValues, sourceValues;
        static float mutedUntil;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            nextAllowed.Clear();
            Emitted = null;
            make = null;
            mutedUntil = 0f;
        }

        /// Suppresses every noise for the next `seconds` (extends, never shortens, a running mute).
        public static void MuteFor(float seconds) => mutedUntil = Mathf.Max(mutedUntil, Time.time + seconds);

        // Every scene load (first scene, death reload, additive loads) gets a settle mute,
        // because that is when physics props are spawned and land.
        static void OnSceneLoaded(Scene scene, LoadSceneMode mode) => MuteFor(SettleMuteSeconds);

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void Bind()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            SceneManager.sceneLoaded += OnSceneLoaded;
            MuteFor(SettleMuteSeconds);

            Type noise = null, soundType = null, soundSource = null;
            foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                noise ??= asm.GetType("Echo.Sound.Noise");
                soundType ??= asm.GetType("Echo.Sound.SoundType");
                soundSource ??= asm.GetType("Echo.Sound.SoundSource");
            }

            if (noise == null || soundType == null || soundSource == null)
            {
                Debug.Log("[InteractionNoise] Echo.Sound.Noise not found: noises stay local until the sound core is merged.");
                return;
            }

            MethodInfo m = noise.GetMethod("Make", BindingFlags.Public | BindingFlags.Static, null,
                new[] { soundType, typeof(Vector3), typeof(float), soundSource, typeof(GameObject), typeof(bool) }, null);
            if (m == null)
            {
                Debug.LogWarning("[InteractionNoise] Noise.Make signature differs from the frozen contract (guide §4.6). Not bound.");
                return;
            }

            typeValues = MapEnum(typeof(NoiseType), soundType);
            sourceValues = MapEnum(typeof(NoiseSource), soundSource);
            make = m;
        }

        static object[] MapEnum(Type mine, Type theirs)
        {
            string[] names = Enum.GetNames(mine);
            var map = new object[names.Length];
            for (int i = 0; i < names.Length; i++)
            {
                if (Enum.IsDefined(theirs, names[i])) map[i] = Enum.Parse(theirs, names[i]);
                else Debug.LogWarning($"[InteractionNoise] {theirs.Name} has no '{names[i]}'; that noise will not reach agents.");
            }
            return map;
        }

        /// Emits a noise. Returns false if it was suppressed (settle mute or cooldown).
        /// <param name="cooldown">Minimum seconds between noises of this type from this emitter.</param>
        public static bool Emit(NoiseType type, Vector3 position, float intensity, NoiseSource source,
                                GameObject emitter = null, float cooldown = 0f, bool playAudio = true)
        {
            if (IsMuted) return false;

            if (cooldown > 0f && emitter != null)
            {
                long key = ((long)emitter.GetInstanceID() << 8) | (long)type;
                if (nextAllowed.TryGetValue(key, out float until) && Time.time < until) return false;
                nextAllowed[key] = Time.time + cooldown;
            }

            intensity = Mathf.Clamp01(intensity);
            bool delivered = Forward(type, position, intensity, source, emitter, playAudio);
            Emitted?.Invoke(new NoiseRecord(type, position, intensity, source, emitter, Time.time, delivered));

#if UNITY_EDITOR
            // Scene-view marker: taller = louder; green = agents heard it, grey = sound core missing.
            Debug.DrawRay(position, Vector3.up * (0.3f + intensity * 2f), delivered ? Color.green : Color.grey, 1.5f);
#endif
            return true;
        }

        static bool Forward(NoiseType type, Vector3 position, float intensity, NoiseSource source,
                            GameObject emitter, bool playAudio)
        {
            if (make == null) return false;
            object t = typeValues[(int)type], s = sourceValues[(int)source];
            if (t == null || s == null) return false;

            args[0] = t; args[1] = position; args[2] = intensity;
            args[3] = s; args[4] = emitter; args[5] = playAudio;
            make.Invoke(null, args);
            args[4] = null;   // don't keep the emitter alive
            return true;
        }
    }
}
