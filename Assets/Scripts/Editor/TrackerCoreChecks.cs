using System;
using System.Collections.Generic;
using System.Diagnostics;
using Echo.AI.Tracker;
using UnityEngine;

namespace Echo.EditorTools
{
    /// ECHO > Tracker > Run Core Checks. Synthetic-trail checks of the Tracker's pure C# core
    /// (no scene, no Play Mode). Each failure logs the observed values.
    public static class TrackerCoreChecks
    {
#if UNITY_EDITOR
        [UnityEditor.MenuItem("ECHO/Tracker/Run Core Checks")]
        static void RunFromMenu()
        {
            var lines = new List<string>();
            int failed = RunAll(lines.Add);
            string report = string.Join("\n", lines);
            if (failed == 0) UnityEngine.Debug.Log($"[TrackerCoreChecks] all passed\n{report}");
            else UnityEngine.Debug.LogError($"[TrackerCoreChecks] {failed} failed\n{report}");
        }
#endif

        static Action<string> log;
        static int failures, total;

        public static int RunAll(Action<string> logger)
        {
            log = logger; failures = 0; total = 0;
            StraightRun();
            CredibilityGate();
            ScorerPrefersAhead();
            EtaFollowsGait();
            AdvantageModes();
            ConfidenceRisesAndDecays();
            BestFirstOrder();
            DeciderEdgeCases();
            GoalCommitment();
            Performance();
            log($"{total - failures}/{total} checks passed");
            return failures;
        }

        static void Check(string name, bool ok, string observed)
        {
            total++;
            if (!ok) failures++;
            log($"{(ok ? "PASS" : "FAIL")}  {name}  ({observed})");
        }

        // ---------- worlds ----------

        static TrackerGraph Corridor(float length, float step)
        {
            var g = new TrackerGraph();
            for (float x = 0f; x <= length + 1e-3f; x += step)
            {
                int i = g.AddNode(new Vector3(x, 0f, 0f));
                if (i > 0) g.Connect(i - 1, i);
            }
            return g;
        }

        /// Centre (0,0,0) + four arms of `nodes` nodes, `step` apart. Optional door on centre<->first east node.
        static TrackerGraph Plus(int nodes, float step, DoorState eastDoor, out int[] east)
        {
            var g = new TrackerGraph();
            int centre = g.AddNode(Vector3.zero);
            east = new int[nodes];
            Vector3[] dirs = { Vector3.right, Vector3.left, Vector3.forward, Vector3.back };
            for (int d = 0; d < 4; d++)
            {
                int prev = centre;
                for (int k = 1; k <= nodes; k++)
                {
                    int i = g.AddNode(dirs[d] * (k * step));
                    int door = d == 0 && k == 1 && eastDoor != DoorState.None ? g.AddDoor(eastDoor, 0.6f) : -1;
                    g.Connect(prev, i, door);
                    if (d == 0) east[k - 1] = i;
                    prev = i;
                }
            }
            return g;
        }

        static TrackerGraph Grid(int nx, int nz, float step)
        {
            var g = new TrackerGraph();
            for (int z = 0; z < nz; z++)
            for (int x = 0; x < nx; x++)
            {
                int i = g.AddNode(new Vector3(x * step, 0f, z * step));
                if (x > 0) g.Connect(i - 1, i);
                if (z > 0) g.Connect(i - nx, i);
            }
            return g;
        }

        static float Jitter(int i) => 0.3f * Mathf.Sin(i * 1.7f);

        static ObservationResult Feed(TrackerMemory m, ITrackerWorld w, RouteService r, Vector3 p, float t, float err = 0.5f)
            => m.Add(new TrackerPercept(p, err, t, HeardKind.Footstep), w.Nearest(p), r, out _);

        // ---------- checks ----------

