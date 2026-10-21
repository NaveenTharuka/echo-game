using System.Collections;
using System.Collections.Generic;
using System.Text;
using Echo.AI.Tracker;
using UnityEngine;
using UnityEngine.AI;
using L = Echo.AI.Tracker.SandboxTrackerLayout;

namespace Echo.DevTools
{
    /// ECHO > Tracker > Calibrate Mover (Play Mode in Sandbox_TrackerGrid). Measures how long the real mover
    /// (NavMeshTrackerMover on a NavMeshAgent) takes to reach a node, so the Tracker's ETA model is fitted to the
    /// body it actually has instead of route / runSpeed.
    ///   Trials: both gaits, standing starts facing along and against the first leg, 0-2 turns, 6-48 m, plus
    ///   re-issued orders while already running (the decider re-plans mid-run).
    ///   Time = MoveTo issued -> mover's Arrived event (same event that puts the decider in Hold).
    ///   Fit per gait (least squares): t = t0 + d / v + k * turns. Logs v, t0, k, the residuals, and the error of
    ///   the current ETA model on every trial. The decider is paused while it runs; the Tracker is reset after.
    public class TrackerMoverCalibration : MonoBehaviour
    {
        struct Trial { public TrackerGait Gait; public bool Moving; public float Dist, Time, Predicted; public int Turns; public string Name; }

        readonly List<Trial> trials = new();
        TrackerStubAgent agent;
        NavMeshTrackerMover mover;
        NavMeshAgent nav;

        public static void Launch()
        {
            if (TrackerScenarios.RefuseIfBusy("mover calibration")) return;
            var run = new GameObject("_TrackerMoverCalibration").AddComponent<TrackerMoverCalibration>();
            TrackerScenarios.Claim(run, "mover calibration");
            run.StartCoroutine(run.RunAll());
        }

        static Vector3 C(int x, int z) => L.Centre(x, z);

        IEnumerator RunAll()
        {
            agent = FindFirstObjectByType<TrackerStubAgent>();
            if (agent == null || agent.Decider == null) { Debug.LogError("[TrackerMoverCalibration] Needs Sandbox_TrackerGrid in Play Mode."); Destroy(gameObject); yield break; }
            mover = agent.GetComponent<NavMeshTrackerMover>();
            nav = agent.GetComponent<NavMeshAgent>();
            Vector3 home = agent.Decider.Home;
            agent.enabled = false;                       // no think ticks: only this script drives the mover
            mover.Stop();

            var routes = new (Vector2Int from, Vector2Int to)[]
            {
                (new(0, 4), new(1, 4)), (new(0, 4), new(2, 4)), (new(0, 4), new(4, 4)), (new(0, 4), new(6, 4)), (new(0, 4), new(8, 4)),
                (new(4, 7), new(5, 4)), (new(4, 7), new(8, 4)), (new(4, 7), new(0, 4)), (new(4, 4), new(4, 8)),
                (new(1, 5), new(4, 6)), (new(0, 5), new(4, 7)), (new(7, 5), new(4, 8)),
            };
            foreach (var gait in new[] { TrackerGait.Run, TrackerGait.Walk })
            {
                foreach (var (from, to) in routes)
                {
                    yield return Standing(gait, C(from.x, from.y), C(to.x, to.y), true, $"{from}->{to}");
                    yield return Standing(gait, C(from.x, from.y), C(to.x, to.y), false, $"{from}->{to} turned away");
                }
                yield return Moving(gait, C(0, 4), C(8, 4), 2f, "(0,4)->(8,4) re-issued after 2 s");
                yield return Moving(gait, C(4, 7), C(8, 4), 2f, "(4,7)->(8,4) re-issued after 2 s");
            }

            agent.enabled = true;
            agent.ResetAgent(home, true);
            Debug.Log(Report());
            Destroy(gameObject);
        }

        IEnumerator Standing(TrackerGait gait, Vector3 from, Vector3 to, bool facing, string name)
        {
            mover.Stop();
            nav.Warp(from);
            Vector3 first = FirstLeg(from, to);
            agent.transform.rotation = Quaternion.LookRotation(facing ? first : -first);
            yield return new WaitForSeconds(0.4f);
            yield return Measure(gait, to, false, name);
        }

