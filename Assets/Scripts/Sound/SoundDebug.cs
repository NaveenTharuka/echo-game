using System.Collections.Generic;
using UnityEngine;

namespace Echo.Sound
{
    /// Sound rings in the Scene/Game view (guide §15.1). Blue = player, magenta = Mimic, grey = agent body,
    /// yellow = environment. Ring radius = how far the sound carries at sensitivity 1.
    public class SoundDebug : MonoBehaviour
    {
        public bool show = true; public float lifetime = 1.5f;
        readonly List<(SoundEvent e, float until)> recent = new();

        void OnEnable() => SoundBus.Emitted += Add;
        void OnDisable() => SoundBus.Emitted -= Add;
        void Add(SoundEvent e) => recent.Add((e, Time.time + lifetime));

        void OnDrawGizmos()
        {
            if (!show || !Application.isPlaying) return;
            recent.RemoveAll(r => r.until < Time.time);
            foreach (var (e, until) in recent)
            {
                float k = (until - Time.time) / lifetime;
                Gizmos.color = e.Source switch
                {
                    SoundSource.Player => new Color(0.2f, 0.8f, 1f, k),
                    SoundSource.Mimic => new Color(1f, 0.2f, 1f, k),
                    SoundSource.AgentBody => new Color(0.5f, 0.5f, 0.5f, k * 0.5f),
                    _ => new Color(1f, 0.85f, 0.2f, k)
                };
                Gizmos.DrawWireSphere(e.Position, e.Intensity * Acoustics.MetresPerIntensity * (1f - k * 0.3f));
            }
        }
    }
}
