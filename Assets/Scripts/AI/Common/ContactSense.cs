using UnityEngine;

namespace Echo.AI
{
    /// Touch (guide §8.4): a trigger sphere on a child of the agent. Touch is a legitimate sense for a blind hunter.
    /// Needs a Rigidbody on one side of the contact: the agent root has a kinematic one.
    [RequireComponent(typeof(SphereCollider))]
    public class ContactSense : MonoBehaviour
    {
        public float radius = 1.3f, repeat = 0.5f;
        AgentBrain brain; float next;

        void Awake()
        {
            var c = GetComponent<SphereCollider>(); c.isTrigger = true; c.radius = radius;
            brain = GetComponentInParent<AgentBrain>();
        }

        void OnTriggerStay(Collider other)
        {
            if (Time.time < next || !other.CompareTag("Player") || brain == null || !brain.isActiveAndEnabled) return;
            next = Time.time + repeat;
            brain.OnContact(other.transform.position);   // touch is a legitimate sense
        }
    }
}
