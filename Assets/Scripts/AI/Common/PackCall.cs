using Echo.Sound;
using UnityEngine;

namespace Echo.AI
{
    /// Hunting as a pack: an agent's cry ("there!") is heard by every other agent close enough to hear the cry itself
    /// (same acoustics as any sound: walls and detours count). What it passes on is the caller's own estimate of the
    /// prey and its error - so the pack is only as well informed as the caller's ears. Agents still ignore each
    /// other's footsteps and breathing (Hearing skips AgentBody sounds); only deliberate calls go through here.
    public static class PackCall
    {
        /// Returns how many agents heard the call.
        public static int Raise(AgentBrain caller, Vector3 mouth, float intensity, Vector3 estimate, float error)
        {
            int heard = 0;
            foreach (var a in AgentRegistry.All)
            {
                if (a == null || a == caller || !a.IsAlive) continue;
                if (a.TryGetComponent(out Hearing ear) && ear.HearCall(mouth, intensity, estimate, error)) heard++;
            }
            return heard;
        }
    }
}
