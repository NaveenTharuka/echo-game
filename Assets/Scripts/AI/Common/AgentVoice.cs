using UnityEngine;

namespace Echo.AI
{
    /// The agent's own voice (presentation): scream, hurt bark, attack snarl, death. Each agent sounds like itself
    /// instead of the 5 shared AgentVoice growls, so players can tell by ear what is coming.
    /// Screams play two layers like AudioService's loud sounds: the close recording, and a distant version (dull,
    /// roomy) that takes over with distance. Both sources have AudioOcclusion (walls, doorways, air).
    /// The AI side is unchanged: brains still emit their AgentVoice SoundEvents (with playAudio off when this exists).
    public class AgentVoice : MonoBehaviour
    {
        public AudioSource source, farSource;
        [Tooltip("0 = full scream (alert / pack call), 1 = short scream (roar when shot, lock-on).")]
        public AudioClip[] scream, screamFar;
        public AudioClip[] hurt, attack, death;
        [Range(0f, 1f)] public float screamVolume = 1f, hurtVolume = 0.75f, attackVolume = 0.85f, deathVolume = 0.9f;
        public Vector2 pitch = new(0.96f, 1.04f);
        [Tooltip("Distance (m) where the far scream starts / has fully taken over.")]
        public Vector2 farBlend = new(10f, 34f);
        [Tooltip("Scream when the brain goes from calm to hunting on its own (for brains that don't call Scream).")]
        public bool screamOnLockOn;
        public float lockOnCooldown = 15f;

        AgentBrain brain;
        float nextBark, lastScream = -999f, lastAlert;
        int lastHurt = -1;

        void Awake() => TryGetComponent(out brain);

        void Update()
        {
            if (!screamOnLockOn || brain == null) return;
            float a = brain.AlertLevel;
            if (a >= 0.85f && lastAlert < 0.5f && Time.time - lastScream > lockOnCooldown && brain.IsAlive) Scream(true);
            lastAlert = a;
        }

        /// Returns the clip length (0 if nothing played).
        public float Scream(bool shortOne = false)
        {
            if (scream == null || scream.Length == 0 || source == null) return 0f;
            int i = Mathf.Clamp(shortOne ? 1 : 0, 0, scream.Length - 1);
            float p = Random.Range(pitch.x, pitch.y);
            float w = FarWeight();
            source.Stop(); if (farSource != null) farSource.Stop();         // a scream cuts a bark short
            if (w < 0.98f) { source.pitch = p; source.PlayOneShot(scream[i], screamVolume * (1f - 0.85f * w)); }
            if (w > 0.02f && farSource != null && screamFar != null && screamFar.Length > 0)
            {
                farSource.pitch = p;
                farSource.PlayOneShot(screamFar[Mathf.Min(i, screamFar.Length - 1)], screamVolume * w);
            }
            lastScream = Time.time; nextBark = Time.time + 1f;
            return scream[i].length / p;
        }

        public void Hurt()
        {
            if (Time.time < nextBark || hurt == null || hurt.Length == 0) return;
            int i = Random.Range(0, hurt.Length);
            if (hurt.Length > 1 && i == lastHurt) i = (i + 1) % hurt.Length;
            lastHurt = i;
            Bark(hurt[i], hurtVolume, 0.45f);
        }

        public void Attack()
        {
            if (attack == null || attack.Length == 0) return;
            Bark(attack[Random.Range(0, attack.Length)], attackVolume, 0.3f);
        }

        public void Death()
        {
            if (death == null || death.Length == 0 || source == null) return;
            source.Stop();
            source.pitch = Random.Range(pitch.x, pitch.y);
            source.PlayOneShot(death[Random.Range(0, death.Length)], deathVolume);
            nextBark = float.MaxValue;
        }

        /// True when this agent has its own clips for that kind of sound (callers then skip the shared growl).
        public bool HasAttack => attack != null && attack.Length > 0 && source != null;
        public bool HasScream => scream != null && scream.Length > 0 && source != null;

        void Bark(AudioClip c, float volume, float gap)
        {
            if (source == null || c == null) return;
            source.pitch = Random.Range(pitch.x, pitch.y);
            source.PlayOneShot(c, volume);
            nextBark = Time.time + gap;
        }

        float FarWeight()
        {
            var cam = Camera.main;
            if (cam == null) return 0f;
            float d = Vector3.Distance(cam.transform.position, transform.position);
            return Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(farBlend.x, farBlend.y, d));
        }
    }
}
