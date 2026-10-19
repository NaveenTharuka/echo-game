using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

namespace Echo.Sound
{
    /// The agent's ear (guide §4.5). Turns ground-truth SoundEvents into noisy PerceivedSounds in an inbox
    /// that the brain drains on its think tick.
    [DisallowMultipleComponent]
    public class Hearing : MonoBehaviour, IHearer
    {
        [Tooltip("1 = normal. Ambusher 1.4, Listener 1.15, Tracker 1.0, Mimic 0.9")]
        public float sensitivity = 1f;
        [Tooltip("Sounds quieter than this at the ear are ignored")]
        public float threshold = 0.03f;
        public float earHeight = 1.6f;
        [Tooltip("Position error (m) for the faintest / loudest audible sound")]
        public float maxPositionError = 6f, minPositionError = 0.5f;
        [Tooltip("Loudness that wakes the brain immediately instead of waiting for its tick")]
        public float urgentLoudness = 0.45f;
        [Tooltip("Extra sensitivity while hunting: effective = sensitivity x (1 + alertBoost x Alertness). " +
                 "A hunting agent strains to hear, so sneaking away from it needs real care.")]
        public float alertBoost = 0.5f;

        /// 0 calm ... 1 hunting. Set by AgentBrain from its AlertLevel every frame.
        public float Alertness { get; set; }
        public float EffectiveSensitivity => sensitivity * (1f + alertBoost * Mathf.Clamp01(Alertness));

        readonly List<PerceivedSound> inbox = new();
        /// Raised for every sound this ear actually perceives (debug views, e.g. HearingVisualizer).
        public event System.Action<PerceivedSound> Heard;
        public bool HasUrgent { get; private set; }

        void OnEnable() => SoundBus.Register(this);
        void OnDisable() => SoundBus.Unregister(this);

        public void Hear(in SoundEvent e)
        {
            if (e.Source == SoundSource.AgentBody) return;   // agents recognise their own kind's movement
            if (e.Emitter == gameObject) return;             // the Mimic ignores its own fakes

            Vector3 ear = transform.position + Vector3.up * earHeight;
            float d = Acoustics.EffectiveDistance(e.Position, ear);
            float loud = Acoustics.Loudness(e.Intensity, EffectiveSensitivity, d);
            if (loud < threshold) return;

            // Fainter sound => worse localisation. Blind hunters must search.
            float err = Mathf.Lerp(maxPositionError, minPositionError, loud);
            Vector2 r = Random.insideUnitCircle * err;
            Vector3 guess = e.Position + new Vector3(r.x, 0f, r.y);
            if (NavMesh.SamplePosition(guess, out var hit, 3f, NavMesh.AllAreas)) guess = hit.position;

            inbox.Add(new PerceivedSound
            {
                EventId = e.Id, Type = e.Type, EstimatedPosition = guess,
                PositionError = err, Loudness = loud, TimeHeard = Time.time
            });
            if (loud >= urgentLoudness) HasUrgent = true;
            Heard?.Invoke(inbox[inbox.Count - 1]);
        }

        public void Drain(List<PerceivedSound> into)
        {
            into.AddRange(inbox);
            inbox.Clear();
            HasUrgent = false;
        }
    }
}
