using UnityEngine;

namespace Echo.DevTools
{
    /// Self-test stand-in for an agent: records the damage it takes.
    public class DamageProbe : MonoBehaviour, IDamageable
    {
        public int Hits { get; private set; }
        public float LastAmount { get; private set; }
        public Vector3 LastFrom { get; private set; }

        public void TakeDamage(float amount, Vector3 from)
        {
            Hits++;
            LastAmount = amount;
            LastFrom = from;
        }
    }
}
