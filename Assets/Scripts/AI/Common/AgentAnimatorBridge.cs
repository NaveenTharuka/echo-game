using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

namespace Echo.AI
{
    /// The only script that talks to the Animator (guide §8.5). Brains call these methods; locomotion is read
    /// from the NavMeshAgent's velocity, so the brain never drives animation directly.
    /// Parameters: Speed (float), Listening (bool), Attack / Hit / Alert (triggers), Dead (bool), optional Turn (float).
    /// Missing parameters are skipped silently, so one bridge works with every agent's controller.
    public class AgentAnimatorBridge : MonoBehaviour
    {
        public Animator animator;
        [Tooltip("Old playback scaling: speed / referenceSpeed. footSlideFix replaces it.")]
        public bool matchPlaybackToSpeed = false;
        public float referenceSpeed = 2.2f;

        [Header("Optional")]
        [Tooltip("Turning speed (deg/s) that sends Turn = +-1. Only used if the controller has a Turn float.")]
        public float turnReference = 180f;
        [Tooltip("Scale playback so the feet match the real ground speed. Only in the locomotion state, so " +
                 "attacks, hits and death always play at normal speed.")]
        public bool footSlideFix = false;
        [Tooltip("Threshold of the fastest clip in the Speed blend tree (run).")]
        public float blendTopSpeed = 5f;
        public string locomotionState = "Locomotion";

        /// Signed yaw speed in deg/s (+ = turning right).
        public float TurnRate { get; private set; }

        static readonly int Speed = Animator.StringToHash("Speed");
        static readonly int TurnF = Animator.StringToHash("Turn");
        static readonly int Listening = Animator.StringToHash("Listening");
        static readonly int AttackT = Animator.StringToHash("Attack");
        static readonly int HitT = Animator.StringToHash("Hit");
        static readonly int AlertT = Animator.StringToHash("Alert");
        static readonly int DieB = Animator.StringToHash("Dead");
        NavMeshAgent nav;
        readonly HashSet<int> has = new();
        int locoHash; float lastYaw; bool scaled;

        void Awake()
        {
            nav = GetComponent<NavMeshAgent>();
            if (!animator) animator = GetComponentInChildren<Animator>();
            if (animator)
            {
                animator.applyRootMotion = false;   // the NavMeshAgent moves the body
                foreach (var p in animator.parameters) has.Add(p.nameHash);
            }
            locoHash = Animator.StringToHash(locomotionState);
            lastYaw = transform.eulerAngles.y;
        }

        void Update()
        {
            float yaw = transform.eulerAngles.y;
            if (Time.deltaTime > 0f) TurnRate = Mathf.DeltaAngle(lastYaw, yaw) / Time.deltaTime;
            lastYaw = yaw;

            if (!animator || nav == null) return;
            float v = nav.velocity.magnitude;
            if (has.Contains(Speed)) animator.SetFloat(Speed, v, 0.1f, Time.deltaTime);   // blend tree: idle/sneak/walk/run
            if (has.Contains(TurnF)) animator.SetFloat(TurnF, Mathf.Clamp(TurnRate / turnReference, -1f, 1f), 0.15f, Time.deltaTime);

            if (footSlideFix) FixFootSlide(v);
            else if (matchPlaybackToSpeed) animator.speed = v > 0.2f ? Mathf.Clamp(v / referenceSpeed, 0.6f, 1.6f) : 1f;
            else if (scaled) { animator.speed = 1f; scaled = false; }
        }

        /// The blend tree shows the ground speed of the (damped) Speed value, capped at the fastest clip.
        /// Playback = real speed / shown speed, so feet keep up while accelerating and above the run threshold.
        void FixFootSlide(float v)
        {
            float k = 1f;
            bool loco = !animator.IsInTransition(0) && animator.GetCurrentAnimatorStateInfo(0).shortNameHash == locoHash;
            if (loco && v > 0.2f && has.Contains(Speed))
            {
                float shown = Mathf.Clamp(animator.GetFloat(Speed), 0.5f, blendTopSpeed);
                k = Mathf.Clamp(v / shown, 0.5f, 1.8f);
            }
            animator.speed = k; scaled = true;
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
            animator.speed = 1f;
            animator.Rebind();   // snap back to the default state
        }
    }
}
