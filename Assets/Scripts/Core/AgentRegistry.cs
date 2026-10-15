using System.Collections.Generic;
using UnityEngine;

namespace Echo.AI
{
    /// Every live agent brain (guide §8.1). Only the Mimic may read other agents through it; music/overlay may too.
    public static class AgentRegistry
    {
        static readonly List<AgentBrain> agents = new();
        public static IReadOnlyList<AgentBrain> All => agents;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void Reset() => agents.Clear();

        public static void Add(AgentBrain a) { if (!agents.Contains(a)) agents.Add(a); }
        public static void Remove(AgentBrain a) => agents.Remove(a);
        public static void ResetAll() { foreach (var a in agents) a.ResetAgent(); }
    }
}