        IEnumerator Moving(TrackerGait gait, Vector3 from, Vector3 to, float after, string name)
        {
            mover.Stop();
            nav.Warp(from);
            agent.transform.rotation = Quaternion.LookRotation(FirstLeg(from, to));
            yield return new WaitForSeconds(0.4f);
            mover.ConsumeEvents(out _, out _);
            mover.MoveTo(to, gait);
            yield return new WaitForSeconds(after);
            yield return Measure(gait, to, true, name);
        }

        /// Issues MoveTo now and times it to the Arrived event. Distance and turns come from the mover's own plan.
        IEnumerator Measure(TrackerGait gait, Vector3 to, bool moving, string name)
        {
            mover.ConsumeEvents(out _, out _);
            Vector3 start = agent.transform.position;
            float t0 = Time.time;
            if (!mover.MoveTo(to, gait)) { Debug.LogWarning($"[TrackerMoverCalibration] {name}: MoveTo failed"); yield break; }
            PlanShape(start, out float dist, out int turns);
            bool arrived = false, failed = false;
            while (!arrived && !failed && Time.time < t0 + 40f)
            {
                yield return null;
                mover.ConsumeEvents(out arrived, out failed);
            }
            if (!arrived) { Debug.LogWarning($"[TrackerMoverCalibration] {name}: no arrival"); yield break; }
            var s = agent.settings;
            trials.Add(new Trial
            {
                Gait = gait, Moving = moving, Dist = dist, Turns = turns, Time = Time.time - t0, Name = name,
                Predicted = TrackerMotion.Eta(dist, Speed(gait), moving ? Speed(gait) : 0f, s)
            });
        }

        float Speed(TrackerGait gait) => gait == TrackerGait.Run ? agent.settings.runSpeed : agent.settings.walkSpeed;

        Vector3 FirstLeg(Vector3 from, Vector3 to)
        {
            var g = agent.graph.Graph;
            int a = g.Nearest(from), b = g.Nearest(to);
            Vector3 d = to - from;
            // First step of the graph route: the neighbour of a that is closest to b by route.
            var routes = agent.Decider.Routes;
            float best = float.PositiveInfinity;
            for (int k = 0; k < g.NeighbourCount(a); k++)
            {
                var e = g.Neighbour(a, k);
                float r = e.Length + routes.Field(b).Dist[e.To];
                if (r < best) { best = r; d = g.Position(e.To) - g.Position(a); }
            }
            d.y = 0f;
            return d.sqrMagnitude > 1e-4f ? d.normalized : Vector3.forward;
        }

        /// Route length the mover will walk (start -> remaining path nodes -> target) and its turns (> 30 deg).
        void PlanShape(Vector3 start, out float dist, out int turns)
        {
            var g = agent.graph.Graph;
            var pts = new List<Vector3> { Flat(start) };
            for (int i = mover.PathIndex; i < mover.Path.Count; i++) pts.Add(Flat(g.Position(mover.Path[i])));
            pts.Add(Flat(mover.Target));
            dist = 0f; turns = 0;
            Vector3 prevDir = Vector3.zero;
            for (int i = 1; i < pts.Count; i++)
            {
                Vector3 seg = pts[i] - pts[i - 1];
                float len = seg.magnitude;
                if (len < 0.05f) continue;
                dist += len;
                Vector3 dir = seg / len;
                if (prevDir != Vector3.zero && Vector3.Dot(prevDir, dir) < 0.866f) turns++;
                prevDir = dir;
            }
        }

        static Vector3 Flat(Vector3 p) { p.y = 0f; return p; }

