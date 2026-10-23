using UnityEngine;

namespace Echo.AI
{
    /// Presentation: swaps the agent's skin to a "damaged" material once its health drops to `threshold`,
    /// so the player can see their shots are working. Restored on revive.
    public class AgentDamageLook : MonoBehaviour
    {
        public Material healthy, damaged;
        [Range(0f, 1f)] public float threshold = 0.5f;
        AgentBody body; Renderer[] renderers; bool showingDamaged;

        void Awake()
        {
            body = GetComponent<AgentBody>();
            renderers = GetComponentsInChildren<Renderer>(true);
        }

        void Update()
        {
            if (body == null || healthy == null || damaged == null) return;
            bool hurt = body.Health <= body.maxHealth * threshold;
            if (hurt == showingDamaged) return;
            showingDamaged = hurt;
            var from = hurt ? healthy : damaged; var to = hurt ? damaged : healthy;
            foreach (var r in renderers)
            {
                var mats = r.sharedMaterials;
                bool changed = false;
                for (int i = 0; i < mats.Length; i++) if (mats[i] == from) { mats[i] = to; changed = true; }
                if (changed) r.sharedMaterials = mats;
            }
        }
    }
}
