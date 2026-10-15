using UnityEngine;

namespace Echo.AI.Tracker
{
    public struct TrackerObservation
    {
        public Vector3 Position;
        public float Time, Error, Credibility;
        public HeardKind Kind;
        public int Node;            // nearest graph node, resolved once on arrival
    }

    public enum ObservationResult { Filtered, Accepted, Suspect, Reset }

    /// Short-term memory: the last `capacity` credible observations within `window` seconds, plus a
    /// two-slot suspect list. The credibility gate is outlier rejection by physical plausibility:
    ///   allowed = v_max * |dt| + err_new + err_last + margin
    ///   cred    = 1 if route <= allowed, else exp(-(route - allowed) / falloff)
    /// Route is the graph route distance (a fake behind a wall is far by route even when close in a
    /// straight line). cred < suspectThreshold goes to the suspect list; two suspects that are
    /// plausible with each other mean the player really moved (or the trail was stale), so the
    /// history is replaced by them (data association by consensus).
    public sealed class TrackerMemory
    {
        readonly TrackerSettings s;
        readonly TrackerObservation[] ring;
        int head, count;                                      // head = oldest
        readonly TrackerObservation[] suspects = new TrackerObservation[2];
        int suspectCount;

        public TrackerMemory(TrackerSettings settings)
        {
            s = settings;
            ring = new TrackerObservation[Mathf.Max(2, settings.capacity)];
        }

        public int Count => count;
        public int SuspectCount => suspectCount;
        public bool HasLast => count > 0;
        /// 0 = oldest.
        public TrackerObservation this[int i] => ring[(head + i) % ring.Length];
        public TrackerObservation Last => this[count - 1];
        public TrackerObservation Suspect(int i) => suspects[i];

        public void Clear() { count = 0; head = 0; suspectCount = 0; }

        /// Drops observations and suspects older than the window.
        public void Prune(float now)
        {
            while (count > 0 && now - ring[head].Time > s.window) { head = (head + 1) % ring.Length; count--; }
            int keep = 0;
            for (int i = 0; i < suspectCount; i++)
                if (now - suspects[i].Time <= s.window) suspects[keep++] = suspects[i];
            suspectCount = keep;
        }

        public float Allowed(in TrackerObservation a, in TrackerObservation b) =>
            s.maxPlayerSpeed * Mathf.Abs(b.Time - a.Time) + a.Error + b.Error + s.credibilityMargin;

        public float Credibility(float route, float allowed) =>
            route <= allowed ? 1f
            : float.IsPositiveInfinity(route) ? 0f
            : Mathf.Exp(-(route - allowed) / s.credibilityFalloff);

        /// `node` = nearest graph node of the percept. Percepts must arrive in time order.
        public ObservationResult Add(in TrackerPercept p, int node, RouteService routes, out float credibility)
        {
            credibility = 0f;
            if (!TrackerSoundFilter.IsTrail(p.Kind)) return ObservationResult.Filtered;
            Prune(p.Time);

            var o = new TrackerObservation
            {
                Position = p.Position, Time = p.Time, Error = Mathf.Max(0f, p.Error),
                Credibility = 1f, Kind = p.Kind, Node = node
            };

            if (count > 0)
            {
                var last = Last;
                float route = routes.Route(last.Position, last.Node, o.Position, o.Node);
                o.Credibility = Credibility(route, Allowed(last, o));
            }
            credibility = o.Credibility;

            if (o.Credibility < s.suspectThreshold)
            {
                for (int i = suspectCount - 1; i >= 0; i--)
                {
                    var prev = suspects[i];
                    float route = routes.Route(prev.Position, prev.Node, o.Position, o.Node);
                    if (route > Allowed(prev, o)) continue;
                    // Consensus: two independent sounds agree with each other, the old trail does not.
                    Clear();
                    prev.Credibility = 1f; o.Credibility = 1f;
                    Push(prev); Push(o);
                    credibility = 1f;
                    return ObservationResult.Reset;
                }
                if (suspectCount == suspects.Length) { suspects[0] = suspects[1]; suspectCount = 1; }
                suspects[suspectCount++] = o;
                return ObservationResult.Suspect;
            }

            Push(o);
            return ObservationResult.Accepted;
        }

        void Push(in TrackerObservation o)
        {
            if (count == ring.Length) { head = (head + 1) % ring.Length; count--; }
            ring[(head + count) % ring.Length] = o;
            count++;
        }
    }
}
