namespace Echo.Sound
{
    /// Frozen contract (guide §4.1). InteractionNoise maps its NoiseType onto these by name.
    public enum SoundType
    {
        Footstep, Run, Jump, Land, Gunshot, Reload, BulletImpact,
        Door, MetalImpact, GlassBreak, ObjectDrop, Machinery, Generator, Switch,
        AgentVoice
    }

    /// Ground truth about who made a sound. Used by audio, debugging and filtering
    /// of agents' own body noise. NEVER exposed to agent decision code.
    public enum SoundSource { Player, Environment, AgentBody, Mimic }
}
