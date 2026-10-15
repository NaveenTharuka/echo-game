using UnityEngine;

namespace Echo.AI
{
    /// Signature one-shots (guide §5.6, Tracker): a soft click every few seconds, faster as the agent's AlertLevel
    /// rises. Presentation only - audio for the player, never a SoundEvent.
    public class AgentClicks : MonoBehaviour
    {
        public AudioSource source;
        public AudioClip[] clips;
        [Tooltip("Seconds between clicks when calm (random in range).")]
        public Vector2 calmInterval = new(3f, 5f);
        [Tooltip("Seconds between clicks when fully alert.")]
        public Vector2 alertInterval = new(0.6f, 1.1f);
        public Vector2 pitch = new(0.9f, 1.15f);
        [Range(0f, 1f)] public float volume = 0.8f;

        AgentBrain brain; float next;

        void Awake() => brain = GetComponent<AgentBrain>();

        void Update()
        {
            if (source == null || clips == null || clips.Length == 0 || Time.time < next) return;
            if (brain != null && !brain.IsAlive) return;
            float a = brain != null ? brain.AlertLevel : 0f;
            var range = Vector2.Lerp(calmInterval, alertInterval, a);
            next = Time.time + Random.Range(range.x, range.y);
            source.pitch = Random.Range(pitch.x, pitch.y);
            source.PlayOneShot(clips[Random.Range(0, clips.Length)], volume);
        }
    }
}