        static void StraightRun()
        {
            var s = new TrackerSettings();
            var g = Corridor(80f, 2f);
            var r = new RouteService(g, s);
            var m = new TrackerMemory(s);
            for (int i = 0; i <= 10; i++)
            {
                float t = i * 0.5f;
                Feed(m, g, r, new Vector3(2f + 3f * t + Jitter(i), 0f, 0f), t);
            }
            bool ok = VelocityEstimator.TryEstimate(m, 5f, s, out var v, out float rms);
            Check("straight run 3 m/s gives v = (3,0,0)", ok && Mathf.Abs(v.x - 3f) < 0.15f && Mathf.Abs(v.z) < 0.1f,
                  $"v={v.x:0.00},{v.z:0.00} rms={rms:0.00} obs={m.Count}");
            Check("memory keeps the last 8", m.Count == 8, $"count={m.Count}");

            m.Clear();
            for (int i = 0; i <= 6; i++) Feed(m, g, r, new Vector3(9f * i * 0.5f, 0f, 0f), i * 0.5f);
            VelocityEstimator.TryEstimate(m, 3f, s, out v, out _);
            Check("velocity capped at v_max", Mathf.Abs(v.magnitude - s.maxPlayerSpeed) < 1e-3f, $"|v|={v.magnitude:0.00}");

            // Three 4 m-error sounds over 1 s on a 4.5 m/s line: the shrunk v is slow (steady heading, humble
            // prediction); the ETA_player speeds are not shrunk towards 0 (else adv is optimistic, Scenario a, 15 Oct):
            // raw = the data, moving = between the data and the 3 m/s prior.
            m.Clear();
            for (int i = 0; i < 3; i++) Feed(m, g, r, new Vector3(10f + 2.25f * i, 0f, 0f), i * 0.5f, 4f);
            VelocityEstimator.TryEstimate(m, 1f, s, out v, out _, out float raw, out float moving, out _);
            Check("noisy short trail: v shrunk; ETA_player speeds raw = 4.5, moving between prior 3 and 4.5",
                  v.magnitude < 2f && Mathf.Abs(raw - 4.5f) < 0.05f && moving > s.velocityPrior && moving < raw,
                  $"|v|={v.magnitude:0.00} raw={raw:0.00} moving={moving:0.00}");

            m.Clear();
            Feed(m, g, r, new Vector3(10f, 0f, 0f), 0f);
            Check("single sound gives no velocity", !VelocityEstimator.TryEstimate(m, 0.5f, s, out _, out _), $"obs={m.Count}");
        }

        static void CredibilityGate()
        {
            var s = new TrackerSettings();
            var g = Corridor(80f, 2f);
            var r = new RouteService(g, s);
            var m = new TrackerMemory(s);
            for (int i = 0; i <= 10; i++) { float t = i * 0.5f; Feed(m, g, r, new Vector3(2f + 3f * t, 0f, 0f), t); }
            float lastT = m.Last.Time;

            var res = Feed(m, g, r, new Vector3(70f, 0f, 0f), 5.3f);
            Check("teleport (53 m in 0.3 s) rejected", res == ObservationResult.Suspect && m.Last.Time == lastT,
                  $"result={res} last.t={m.Last.Time} suspects={m.SuspectCount}");

            res = Feed(m, g, r, new Vector3(0f, 0f, 0f), 5.6f);
            Check("two disagreeing suspects do not reset", res == ObservationResult.Suspect && m.Last.Time == lastT,
                  $"result={res} suspects={m.SuspectCount}");

            res = Feed(m, g, r, new Vector3(71f, 0f, 0f), 5.9f);
            Check("two agreeing suspects reset the trail", res == ObservationResult.Reset && m.Count == 2 && m.Last.Position.x == 71f,
                  $"result={res} count={m.Count} last.x={m.Last.Position.x}");

            var filtered = m.Add(new TrackerPercept(new Vector3(72f, 0f, 0f), 0.5f, 6f, HeardKind.Generator), 36, r, out _);
            Check("generator noise filtered", filtered == ObservationResult.Filtered && m.Count == 2, $"result={filtered}");

            // Wall check: two corridors 3 m apart in a straight line, 100 m apart by route.
            var u = new TrackerGraph();
            int a0 = u.AddNode(new Vector3(0, 0, 0)), a1 = u.AddNode(new Vector3(50, 0, 0)), a2 = u.AddNode(new Vector3(50, 0, 3)), a3 = u.AddNode(new Vector3(0, 0, 3));
            u.Connect(a0, a1); u.Connect(a1, a2); u.Connect(a2, a3);
            var ru = new RouteService(u, s);
            var mu = new TrackerMemory(s);
            Feed(mu, u, ru, new Vector3(0, 0, 0), 0f);
            res = Feed(mu, u, ru, new Vector3(0, 0, 3), 1f);
            Check("sound 3 m away through a wall (103 m by route) rejected", res == ObservationResult.Suspect, $"result={res}");
        }

