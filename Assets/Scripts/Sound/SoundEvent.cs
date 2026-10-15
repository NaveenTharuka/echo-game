using UnityEngine;

namespace Echo.Sound
{
    /// Ground truth of one sound (guide §4.2). Stays inside the sound system: agents get a PerceivedSound.
    public readonly struct SoundEvent
    {
        public readonly int Id;
        public readonly SoundType Type;
        public readonly Vector3 Position;
        public readonly float Intensity;      // 0..1  (1 = gunshot)
        public readonly float Time;
        public readonly SoundSource Source;   // ground truth - not for agents
        public readonly GameObject Emitter;

        public SoundEvent(int id, SoundType type, Vector3 position, float intensity,
                          float time, SoundSource source, GameObject emitter)
        {
            Id = id; Type = type; Position = position; Intensity = intensity;
            Time = time; Source = source; Emitter = emitter;
        }
    }

    /// What an agent actually perceives: an *estimate*. No source, no emitter: agents cannot tell real from fake.
    public struct PerceivedSound
    {
        public int EventId;
        public SoundType Type;
        public Vector3 EstimatedPosition;
        public float PositionError;   // metres (radius of uncertainty)
        public float Loudness;        // 0..1 at the agent's ear
        public float TimeHeard;
    }
}
