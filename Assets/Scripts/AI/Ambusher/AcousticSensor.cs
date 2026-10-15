using Echo.Navigation;
using Echo.Sound;
using UnityEngine;

namespace Echo.AI
{
    /// Optional (guide §12.6): a facility microphone (put on Security Cam props in corridors). Reports only *which zone*
    /// a sound happened in to the Ambusher - more data for its route model. Story: it is wired into the intercom.
    /// Shooting it disables it (IDamageable).
    public class AcousticSensor : MonoBehaviour, IHearer, IDamageable
    {
        public float sensitivity = 0.8f, threshold = 0.05f;
        AmbusherBrain ambusher;

        void OnEnable() { SoundBus.Register(this); ambusher = FindFirstObjectByType<AmbusherBrain>(); }
        void OnDisable() => SoundBus.Unregister(this);

        public void Hear(in SoundEvent e)
        {
            if (ambusher == null || e.Source == SoundSource.AgentBody) return;
            float loud = Acoustics.Loudness(e.Intensity, sensitivity, Acoustics.EffectiveDistance(e.Position, transform.position));
            if (loud >= threshold)   // reports only the zone + a coarse position
                ambusher.SensorReport(ZoneVolume.ZoneAt(transform.position), transform.position, Time.time);
        }

        public void TakeDamage(float amount, Vector3 from) => enabled = false;
    }
}