        static void ScorerPrefersAhead()
        {
            var s = new TrackerSettings();
            // Player walks east along the west arm (~2.75 m/s), last heard 5 m before the junction.
            // Tracker on the south arm: far (20 m) = it cannot beat the player anywhere near the prediction;
            // near (8 m) = it can. Locked = door between the junction and the east arm.
            (string name, float selfZ, DoorState door)[] cases =
                { ("far", -20f, DoorState.None), ("near", -8f, DoorState.None), ("locked", -8f, DoorState.Locked) };
            foreach (var (name, selfZ, door) in cases)
            {
                var g = Plus(5, 4f, door, out var east);
                var r = new RouteService(g, s);
                var m = new TrackerMemory(s);
                float[] xs = { -16f, -12f, -8f, -5f }; float[] ts = { 0f, 1.5f, 3f, 4f };
                for (int i = 0; i < xs.Length; i++) Feed(m, g, r, new Vector3(xs[i], 0f, 0f), ts[i]);
                VelocityEstimator.TryEstimate(m, 4f, s, out var v, out _);
                var heat = new HeatMap(); heat.EnsureSize(g.Count);
                var sc = new InterceptScorer();
                Vector3 self = new(0f, 0f, selfZ);
                bool ok = sc.Evaluate(r, m.Last, v, self, g.Nearest(self), 4f, heat, null, s);
                Vector3 best = ok ? g.Position(sc.Best) : Vector3.zero;
                string seen = $"best={best} adv={(ok ? sc.Advantage[sc.Best] : 0f):0.00} S={sc.BestScore:0.00} " +
                              $"margin={sc.Margin:0.00} pred={sc.Predicted} candidates={sc.CandidateCount}";
                if (name == "far")
                    Check("far Tracker: picks the node ahead on the player's line", ok && best.x > -5f && Mathf.Abs(best.z) < 0.01f, seen);
                else if (name == "near")
                    Check("near Tracker: picks a node it reaches first (adv > 0)", ok && sc.Advantage[sc.Best] > 0f, seen);
                else
                {
                    bool anyEast = false;
                    foreach (int e in east) anyEast |= sc.IsCandidate[e];
                    Check("locked door drops every candidate behind it", ok && !anyEast && best.x <= 0f, seen);
                }
            }
        }

