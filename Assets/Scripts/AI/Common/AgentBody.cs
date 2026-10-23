using System.Collections;
using Echo.Player;
using Echo.Sound;
using UnityEngine;

namespace Echo.AI
{
    /// Health, attack and death of an agent (guide §8.4). Implements IDamageable so HitscanWeapon can hurt it.
    /// The attack is decided by the brain from sound/touch; whatever is in front of the claws when the swing lands
    /// gets hit (physics, not perception). The player has one-hit death (PlayerDeath.Kill); anything else
    /// damageable takes attackDamage.
    public class AgentBody : MonoBehaviour, IDamageable
    {
        public float maxHealth = 100f;
        public float attackDamage = 35f, attackCooldown = 1.6f, windup = 0.5f, reach = 1.1f, hitRadius = 0.9f;
        [Tooltip("Lunge: during the wind-up the body springs at the target if it is within this range (m) ...")]
        public float lungeRange = 3.4f;
        [Tooltip("... at up to this speed (m/s), stopping short so the claws land on it.")]
        public float lungeSpeed = 9f;
        [Tooltip("Charging (moving faster than this, m/s) the lunge carries on past the estimate: prey runs away.")]
        public float chargeSpeed = 4f, chargeLead = 1.5f;
        [Tooltip("Layers the claws can hit. Everything by default; set to Player once the layer exists.")]
        public LayerMask playerMask = ~0;
        public float Health { get; private set; }
        public bool IsDead => Health <= 0f;
        /// True from the start of the wind-up until the swing has landed.
        public bool IsAttacking { get; private set; }

        AgentBrain brain; AgentMotor motor; AgentAnimatorBridge anim; AgentVoice voice; Collider body; float nextAttack;

        void Awake()
        {
            Health = maxHealth;
            brain = GetComponent<AgentBrain>(); motor = GetComponent<AgentMotor>(); TryGetComponent(out anim); TryGetComponent(out voice);
            body = GetComponent<Collider>();
        }

        public void TryAttack(Vector3 towards)
        {
            if (IsDead || Time.time < nextAttack) return;
            nextAttack = Time.time + attackCooldown;
            StartCoroutine(Attack(towards));
        }

        IEnumerator Attack(Vector3 towards)
        {
            IsAttacking = true;
            // Lunge: spring at where it thinks the prey is (its estimate - never the player itself). When it was
            // already charging, momentum carries it further, because fleeing prey keeps running.
            Vector3 to = towards - transform.position; to.y = 0f;
            float dist = to.magnitude;
            bool charging = motor.Velocity.magnitude > chargeSpeed;
            float lunge = dist <= lungeRange + (charging ? chargeLead : 0f)
                ? Mathf.Max(0f, dist + (charging ? chargeLead : 0f) - reach * 0.7f) : 0f;
            Vector3 dir = dist > 0.01f ? to / dist : transform.forward;
            float lungeV = Mathf.Min(lungeSpeed, lunge / Mathf.Max(0.05f, windup));
            motor.Stop();
            if (anim != null) anim.Attack();
            if (voice != null) voice.Attack();
            float t = 0f, moved = 0f;
            while (t < windup)
            {
                motor.FaceTowards(towards, 720f);
                if (moved < lunge && !IsDead)
                {
                    float step = Mathf.Min(lungeV * Time.deltaTime, lunge - moved);
                    motor.Lunge(dir * step); moved += step;
                }
                t += Time.deltaTime; yield return null;
            }
            IsAttacking = false;
            if (IsDead) yield break;
            Noise.Make(SoundType.AgentVoice, transform.position, 0.4f, SoundSource.AgentBody, gameObject,
                       playAudio: voice == null || !voice.HasAttack);   // its own snarl already played at the wind-up
            // Hit resolution is physics, not perception: whatever is in front of the claws gets hit.
            var centre = transform.position + Vector3.up * 1.1f + transform.forward * reach;
            foreach (var c in Physics.OverlapSphere(centre, hitRadius, playerMask, QueryTriggerInteraction.Ignore))
            {
                if (c.transform.IsChildOf(transform)) continue;
                var victim = c.GetComponentInParent<PlayerDeath>();
                if (victim != null) { victim.Kill(gameObject); continue; }
                var d = c.GetComponentInParent<IDamageable>();
                if (d != null) d.TakeDamage(attackDamage, transform.position);
            }
        }

        public void TakeDamage(float amount, Vector3 from)
        {
            if (IsDead) return;
            Health -= amount;
            if (IsDead)
            {
                StopAllCoroutines(); IsAttacking = false;
                motor.Stop(); if (anim != null) anim.Die();
                if (voice != null) voice.Death();
                if (body) body.enabled = false;
                return;
            }
            if (anim != null) anim.Hit();
            if (voice != null) voice.Hurt();
            brain.OnHurt(from);
        }

        public void Revive()
        {
            Health = maxHealth;
            if (body) body.enabled = true;
            if (anim != null) anim.Revive();
        }

        void OnDrawGizmosSelected()
        {
            Gizmos.color = new Color(1f, 0.2f, 0.2f, 0.5f);
            Gizmos.DrawWireSphere(transform.position + Vector3.up * 1.1f + transform.forward * reach, hitRadius);
        }
    }
}
