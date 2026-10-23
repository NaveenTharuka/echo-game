using System;
using System.Collections.Generic;
using Echo.Sound;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Echo.Interaction
{
    /// The Echo.Sound.SoundType values my systems emit (names match the frozen enum, guide §4.1;
    /// mapped by an explicit switch, so a renamed SoundType is a compile error, not a silent drop).
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
        /// True if the sound core received it (always, since the sound core is a direct dependency).
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
    /// Forwards to Noise.Make (frozen contract, guide §4.6) and raises <see cref="Emitted"/> for debug views / self-tests.
    public static class InteractionNoise
    {
        public const float SettleMuteSeconds = 1.5f;

        /// Every accepted noise, delivered or not. For debug views / self-tests.
        public static event Action<NoiseRecord> Emitted;

        /// Kept for the self-test summary: the sound core is a direct dependency now, so always true.
        public static bool SoundCoreBound => true;

        /// True while noises are suppressed (after a scene load, or an explicit MuteFor).
        public static bool IsMuted => Time.time < mutedUntil;

        static readonly Dictionary<long, float> nextAllowed = new();
        static float mutedUntil;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            nextAllowed.Clear();
            Emitted = null;
            mutedUntil = 0f;
        }

        /// Suppresses every noise for the next `seconds` (extends, never shortens, a running mute).
        public static void MuteFor(float seconds) => mutedUntil = Mathf.Max(mutedUntil, Time.time + seconds);

        // Every scene load (first scene, death reload, additive loads) gets a settle mute,
        // because that is when physics props are spawned and land.
        static void OnSceneLoaded(Scene scene, LoadSceneMode mode) => MuteFor(SettleMuteSeconds);

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void Init()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            SceneManager.sceneLoaded += OnSceneLoaded;
            MuteFor(SettleMuteSeconds);
        }

        static SoundType ToSound(NoiseType t) => t switch
        {
            NoiseType.Footstep => SoundType.Footstep, NoiseType.Run => SoundType.Run,
            NoiseType.Jump => SoundType.Jump, NoiseType.Land => SoundType.Land,
            NoiseType.Gunshot => SoundType.Gunshot, NoiseType.BulletImpact => SoundType.BulletImpact,
            NoiseType.Door => SoundType.Door, NoiseType.MetalImpact => SoundType.MetalImpact,
            NoiseType.GlassBreak => SoundType.GlassBreak, NoiseType.ObjectDrop => SoundType.ObjectDrop,
            NoiseType.Machinery => SoundType.Machinery, NoiseType.Generator => SoundType.Generator,
            NoiseType.Switch => SoundType.Switch,
            _ => throw new ArgumentOutOfRangeException(nameof(t), t, null)
        };

        static SoundSource ToSource(NoiseSource s) => s switch
        {
            NoiseSource.Player => SoundSource.Player, NoiseSource.Environment => SoundSource.Environment,
            NoiseSource.AgentBody => SoundSource.AgentBody,
            _ => throw new ArgumentOutOfRangeException(nameof(s), s, null)
        };

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
            Noise.Make(ToSound(type), position, intensity, ToSource(source), emitter, playAudio);
            Emitted?.Invoke(new NoiseRecord(type, position, intensity, source, emitter, Time.time, true));

#if UNITY_EDITOR
            // Scene-view marker: taller = louder.
            Debug.DrawRay(position, Vector3.up * (0.3f + intensity * 2f), Color.green, 1.5f);
#endif
            return true;
        }
    }
}