        /// ETA_tracker is the calibrated motion model at the gait the Tracker will really use (Scenario a, 15 Oct:
        /// the phase-3 ETA assumed run speed while the Tracker walked in the Walk band).
        static void EtaFollowsGait()
        {
            var s = new TrackerSettings();
            float standRun = TrackerMotion.Eta(10f, s.runSpeed, 0f, s), movingRun = TrackerMotion.Eta(10f, s.runSpeed, s.runSpeed, s);
            float standWalk = TrackerMotion.Eta(10f, s.walkSpeed, 0f, s);
            float expRun = (10f - s.etaArrive) / s.runSpeed, expWalk = (10f - s.etaArrive) / s.walkSpeed;
            Check("motion model: (route - arrive) / v + startup only from a standstill",
                  Mathf.Abs(standRun - (expRun + s.etaStartup)) < 1e-4f && Mathf.Abs(movingRun - expRun) < 1e-4f &&
                  Mathf.Abs(standWalk - (expWalk + s.etaStartup)) < 1e-4f,
                  $"10 m: run standing {standRun:0.000} s, run moving {movingRun:0.000} s, walk standing {standWalk:0.000} s");

            // Same trail as the "near" scorer case: the node the run ETA intercepts is worth less at a walk.
            var g = Plus(5, 4f, DoorState.None, out _);
            var r = new RouteService(g, s);
            var m = new TrackerMemory(s);
            float[] xs = { -16f, -12f, -8f, -5f }; float[] ts = { 0f, 1.5f, 3f, 4f };
            for (int i = 0; i < xs.Length; i++) Feed(m, g, r, new Vector3(xs[i], 0f, 0f), ts[i]);
            VelocityEstimator.TryEstimate(m, 4f, s, out var v, out _);
            var heat = new HeatMap(); heat.EnsureSize(g.Count);
            Vector3 self = new(0f, 0f, -8f);
            var sc = new InterceptScorer();
            sc.Evaluate(r, m.Last, v, self, g.Nearest(self), 4f, heat, null, s, s.runSpeed);
            int pick = sc.Best; float advRun = pick >= 0 ? sc.Advantage[pick] : 0f;
            sc.Evaluate(r, m.Last, v, self, g.Nearest(self), 4f, heat, null, s, s.walkSpeed);
            float advWalk = pick >= 0 && sc.IsCandidate[pick] ? sc.Advantage[pick] : float.NegativeInfinity;
            Check("walking Tracker: the run-speed interception node loses its advantage",
                  pick >= 0 && advRun > 0f && advWalk < advRun,
                  $"node {(pick >= 0 ? g.Position(pick).ToString() : "-")}: adv run {advRun:0.00}, walk {advWalk:0.00}");

            // The decider scores at the speed of the gait it issues. runToIntercept (adopted): a claimed interception
            // (adv > 0) is run to and scored at run speed, also in the Walk band where the phase-3 ETA was wrong;
            // without it the Walk band walks and is scored at walk speed.
            (string name, float[] x, float[] t, bool runTo, ConfidenceBand band, TrackerGait expect)[] trails =
            {
                ("4 sounds", xs, ts, true, ConfidenceBand.Run, TrackerGait.Run),
                ("2 sounds", new[] { -9f, -5f }, new[] { 2.5f, 4f }, true, ConfidenceBand.Walk, TrackerGait.Run),
                ("2 sounds, runToIntercept off", new[] { -9f, -5f }, new[] { 2.5f, 4f }, false, ConfidenceBand.Walk, TrackerGait.Walk),
            };
            foreach (var (name, tx, tt, runTo, expectBand, expect) in trails)
            {
                var sim = new Sim(g, self) { AutoArrive = false };
                sim.D.Settings.runToIntercept = runTo;
                for (int i = 0; i < tx.Length; i++)
                    sim.Percepts.Add(new TrackerPercept(new Vector3(tx[i], 0f, 0f), 0.5f, tt[i], HeardKind.Run));
                sim.Now = 3.8f; sim.Tick();                        // Analyse
                var c = sim.Tick();                                 // Intercept: MoveTo
                var d = sim.D;
                bool consistent = false; string seen = $"state={d.State} cmd={c.Kind} C={d.Confidence.Value:0.00}";
                if (c.Kind == TrackerCommandKind.MoveTo && d.HasVelocity && d.GoalNode >= 0)
                {
                    var check = new InterceptScorer();
                    float speed = c.Gait == TrackerGait.Run ? s.runSpeed : s.walkSpeed;
                    check.Evaluate(d.Routes, d.Anchor, d.Velocity, sim.Self, g.Nearest(sim.Self), sim.Now, d.Heat, null, s, speed,
                                   playerSpeed: d.PlayerSpeed);
                    consistent = c.Gait == expect && d.Band == expectBand && check.Best == d.GoalNode &&
                                 Mathf.Abs(check.Advantage[d.GoalNode] - d.Scorer.Advantage[d.GoalNode]) < 1e-4f;
                    seen = $"gait={c.Gait} band={d.Band} C={d.Confidence.Value:0.00} goal={g.Position(d.GoalNode)} " +
                           $"adv={d.Scorer.Advantage[d.GoalNode]:0.00}, rescored at {speed} m/s: best={(check.Best >= 0 ? g.Position(check.Best).ToString() : "-")} " +
                           $"adv={check.Advantage[d.GoalNode]:0.00}";
                }
                Check($"decider ({name}): {expectBand} band issues {expect}; goal and advantage are those of that gait", consistent, seen);
            }
        }

