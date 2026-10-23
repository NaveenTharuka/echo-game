using UnityEngine;

namespace Echo.Sound
{
    /// One call makes a noise (guide §4.6): plays the audio AND informs the AI.
    /// Frozen signature: InteractionNoise binds to it by reflection.
    public static class Noise
    {
        public static void Make(SoundType type, Vector3 position, float intensity,
                                SoundSource source, GameObject emitter = null, bool playAudio = true)
        {
            if (playAudio) AudioService.Play(type, position, intensity);
            SoundBus.Emit(type, position, intensity, source, emitter);
        }
    }
}
