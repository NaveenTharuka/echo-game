using Echo.Sound;
using UnityEngine;

namespace Echo.AI
{
    /// The Listener's core (guide §9.2). Pure logic: unit-testable, and the Mimic uses it to model the Listener.
    /// priority = loudness x typeWeight x recency x habituation,  recency = exp(-age / 8 s).
    public static class ListenerPriority
    {
        public const float SwitchFactor = 1.25f;
        public const float RecencyTau = 8f;

        public static float TypeWeight(SoundType t) => t switch
        {
            SoundType.Gunshot => 1f, SoundType.GlassBreak => 0.9f, SoundType.MetalImpact => 0.75f,
            SoundType.Land => 0.7f, SoundType.Door => 0.6f, SoundType.Run => 0.6f,
            SoundType.BulletImpact => 0.55f, SoundType.ObjectDrop => 0.5f, SoundType.Footstep => 0.45f,
            SoundType.Jump => 0.45f, SoundType.Switch => 0.4f, SoundType.Generator => 0.35f,
            SoundType.Reload => 0.3f, SoundType.Machinery => 0.15f,
            _ => 0f
        };

        public static float Score(float loudness, SoundType type, float age, float habituation = 1f)
            => loudness * TypeWeight(type) * Mathf.Exp(-age / RecencyTau) * habituation;

        /// Hysteresis: adopt a new sound only if it clearly beats the current one, or the current one is stale.
        public static bool ShouldSwitch(float newPriority, float currentPriority, float currentAge)
            => newPriority > SwitchFactor * currentPriority || currentAge > 10f;
    }
}