        /// Time advantage as reward vs constraint on the phase-1 "near" case (the reward picks the side node (0,0,-4)
        /// beside the junction). Constraint modes must pick a feasible (claimed) node or fall back without a claim;
        /// ConstraintOnLine's claim must lie on the player's line.
        static void AdvantageModes()
        {
            var g = Plus(5, 4f, DoorState.None, out _);
            float[] xs = { -16f, -12f, -8f, -5f }; float[] ts = { 0f, 1.5f, 3f, 4f };
            Vector3 self = new(0f, 0f, -8f);
            foreach (var mode in new[] { AdvantageMode.Reward, AdvantageMode.Saturated, AdvantageMode.Constraint, AdvantageMode.ConstraintOnLine })
            {
                var s = new TrackerSettings { advMode = mode };
                var r = new RouteService(g, s);
                var m = new TrackerMemory(s);
                for (int i = 0; i < xs.Length; i++) Feed(m, g, r, new Vector3(xs[i], 0f, 0f), ts[i]);
                VelocityEstimator.TryEstimate(m, 4f, s, out var v, out _);
                var heat = new HeatMap(); heat.EnsureSize(g.Count);
                var sc = new InterceptScorer();
                bool has = sc.Evaluate(r, m.Last, v, self, g.Nearest(self), 4f, heat, null, s, s.runSpeed);
                int b = sc.Best;
                Vector3 off = b >= 0 ? g.Position(b) - m.Last.Position : Vector3.zero; off.y = 0f;
                float cos = off.magnitude > 0.5f ? Vector3.Dot(v.normalized, off.normalized) : 0f;
                bool ok = has && b >= 0 && mode switch
                {
                    AdvantageMode.Saturated => sc.Advantage[b] <= s.advCap + 1e-5f,
                    AdvantageMode.Constraint => sc.Fallback ? !sc.Claimed[b] : sc.Claimed[b] && sc.Advantage[b] > 0f,
                    AdvantageMode.ConstraintOnLine => sc.Fallback ? !sc.Claimed[b] : sc.Claimed[b] && cos >= s.onLineCos,
                    _ => sc.Claimed[b] == sc.Advantage[b] > 0f,
                };
                Check($"advantage mode {mode}: pick is consistent with the mode", ok,
                      $"best={(b >= 0 ? g.Position(b).ToString() : "-")} adv={(b >= 0 ? sc.Advantage[b] : 0f):0.00} cos={cos:0.00} " +
                      $"claimed={(b >= 0 && sc.Claimed[b])} fallback={sc.Fallback} S={sc.BestScore:0.00}");
            }
        }

        static float Confidence(TrackerMemory m, RouteService r, InterceptScorer sc, HeatMap heat, Vector3 self, float now, TrackerSettings s)
        {
            bool hasV = VelocityEstimator.TryEstimate(m, now, s, out var v, out float rms);
            float margin = 1f;
            if (hasV) { sc.Evaluate(r, m.Last, v, self, r.World.Nearest(self), now, heat, null, s); margin = sc.Margin; }
            return ConfidenceModel.Compute(now - m.Last.Time, m.Count, hasV, rms, margin, s).Value;
        }

        static void ConfidenceRisesAndDecays()
        {
            var s = new TrackerSettings();
            var g = Plus(6, 4f, DoorState.None, out _);
            var r = new RouteService(g, s);
            var m = new TrackerMemory(s);
            var heat = new HeatMap(); heat.EnsureSize(g.Count);
            var sc = new InterceptScorer();
            Vector3 self = new(0f, 0f, -24f);
            var cs = new List<float>();
            for (int i = 0; i < 6; i++)
            {
                float t = i * 0.6f;
                Feed(m, g, r, new Vector3(-22f + 3f * t + Jitter(i), 0f, 0f), t);
                cs.Add(Confidence(m, r, sc, heat, self, t, s));
            }
            bool rising = cs[cs.Count - 1] > cs[0] + 0.1f;
            for (int i = 2; i < cs.Count; i++) rising &= cs[i] >= cs[i - 1] - 0.05f;
            Check("confidence rises with consistent evidence", rising, "C=" + string.Join(" ", cs.ConvertAll(c => c.ToString("0.00"))));

            float tl = m.Last.Time;
            float[] ages = { 0f, 2f, 4f, 8f, 16f };
            var ds = new List<float>();
            foreach (float a in ages) ds.Add(Confidence(m, r, sc, heat, self, tl + a, s));
            bool decays = true;
            for (int i = 1; i < ds.Count; i++) decays &= ds[i] < ds[i - 1];
            Check("confidence decays with age", decays && ds[ds.Count - 1] < s.walkThreshold,
                  "C(age 0,2,4,8,16)=" + string.Join(" ", ds.ConvertAll(c => c.ToString("0.00"))));
        }

