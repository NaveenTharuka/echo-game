using System;
using System.Collections.Generic;
using Echo.Interaction;
using UnityEngine;
using UnityEngine.AI;

namespace Echo.AI.Tracker
{
    /// Stand-in for M1's Hearing until the sound core exists. Listens to InteractionNoise.Emitted and applies
    /// the guide's model (§4.4-4.5): effective distance = straight line if clear, else max(graph route,
    /// 1.15 x straight line); R = intensity x 45 m x sensitivity; loudness = (1 - d/R)^2; heard if >= threshold;
    /// error = lerp(maxError, minError, loudness); estimate = true position + random offset within the error.
    /// Only the estimate, error, time and kind reach the Tracker: source and emitter are stripped (blind rule).
    /// The Tracker's own noises (e.g. a door it opened) are not heard.
    public class TrackerPerceptionStub : MonoBehaviour
    {
        public SandboxTrackerGraph graph;
        public float sensitivity = 1f;
        public float threshold = 0.03f;
        public float earHeight = 1.6f;
        public float maxPositionError = 6f, minPositionError = 0.5f;
        public const float MetresPerIntensity = 45f;

        static readonly HeardKind[] kindOf = BuildKindMap();
        readonly List<TrackerPercept> inbox = new(32);
        readonly RaycastHit[] hits = new RaycastHit[16];
        RouteService routes;

        /// Last heard sound, for the overlay.
        public bool HasLastHeard { get; private set; }
        public TrackerPercept LastHeard { get; private set; }
        public int HeardCount { get; private set; }

        static HeardKind[] BuildKindMap()
        {
            var names = Enum.GetNames(typeof(NoiseType));
            var map = new HeardKind[names.Length];
            for (int i = 0; i < names.Length; i++)
            {
                if (!Enum.TryParse(names[i], out map[i]))
                    Debug.LogError($"[TrackerPerceptionStub] NoiseType.{names[i]} has no HeardKind twin.");
            }
            return map;
        }

        void Start() { if (graph != null) routes = new RouteService(graph.Graph, new TrackerSettings()); }
        void OnEnable() => InteractionNoise.Emitted += OnNoise;
        void OnDisable() => InteractionNoise.Emitted -= OnNoise;

        public void Drain(List<TrackerPercept> into)
        {
            into.AddRange(inbox);
            inbox.Clear();
        }

        void OnNoise(NoiseRecord r)
        {
            if (r.Emitter != null && r.Emitter.transform.IsChildOf(transform)) return;   // its own noise

            float loud = Loudness(r.Position, r.Intensity);
            if (loud < threshold) return;

            float err = Mathf.Lerp(maxPositionError, minPositionError, loud);
            Vector2 off = UnityEngine.Random.insideUnitCircle * err;
            Vector3 guess = r.Position + new Vector3(off.x, 0f, off.y);
            if (NavMesh.SamplePosition(guess, out var hit, 3f, NavMesh.AllAreas)) guess = hit.position;

            var p = new TrackerPercept(guess, err, r.Time, kindOf[(int)r.Type]);
            if (inbox.Count < 64) inbox.Add(p);
            LastHeard = p; HasLastHeard = true; HeardCount++;
        }

        /// Loudness (0..1) at this agent's ear of a sound of `intensity` at `source` (guide §4.4).
        public float Loudness(Vector3 source, float intensity)
        {
            float d = EffectiveDistance(source, transform.position + Vector3.up * earHeight);
            float radius = intensity * MetresPerIntensity * sensitivity;
            if (radius <= 0f || d >= radius) return 0f;
            float x = 1f - d / radius;
            return x * x;
        }

        public bool WouldHear(Vector3 source, float intensity) => Loudness(source, intensity) >= threshold;

        float EffectiveDistance(Vector3 source, Vector3 ear)
        {
            float straight = Vector3.Distance(source, ear);
            if (!Occluded(source + Vector3.up * 0.3f, ear) || routes == null) return straight;
            var g = routes.World;
            float route = routes.Route(source, g.Nearest(source), ear, g.Nearest(ear));
            return Mathf.Max(route, straight * 1.15f);
        }

        /// Static or kinematic geometry between source and ear (walls, door frames, closed door leaves).
        /// Dynamic props, the player's CharacterController and this agent do not block sound.
        bool Occluded(Vector3 from, Vector3 to)
        {
            Vector3 dir = to - from;
            float len = dir.magnitude;
            if (len < 1e-3f) return false;
            int n = Physics.RaycastNonAlloc(from, dir / len, hits, len, ~0, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < n; i++)
            {
                var c = hits[i].collider;
                var rb = c.attachedRigidbody;
                if ((rb != null && !rb.isKinematic) || c is CharacterController || c.transform.IsChildOf(transform)) continue;
                return true;
            }
            return false;
        }
    }
}
