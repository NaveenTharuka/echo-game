using System.Collections.Generic;
using UnityEngine;

namespace Echo.AI
{
    /// The Ambusher's memory of how the player moves (guide §12.2): a first-order Markov chain over zones with
    /// exponential forgetting and Laplace smoothing. Plain C# (no Unity objects) so it can be checked in the editor.
    ///   count[A][B] += 1 per observed transition A -> B (adjacent zones only); every update count *= exp(-dt / tau)
    ///   P(B | A) = (count[A][B] + alpha) / (sum_C count[A][C] + alpha * deg(A))
    public class RouteModel
    {
        public float Alpha = 0.5f, ForgetTau = 120f;
        readonly Dictionary<string, Dictionary<string, float>> counts = new();
        readonly Dictionary<string, HashSet<string>> adjacency;   // zone graph
        string lastZone; float lastTime;

        public string CurrentZone => lastZone;
        public IReadOnlyDictionary<string, HashSet<string>> Adjacency => adjacency;

        public RouteModel(Dictionary<string, HashSet<string>> zoneAdjacency) { adjacency = zoneAdjacency; }

        /// A credible sound was localised in `zone` at `time`. A change of zone = one observed transition.
        public void Observe(string zone, float time)
        {
            if (string.IsNullOrEmpty(zone)) return;
            Decay(time);
            if (lastZone != null && zone != lastZone && adjacency.TryGetValue(lastZone, out var nbrs) && nbrs.Contains(zone))
            {
                if (!counts.TryGetValue(lastZone, out var row)) counts[lastZone] = row = new Dictionary<string, float>();
                row[zone] = (row.TryGetValue(zone, out var c) ? c : 0f) + 1f;
            }
            lastZone = zone; lastTime = time;
        }

        /// Forget old habits: every count decays with time constant ForgetTau.
        public void Decay(float time)
        {
            float k = Mathf.Exp(-(time - lastTime) / ForgetTau);
            lastTime = Mathf.Max(lastTime, time);
            if (k >= 0.999f) return;
            foreach (var row in counts.Values)
                foreach (var key in new List<string>(row.Keys)) row[key] *= k;
        }

        public float Count(string from, string to)
            => counts.TryGetValue(from, out var row) && row.TryGetValue(to, out var c) ? c : 0f;

        public float P(string from, string to)
        {
            if (!adjacency.TryGetValue(from, out var nbrs) || !nbrs.Contains(to)) return 0f;
            counts.TryGetValue(from, out var row);
            float total = 0f; if (row != null) foreach (var v in row.Values) total += v;
            float c = row != null && row.TryGetValue(to, out var x) ? x : 0f;
            return (c + Alpha) / (total + Alpha * nbrs.Count);
        }

        /// Probability the player passes through each zone within `horizon` transitions (forward propagation).
        public Dictionary<string, float> PassProbability(string from, int horizon = 3)
        {
            var pass = new Dictionary<string, float>();
            var pi = new Dictionary<string, float> { [from] = 1f };
            var notYet = new Dictionary<string, float>();
            for (int k = 0; k < horizon; k++)
            {
                var next = new Dictionary<string, float>();
                foreach (var (z, pz) in pi)
                    if (adjacency.TryGetValue(z, out var nbrs))
                        foreach (var n in nbrs)
                            next[n] = (next.TryGetValue(n, out var v) ? v : 0f) + pz * P(z, n);
                foreach (var (z, pz) in next)
                    notYet[z] = (notYet.TryGetValue(z, out var q) ? q : 1f) * (1f - pz);
                pi = next;
            }
            foreach (var (z, q) in notYet) if (z != from) pass[z] = 1f - q;
            return pass;
        }

        /// How many times (decayed) the player has been heard leaving `zone`: the evidence behind P(. | zone).
        public float Evidence(string zone)
        {
            float t = 0f;
            if (counts.TryGetValue(zone, out var row)) foreach (var v in row.Values) t += v;
            return t;
        }

        public float TotalObservations()
        {
            float t = 0f; foreach (var row in counts.Values) foreach (var v in row.Values) t += v; return t;
        }

        /// Strongest learned transitions, for the overlay ("Wall_S->Corner 2.4").
        public string TopTransitions(int n = 3)
        {
            var all = new List<(string, float)>();
            foreach (var (a, row) in counts) foreach (var (b, c) in row) if (c > 0.05f) all.Add(($"{a}->{b}", c));
            all.Sort((x, y) => y.Item2.CompareTo(x.Item2));
            var parts = new List<string>();
            for (int i = 0; i < Mathf.Min(n, all.Count); i++) parts.Add($"{all[i].Item1} {all[i].Item2:0.0}");
            return string.Join("  ", parts);
        }

        public void Clear() { counts.Clear(); lastZone = null; }
    }
}