        static void BestFirstOrder()
        {
            var s = new TrackerSettings();
            var g = Grid(7, 7, 4f);
            var r = new RouteService(g, s);
            var heat = new HeatMap(); heat.EnsureSize(g.Count);
            int centre = g.Nearest(new Vector3(12f, 0f, 12f));
            var last = new TrackerObservation { Position = g.Position(centre), Time = 0f, Error = 0.5f, Credibility = 1f, Node = centre };
            var search = new ColdTrailSearch();
            search.Begin(r, last, new Vector3(3f, 0f, 0f), heat, 2f, s);

            bool order = true; var visits = new List<int>();
            for (int step = 0; step < s.searchMaxNodes; step++)
            {
                int expected = -1; float bp = float.NegativeInfinity;
                for (int i = 0; i < g.Count; i++)
                    if (search.IsQueued(i) && !search.IsVisited(i) && search.Priority(i) > bp) { bp = search.Priority(i); expected = i; }
                int got = search.Next(2f);
                visits.Add(got);
                order &= got == expected;
            }
            int after = search.Next(2f);
            bool eastFirst = visits.Count > 1 && g.Position(visits[1]).x > g.Position(centre).x;
            Check("best-first visits the highest-priority frontier node each step", order,
                  "visits=" + string.Join(",", visits));
            Check("first move follows the player's direction; stops after 6 nodes", eastFirst && after == -1,
                  $"second={(visits.Count > 1 ? g.Position(visits[1]).ToString() : "-")} after={after}");
        }

        /// Tiny simulation: a MoveTo teleports the agent to the target and reports Arrived next tick.
        sealed class Sim
        {
            public readonly TrackerDecider D;
            public readonly List<TrackerPercept> Percepts = new();
            public Vector3 Self;
            public float Now;
            public bool AutoArrive = true, Fail;
            public TrackerCommand Last;
            bool arrive;

            public Sim(ITrackerWorld w, Vector3 self) { Self = self; D = new TrackerDecider(w, new TrackerSettings(), self); }

            public TrackerCommand Tick(float dt = 0.2f)
            {
                Now += dt;
                var input = new TrackerTickInput { Now = Now, Self = Self, Arrived = arrive, Failed = Fail };
                arrive = false; Fail = false;
                Last = D.Tick(input, Percepts);
                Percepts.Clear();
                if (Last.Kind == TrackerCommandKind.MoveTo && AutoArrive) { Self = Last.Target; arrive = true; }
                return Last;
            }
        }

