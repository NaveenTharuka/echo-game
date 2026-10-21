using System.Collections.Generic;
using Echo.Sound;
using UnityEngine;

namespace Echo.AI
{
    /// Base of every agent brain (guide §8.3): think tick (5 Hz, or at once on a loud sound), perception drain,
    /// motor event snapshot, and the hooks the body calls (touch, hurt, reset).
    [RequireComponent(typeof(Hearing), typeof(AgentMotor), typeof(AgentBody))]
    public abstract class AgentBrain : MonoBehaviour
    {
        [SerializeField] protected float thinkInterval = 0.2f;   // 5 Hz decisions

        protected Hearing Hearing; protected AgentMotor Motor; protected AgentBody Body;
        protected AgentAnimatorBridge Anim;                       // may be null in greybox prototypes
        protected readonly List<PerceivedSound> NewSounds = new();
        protected readonly HabituationMemory Habituation = new();
        protected Vector3 Home; protected Quaternion HomeRotation;
        /// Snapshot of the motor's one-shot events since the previous tick (read these, not the motor flags).
        protected bool Arrived, Failed;
        float nextThink;

        public bool IsAlive => !Body.IsDead;

        public abstract string DisplayName { get; }
        public abstract string StateName { get; }
        public virtual float AlertLevel => 0f;                // 0 calm ... 1 hunting (music, Mimic)
        public virtual float CurrentInterest => 0f;           // priority of what it is pursuing (Mimic's model)
        public float HearingSensitivity => Hearing.sensitivity;
        public virtual string DebugText => "";

        protected virtual void Awake()
        {
            Hearing = GetComponent<Hearing>(); Motor = GetComponent<AgentMotor>(); Body = GetComponent<AgentBody>();
            TryGetComponent(out Anim);
            Home = transform.position; HomeRotation = transform.rotation;
        }
        protected virtual void OnEnable() => AgentRegistry.Add(this);
        protected virtual void OnDisable() => AgentRegistry.Remove(this);

        void Update()
        {
            if (Body.IsDead) return;
            Hearing.Alertness = AlertLevel;                      // hunting agents listen harder
            Motor.Urgency = AlertLevel;                          // ... and run harder (chase speed)
            if (Time.time < nextThink && !Hearing.HasUrgent) return;
            nextThink = Time.time + thinkInterval * Random.Range(0.85f, 1.15f); // de-synchronise agents
            NewSounds.Clear();
            Hearing.Drain(NewSounds);
            // Take the motor's events *before* thinking: a MoveTo() that fails inside Think()
            // sets FailedFlag again, and the brain sees it on its next tick instead of losing it.
            Arrived = Motor.ArrivedFlag; Failed = Motor.FailedFlag;
            Motor.ConsumeFlags();
            Think();
        }

        /// Perception -> memory -> decision -> action. Called at ~5 Hz or immediately on loud sounds.
        protected abstract void Think();

        /// Touch sense: something (the player) brushed against the agent.
        public virtual void OnContact(Vector3 where) => Body.TryAttack(where);

        /// Got hurt: by default treat it like a very loud, precise sound at `from`.
        public virtual void OnHurt(Vector3 from) { }

        public virtual void ResetAgent()
        {
            Motor.Warp(Home); transform.rotation = HomeRotation;
            Habituation.Clear();
            if (Body.IsDead) Body.Revive();
        }
    }
}
