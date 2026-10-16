using UnityEngine;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
using UnityEngine.InputSystem;
using UnityEngine.Rendering;
#endif

namespace Echo.AI.Tracker
{
    /// What the overlay reads: the decider and the think-cost counters (TrackerBrain, TrackerStubAgent).
    public interface ITrackerDebugSource
    {
        TrackerDecider Decider { get; }
        int Ticks { get; }
        float LastThinkMs { get; }
        float AvgThinkMs { get; }
        float MaxThinkMs { get; }
    }

    /// Tracker debug view (editor and development builds only; F2 toggles). Reads the decider, never writes.
    /// World: breadcrumbs with error circles (orange), suspects (magenta), velocity arrow (cyan), predicted
    /// point (red), candidate nodes coloured by score (blue = low, yellow = high), chosen node (green),
    /// cold-trail search (white = visited). Screen: state, confidence and its parts, think cost.
    /// Needs an ITrackerDebugSource on the same object (TrackerBrain or the grid sandbox's TrackerStubAgent).
    public class TrackerDebugOverlay : MonoBehaviour
    {
        public bool show = true;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        ITrackerDebugSource agent;
        static Material lineMaterial;
        GUIStyle style;
        string text = "";
        int textTick = -1;

        void Awake() => TryGetComponent(out agent);
        void OnEnable() => RenderPipelineManager.endCameraRendering += Draw;
        void OnDisable() => RenderPipelineManager.endCameraRendering -= Draw;

        void Update()
        {
            var kb = Keyboard.current;
            if (kb != null && kb.f2Key.wasPressedThisFrame) show = !show;
        }

        static bool EnsureMaterial()
        {
            if (lineMaterial != null) return true;
            var shader = Shader.Find("Hidden/Internal-Colored");
            if (shader == null) return false;
            lineMaterial = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            lineMaterial.SetInt("_ZTest", (int)CompareFunction.Always);
            lineMaterial.SetInt("_Cull", (int)CullMode.Off);
            lineMaterial.SetInt("_ZWrite", 0);
            return true;
        }

        void Draw(ScriptableRenderContext ctx, Camera cam)
        {
            var d = agent != null ? agent.Decider : null;
            if (!show || d == null || !EnsureMaterial()) return;
            var w = d.World;
            const float lift = 0.15f;

            GL.PushMatrix();
            lineMaterial.SetPass(0);
            GL.LoadProjectionMatrix(cam.projectionMatrix);
            GL.modelview = cam.worldToCameraMatrix;
            GL.Begin(GL.LINES);

            // Candidates by score, chosen node, search.
            var sc = d.Scorer;
            float lo = float.PositiveInfinity, hi = float.NegativeInfinity;
            bool scored = d.HasVelocity && sc.Scores.Length == w.Count;
            if (scored)
                for (int i = 0; i < w.Count; i++)
                    if (sc.IsCandidate[i]) { lo = Mathf.Min(lo, sc.Scores[i]); hi = Mathf.Max(hi, sc.Scores[i]); }
            bool searching = d.State == TrackerState.Search;
            for (int i = 0; i < w.Count; i++)
            {
                Vector3 p = w.Position(i);
                if (searching && d.Search.Active && d.Search.IsVisited(i)) { Post(p, 2f, Color.white); continue; }
                if (scored && sc.IsCandidate[i])
                {
                    float t = hi > lo ? (sc.Scores[i] - lo) / (hi - lo) : 1f;
                    Post(p, 0.4f + 1.6f * t, Color.Lerp(new Color(0.2f, 0.4f, 1f), Color.yellow, t));
                }
            }
            if (d.HasGoal)
            {
                Circle(d.Goal + Vector3.up * lift, 0.9f, Color.green);
                Line(transform.position + Vector3.up * lift, d.Goal + Vector3.up * lift, new Color(0f, 1f, 0f, 0.5f));
            }

            // Breadcrumbs.
            var m = d.Memory;
            for (int i = 0; i < m.Count; i++)
            {
                var o = m[i];
                Vector3 p = o.Position + Vector3.up * lift;
                Circle(p, Mathf.Max(0.2f, o.Error), new Color(1f, 0.55f, 0f, 0.5f));
                if (i > 0) Line(m[i - 1].Position + Vector3.up * lift, p, new Color(1f, 0.55f, 0f));
            }
            for (int i = 0; i < m.SuspectCount; i++) Cross(m.Suspect(i).Position + Vector3.up * lift, 0.6f, Color.magenta);
            if (m.HasLast && d.HasVelocity)
            {
                Vector3 a = d.Anchor.Position + Vector3.up * lift;      // the fitted trail point it scored from
                Line(a, a + d.Velocity, Color.cyan);
                Cross(d.Predicted + Vector3.up * lift, 0.7f, Color.red);
                Circle(d.Predicted + Vector3.up * lift, 0.5f, Color.red);
            }

            GL.End();
            GL.PopMatrix();
        }

        static void Line(Vector3 a, Vector3 b, Color c) { GL.Color(c); GL.Vertex(a); GL.Vertex(b); }
        static void Post(Vector3 p, float h, Color c) => Line(p, p + Vector3.up * h, c);
        static void Cross(Vector3 p, float r, Color c)
        {
            Line(p - new Vector3(r, 0f, r), p + new Vector3(r, 0f, r), c);
            Line(p - new Vector3(r, 0f, -r), p + new Vector3(r, 0f, -r), c);
        }
        static void Circle(Vector3 p, float r, Color c)
        {
            const int seg = 16;
            Vector3 prev = p + new Vector3(r, 0f, 0f);
            for (int i = 1; i <= seg; i++)
            {
                float a = i * Mathf.PI * 2f / seg;
                Vector3 next = p + new Vector3(Mathf.Cos(a) * r, 0f, Mathf.Sin(a) * r);
                Line(prev, next, c); prev = next;
            }
        }

        void OnGUI()
        {
            var d = agent != null ? agent.Decider : null;
            if (!show || d == null) return;
            if (textTick != agent.Ticks)   // rebuild the text only when the Tracker has thought
            {
                textTick = agent.Ticks;
                var c = d.Confidence;
                var m = d.Memory;
                text = $"TRACKER  {d.State}   band {d.Band}\n" +
                       $"C {c.Value:0.00}  = fresh {c.Freshness:0.00} x (0.4 ev {c.Evidence:0.00} + 0.3 fit {c.Fit:0.00} + 0.3 margin {c.Margin:0.00})\n" +
                       $"obs {m.Count}  suspects {m.SuspectCount}  |v| {(d.HasVelocity ? d.Velocity.magnitude.ToString("0.0") : "-")} m/s  " +
                       $"candidates {(d.HasVelocity ? d.Scorer.CandidateCount : 0)}\n" +
                       $"heard: accepted {d.Accepted}  suspect {d.Suspects}  resets {d.Resets}  filtered {d.Filtered}  " +
                       $"last {d.LastResult} cred {d.LastCredibility:0.00}\n" +
                       $"think {agent.LastThinkMs:0.000} ms  avg {agent.AvgThinkMs:0.000}  max {agent.MaxThinkMs:0.000}  " +
                       $"Dijkstra {d.Routes.Computations}" + (agent is TrackerStubAgent stub ? $"  kills {stub.Kills}" : "") + "   [F2 hide]";
            }
            style ??= new GUIStyle(GUI.skin.box) { alignment = TextAnchor.UpperLeft, fontSize = 13, richText = false };
            GUI.Box(new Rect(10f, 10f, 640f, 98f), text, style);
        }
#endif
    }
}