        static void DeciderEdgeCases()
        {
            var g = Corridor(80f, 2f);

            // No history: stays idle.
            var sim = new Sim(g, Vector3.zero);
            var c = sim.Tick();
            Check("no history: Idle, no command", sim.D.State == TrackerState.Idle && c.Kind == TrackerCommandKind.None, $"state={sim.D.State} cmd={c.Kind}");

            // Single sound: Analyse (stop, listen) then investigate it at a walk.
            sim.AutoArrive = false;
            sim.Percepts.Add(new TrackerPercept(new Vector3(30f, 0f, 0f), 1f, sim.Now + 0.1f, HeardKind.Footstep));
            c = sim.Tick();
            var analyse = sim.D.State;
            c = sim.Tick();
            Check("single sound: Analyse, then walk to it",
                  analyse == TrackerState.Analyse && sim.D.State == TrackerState.Intercept && c.Kind == TrackerCommandKind.MoveTo &&
                  Mathf.Abs(c.Target.x - 30f) < 0.01f && c.Gait == TrackerGait.Walk,
                  $"{analyse}->{sim.D.State} cmd={c.Kind} target={c.Target} gait={c.Gait} C={sim.D.Confidence.Value:0.00}");

            // Silence: confidence decays -> Search -> Idle with memory cleared.
            sim.AutoArrive = true;
            bool searched = false; float tSearch = -1f;
            for (int i = 0; i < 300 && !(searched && sim.D.State == TrackerState.Idle); i++)
            {
                sim.Tick();
                if (sim.D.State == TrackerState.Search && !searched) { searched = true; tSearch = sim.Now; }
            }
            Check("player goes quiet: Search, then Idle, memory cleared",
                  searched && sim.D.State == TrackerState.Idle && sim.D.Memory.Count == 0,
                  $"search at t={tSearch:0.0}s, idle at t={sim.Now:0.0}s, state={sim.D.State} mem={sim.D.Memory.Count}");

            // Several sounds in one tick, out of order.
            sim = new Sim(g, Vector3.zero) { AutoArrive = false };
            sim.Percepts.Add(new TrackerPercept(new Vector3(12f, 0f, 0f), 0.5f, 1.0f, HeardKind.Footstep));
            sim.Percepts.Add(new TrackerPercept(new Vector3(10f, 0f, 0f), 0.5f, 0.6f, HeardKind.Footstep));
            sim.Percepts.Add(new TrackerPercept(new Vector3(11f, 0f, 0f), 0.5f, 0.8f, HeardKind.Footstep));
            sim.Now = 1f; sim.Tick();
            Check("several sounds in one tick are taken in time order",
                  sim.D.Memory.Count == 3 && sim.D.Memory.Last.Time == 1.0f && sim.D.Suspects == 0,
                  $"mem={sim.D.Memory.Count} last.t={sim.D.Memory.Last.Time} suspects={sim.D.Suspects}");

            // Trail -> Intercept at a run; then the path fails: node banned, new goal.
            sim = new Sim(g, new Vector3(70f, 0f, 0f)) { AutoArrive = false };
            for (int i = 0; i < 8; i++)
            {
                sim.Percepts.Add(new TrackerPercept(new Vector3(10f + 3f * sim.Now, 0f, 0f), 0.5f, sim.Now, HeardKind.Run));
                sim.Tick(0.5f);
            }
            var before = sim.D.State; int goal = sim.D.GoalNode; var gait = sim.Last.Gait;
            sim.Fail = true;
            sim.Percepts.Add(new TrackerPercept(new Vector3(10f + 3f * sim.Now, 0f, 0f), 0.5f, sim.Now, HeardKind.Run));
            c = sim.Tick(0.2f);
            Check("consistent trail: Intercept at a run",
                  before == TrackerState.Intercept && sim.D.HasVelocity && sim.D.Band == ConfidenceBand.Run,
                  $"state={before} v={sim.D.Velocity} C={sim.D.Confidence.Value:0.00} band={sim.D.Band} gait={gait}");
            Check("path fails mid-route: node banned, new goal",
                  sim.D.IsBanned(goal) && c.Kind == TrackerCommandKind.MoveTo && sim.D.GoalNode != goal,
                  $"old={goal} banned={sim.D.IsBanned(goal)} new={sim.D.GoalNode} cmd={c.Kind}");

            // Sound right next to the Tracker -> Attack.
            sim.Percepts.Add(new TrackerPercept(sim.Self + new Vector3(1f, 0f, 0f), 0.5f, sim.Now, HeardKind.Footstep));
            c = sim.Tick();
            Check("close precise sound: Attack", c.Kind == TrackerCommandKind.Attack && sim.D.State == TrackerState.Attack,
                  $"cmd={c.Kind} state={sim.D.State}");

            // Player dies -> reset (the Tracker is away from home).
            sim.Self = new Vector3(20f, 0f, 0f);
            sim.D.Reset();
            c = sim.Tick();
            Check("player death: reset to Idle, memory and heat cleared, going home",
                  sim.D.State == TrackerState.Idle && sim.D.Memory.Count == 0 && c.Kind == TrackerCommandKind.MoveTo && c.Target == sim.D.Home,
                  $"state={sim.D.State} mem={sim.D.Memory.Count} cmd={c.Kind}");
        }

        /// Re-planning loop (15 Oct, scenario a in Unity): a runner crosses the Sandbox_Tracker corridor, heard every
        /// 0.5 s with 3-5 m error. Under the phase-3 rule (switch on any > replanDistance move, raw latest percept as the
        /// anchor) the goal flips back and forth between neighbouring nodes (A -> B -> A). Commitment + fitted anchor
        /// must remove most of those flips and still bring the Tracker as close to the runner (straight-line mover).
        static void GoalCommitment()
        {
            var old = new TrackerSettings { switchMargin = 0f, fitAnchor = false };
            RunCommitment(old, out int oldFlips, out int oldMoves, out float oldNear);
            RunCommitment(new TrackerSettings(), out int flips, out int moves, out float near);
            string seen = $"20 runs; phase-3 rule: {oldFlips} A-B-A flips, {oldMoves} MoveTos, mean closest approach {oldNear:0.0} m; " +
                          $"now: {flips} flips, {moves} MoveTos, {near:0.0} m";
            Check("noisy trail reproduces the re-planning loop under the phase-3 rule", oldFlips >= 5, seen);
            Check("commitment: flips cut by >= 4x, the Tracker gets as close to the runner", flips * 4 <= oldFlips && near <= oldNear + 0.5f, seen);
        }

