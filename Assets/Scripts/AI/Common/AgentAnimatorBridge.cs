using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

namespace Echo.AI
{
    /// The only script that talks to the Animator (guide §8.5). Brains call these methods; locomotion is read
    /// from the NavMeshAgent's velocity, so the brain never drives animation directly.
    /// Parameters: Speed (float), Listening (bool), Attack / Hit / Alert (triggers), Dead (bool).
    /// Missing parameters are skipped silently, so one bridge works with every agent's controller.
    public class AgentAnimatorBridge : MonoBehaviour
    {
        public Animator animator;
        [Tooltip("Multiplies the animation playback speed while moving so feet don't slide: speed / referenceSpeed.")]
        public bool matchPlaybackToSpeed = false;
        public float referenceSpeed = 2.2f;

        static readonly int Speed = Animator.StringToHash("Speed");
        static readonly int Listening = Animator.StringToHash("Listening");
        static readonly int AttackT = Animator.StringToHash("Attack");
        static readonly int HitT = Animator.StringToHash("Hit");
        static readonly int AlertT = Animator.StringToHash("Alert");
        static readonly int DieB = Animator.StringToHash("Dead");
        NavMeshAgent nav;
        readonly HashSet<int> has = new();

        void Awake()
        {
            nav = GetComponent<NavMeshAgent>();
            if (!animator) animator = GetComponentInChildren<Animator>();
            if (animator)
            {
                animator.applyRootMotion = false;   // the NavMeshAgent moves the body
                foreach (var p in animator.parameters) has.Add(p.nameHash);
            }
        }

        void Update()
        {
            if (!animator || nav == null) return;
            float v = nav.velocity.magnitude;
            if (has.Contains(Speed)) animator.SetFloat(Speed, v, 0.1f, Time.deltaTime);   // blend tree: idle/sneak/walk/run
            if (matchPlaybackToSpeed) animator.speed = v > 0.2f ? Mathf.Clamp(v / referenceSpeed, 0.6f, 1.6f) : 1f;
        }

        public void SetListening(bool on) { if (animator && has.Contains(Listening)) animator.SetBool(Listening, on); }
        public void Attack() { if (animator && has.Contains(AttackT)) animator.SetTrigger(AttackT); }
        public void Hit() { if (animator && has.Contains(HitT)) animator.SetTrigger(HitT); }
        public void Alert() { if (animator && has.Contains(AlertT)) animator.SetTrigger(AlertT); }
        public void Die() { if (animator && has.Contains(DieB)) animator.SetBool(DieB, true); }
        public void Revive()
        {
            if (!animator) return;
            if (has.Contains(DieB)) animator.SetBool(DieB, false);
            animator.Rebind();   // snap back to the default state
        }
    }
}
