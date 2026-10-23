using System.Collections.Generic;
using Echo.Sound;
using UnityEngine;

namespace Echo.DevTools
{
    /// Dev tool: an invisible "ghost player" that walks a route and emits real player footsteps through Noise.Make,
    /// so agents (especially the Tracker, which needs a moving trail) can be tested without walking yourself.
    /// Put it in a scene, add child Transforms as the route (or fill `route`), press Play. Gizmo = route.
    public class SoundTrailProbe : MonoBehaviour
    {
        public List<Vector3> route = new();
        public float speed = 4f;              // m/s (player walk 4, sprint 6)
        public bool running;                  // Run sounds (0.30) instead of Footstep (0.15)
        public float startDelay = 1f;
        public bool loop;

        public Vector3 Position { get; private set; }
        public bool Finished { get; private set; }
        float travelled, stepAt, startAt; int seg;

        void Start()
        {
            if (route.Count == 0) foreach (Transform t in transform) route.Add(t.position);
            if (route.Count > 0) Position = route[0];
            startAt = Time.time + startDelay;
            stepAt = running ? 2.2f : 1.6f;
        }

        void Update()
        {
            if (Finished || route.Count < 2 || Time.time < startAt) return;
            float move = speed * Time.deltaTime;
            while (move > 0f && !Finished)
            {
                Vector3 to = route[seg + 1];
                float d = Vector3.Distance(Position, to);
                float step = Mathf.Min(move, d);
                Position = Vector3.MoveTowards(Position, to, step);
                move -= step; travelled += step;
                if (travelled >= stepAt)
                {
                    travelled = 0f;
                    Noise.Make(running ? SoundType.Run : SoundType.Footstep, Position,
                               running ? 0.30f : 0.15f, SoundSource.Player, gameObject);
                }
                if (step >= d)
                {
                    if (++seg >= route.Count - 1)
                    {
                        if (loop) { seg = 0; Position = route[0]; } else Finished = true;
                    }
                }
            }
        }

        void OnDrawGizmos()
        {
            Gizmos.color = new Color(0.2f, 0.8f, 1f);
            for (int i = 0; i + 1 < route.Count; i++) Gizmos.DrawLine(route[i] + Vector3.up * 0.1f, route[i + 1] + Vector3.up * 0.1f);
            if (Application.isPlaying) Gizmos.DrawSphere(Position + Vector3.up, 0.3f);
        }
    }
}