        static void RunCommitment(TrackerSettings s, out int flips, out int moves, out float approach)
        {
            var g = SandboxTrackerLayout.BuildGraph(out _);
            flips = moves = 0; approach = 0f;
            var percepts = new List<TrackerPercept>();
            for (int seed = 0; seed < 20; seed++)
            {
                var rng = new System.Random(seed);
                Vector3 self = SandboxTrackerLayout.Centre(4, 5), runner = SandboxTrackerLayout.Centre(0, 4);
                var d = new TrackerDecider(g, s, self);
                int g1 = -1, g2 = -1; float speed = 0f, nextSound = 0f, closest = float.MaxValue; Vector3 goal = self;
                for (float t = 0.2f; t < 14f; t += 0.2f)
                {
                    runner.x = Mathf.Min(runner.x + 3f * 0.2f, SandboxTrackerLayout.Centre(8, 4).x);
                    if (t >= nextSound)
                    {
                        nextSound = t + 0.5f;
                        float err = 3f + 2f * (float)rng.NextDouble(), ang = (float)(rng.NextDouble() * Math.PI * 2);
                        float rad = err * Mathf.Sqrt((float)rng.NextDouble());
                        percepts.Add(new TrackerPercept(runner + new Vector3(Mathf.Cos(ang) * rad, 0f, Mathf.Sin(ang) * rad), err, t, HeardKind.Run));
                    }
                    closest = Mathf.Min(closest, Vector3.Distance(self, runner));
                    bool arrived = speed > 0f && self == goal;
                    var c = d.Tick(new TrackerTickInput { Now = t, Self = self, Arrived = arrived, SelfSpeed = speed }, percepts);
                    percepts.Clear();
                    if (c.Kind == TrackerCommandKind.MoveTo) { goal = c.Target; speed = c.Gait == TrackerGait.Run ? s.runSpeed : 2.2f; }
                    else if (c.Kind == TrackerCommandKind.Stop) speed = 0f;
                    if (c.Kind == TrackerCommandKind.MoveTo && d.State == TrackerState.Intercept)
                    {
                        moves++;
                        if (d.LastReplan == TrackerReplan.GoalMoved && d.GoalNode == g2) flips++;
                        if (d.GoalNode != g1) { g2 = g1; g1 = d.GoalNode; }
                    }
                    self = Vector3.MoveTowards(self, goal, speed * 0.2f);
                }
                approach += closest / 20f;
            }
        }

        static void Performance()
        {
            var g = Grid(20, 20, 3f);                    // 400 nodes, 760 edges
            var sim = new Sim(g, new Vector3(30f, 0f, 30f)) { AutoArrive = false };
            var sw = new Stopwatch();
            double maxMs = 0; int measured = 0; long bytes = 0;
            for (int i = 0; i < 2200; i++)
            {
                if (i % 2 == 0)
                {
                    float x = (sim.Now * 3f) % 57f;
                    sim.Percepts.Add(new TrackerPercept(new Vector3(x, 0f, 15f + (i / 300) * 3f), 0.8f, sim.Now, HeardKind.Footstep));
                }
                if (sim.Last.Kind == TrackerCommandKind.MoveTo)
                    sim.Self = Vector3.MoveTowards(sim.Self, sim.Last.Target, 5f * 0.2f);
                bool measure = i >= 200;
                long b0 = measure ? GC.GetAllocatedBytesForCurrentThread() : 0;
                sw.Restart();
                sim.Tick();
                sw.Stop();
                if (!measure) continue;
                bytes += GC.GetAllocatedBytesForCurrentThread() - b0;
                measured++;
                maxMs = Math.Max(maxMs, sw.Elapsed.TotalMilliseconds);
            }
            double avgMs = 0;
            // Second pass for a clean average (the first pass includes the allocation counter calls).
            sw.Restart();
            for (int i = 0; i < 2000; i++)
            {
                if (i % 2 == 0)
                {
                    float x = (sim.Now * 3f) % 57f;
                    sim.Percepts.Add(new TrackerPercept(new Vector3(x, 0f, 15f), 0.8f, sim.Now, HeardKind.Footstep));
                }
                sim.Tick();
            }
            sw.Stop();
            avgMs = sw.Elapsed.TotalMilliseconds / 2000;
            Check("think tick on a 400-node graph well under 0.5 ms", avgMs < 0.5,
                  $"avg={avgMs * 1000:0} us, max={maxMs * 1000:0} us over {measured} ticks, Dijkstra runs={sim.D.Routes.Computations}");
            Check("no allocations per tick after warm-up", bytes == 0, $"{bytes} bytes over {measured} ticks");
        }
    }
}