        string Report()
        {
            var sb = new StringBuilder("[TrackerMoverCalibration]\n");
            sb.AppendLine($"NavMeshAgent: accel {nav.acceleration} m/s2, angular {nav.angularSpeed} deg/s, stopping {nav.stoppingDistance} m, " +
                          $"autoBraking {nav.autoBraking}; mover arrive radius {mover.arriveRadius} m, waypoint radius {mover.waypointRadius} m");
            foreach (var gait in new[] { TrackerGait.Run, TrackerGait.Walk })
            {
                float nominal = gait == TrackerGait.Run ? agent.settings.runSpeed : agent.settings.walkSpeed;
                sb.AppendLine($"--- {gait} (nominal {nominal} m/s)");
                sb.AppendLine($"{"trial",-36} {"d m",6} {"turns",5} {"meas s",7} {"d/v s",6} {"model s",7}");
                var rows = new List<Trial>();
                foreach (var t in trials) if (t.Gait == gait) rows.Add(t);
                foreach (var t in rows)
                    sb.AppendLine($"{t.Name,-36} {t.Dist,6:0.0} {t.Turns,5} {t.Time,7:0.00} {t.Dist / nominal,6:0.00} {t.Predicted,7:0.00}");

                // Standing starts: t = t0 + d * (1/v) + k * turns (3-parameter least squares).
                var st = rows.FindAll(t => !t.Moving);
                if (Fit(st, out float t0, out float invV, out float k, out float rms, out float maxRes))
                    sb.AppendLine($"fit standing: v = {1f / invV:0.00} m/s, t0 = {t0:0.00} s, k = {k:0.00} s/turn, rms {rms:0.00} s, max |res| {maxRes:0.00} s");
                ErrorStats(st, nominal, false, sb, "standing");
                ErrorStats(rows.FindAll(t => t.Moving), nominal, true, sb, "moving");
            }
            return sb.ToString();
        }

        static void ErrorStats(List<Trial> rows, float nominal, bool moving, StringBuilder sb, string label)
        {
            if (rows.Count == 0) return;
            float oldSum = 0f, oldMax = 0f, newSum = 0f, newMax = 0f;
            foreach (var t in rows)
            {
                float eo = t.Time - t.Dist / nominal, en = t.Time - t.Predicted;
                oldSum += eo; newSum += en;
                oldMax = Mathf.Max(oldMax, Mathf.Abs(eo)); newMax = Mathf.Max(newMax, Mathf.Abs(en));
            }
            sb.AppendLine($"{label}: measured - d/v mean {oldSum / rows.Count:+0.00;-0.00} s (max {oldMax:0.00}); " +
                          $"measured - model mean {newSum / rows.Count:+0.00;-0.00} s (max {newMax:0.00})");
        }

        /// Ordinary least squares for t = a + b d + c turns (normal equations, 3 x 3).
        static bool Fit(List<Trial> rows, out float a, out float b, out float c, out float rms, out float maxRes)
        {
            a = b = c = rms = maxRes = 0f;
            if (rows.Count < 4) return false;
            double[,] m = new double[3, 4];
            foreach (var t in rows)
            {
                double[] x = { 1, t.Dist, t.Turns };
                for (int i = 0; i < 3; i++) { for (int j = 0; j < 3; j++) m[i, j] += x[i] * x[j]; m[i, 3] += x[i] * t.Time; }
            }
            for (int p = 0; p < 3; p++)
            {
                int piv = p;
                for (int r = p + 1; r < 3; r++) if (System.Math.Abs(m[r, p]) > System.Math.Abs(m[piv, p])) piv = r;
                if (System.Math.Abs(m[piv, p]) < 1e-9) return false;
                for (int j = 0; j < 4; j++) (m[p, j], m[piv, j]) = (m[piv, j], m[p, j]);
                for (int r = 0; r < 3; r++)
                {
                    if (r == p) continue;
                    double f = m[r, p] / m[p, p];
                    for (int j = p; j < 4; j++) m[r, j] -= f * m[p, j];
                }
            }
            a = (float)(m[0, 3] / m[0, 0]); b = (float)(m[1, 3] / m[1, 1]); c = (float)(m[2, 3] / m[2, 2]);
            double ss = 0;
            foreach (var t in rows)
            {
                float res = t.Time - (a + b * t.Dist + c * t.Turns);
                ss += res * res; maxRes = Mathf.Max(maxRes, Mathf.Abs(res));
            }
            rms = (float)System.Math.Sqrt(ss / rows.Count);
            return true;
        }
    }
}
