using Echo.Navigation;
using Unity.AI.Navigation;
using UnityEngine;

namespace Echo.AI
{
    /// Lets agents through a Door whatever route they planned. The NavMesh path may run through the doorway via
    /// `link` (the door frame is too narrow for the baked NavMesh), so the door must open whenever an agent walks
    /// up to it, not only when the motor follows the door's own graph edge:
    ///   - a trigger around the leaf opens the closed door for any agent that enters it (door sound included);
    ///   - the link is off while the door is locked, so nothing paths through a locked door.
    [RequireComponent(typeof(BoxCollider))]
    public class DoorAgentPassage : MonoBehaviour
    {
        public Door door;
        public NavMeshLink link;

        void Awake() { GetComponent<BoxCollider>().isTrigger = true; }

        void Update()
        {
            if (door != null && link != null && link.activated == door.isLocked) link.activated = !door.isLocked;
        }

        void OnTriggerEnter(Collider other) => TryOpen(other);
        void OnTriggerStay(Collider other) => TryOpen(other);

        void TryOpen(Collider other)
        {
            if (door == null || door.isOpen || door.isLocked) return;
            var agent = other.GetComponentInParent<AgentBrain>();
            if (agent == null || !agent.IsAlive) return;
            door.SetOpen(true, agent.gameObject, byAgent: true);
        }
    }
}
