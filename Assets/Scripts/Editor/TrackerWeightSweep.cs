using System;
using System.Collections.Generic;
using System.Text;
using Echo.AI.Tracker;
using UnityEngine;

namespace Echo.EditorTools
{
    /// ECHO > Tracker > Run Weight Sweep. Closed-loop simulation of the full TrackerDecider against a scripted
    /// runner, to compare scoring variants and weight sets with data.
    ///
    /// World: the Sandbox_Tracker layout and a 10 x 10 lattice (6 m, random spanning tree + 40% extra links = loops).
    /// Runner: random start/goal (route >= 36 m), 2.5-4.5 m/s, one gunshot at t = 0 then a noisy step every 0.5 s.
    /// Hearing (guide §4.4-4.5): effective distance = graph route; R = intensity x 45 m; loudness (1 - d/R)^2 >= 0.03;
    /// error = lerp(6, 0.5, loudness); estimate = true position + uniform offset within the error.
    /// Tracker: starts 15-35 m (route) from the runner, follows its MoveTo goals along the graph (walk 2.2 / run 5).
    /// (ECHO > Tracker > Calibrate Mover: the real NavMeshAgent mover is within 0.1 s of route / speed at run, so this
    /// ideal walker is a fair stand-in for the body; the decider's stops and gait choice are simulated by the decider.)
    /// Catch = Tracker within 2 m of the runner. Intercept = a catch with the Tracker in front of the runner.
    /// Episode ends 3 s after the runner reaches its goal, or at 45 s. Same seeds for every config, so configs are
    /// compared PAIRED (McNemar on the episodes where exactly one of the two caught): a change is adopted only if
    /// z beats the Bonferroni-corrected two-sided 5% threshold for the number of alternatives tested
    /// (1 -> 1.96, 3 -> 2.39, 4 -> 2.50).
    public static class TrackerWeightSweep
    {
        public const int EpisodesPerWorld = 500;
        const float Dt = 0.1f, Think = 0.2f, MaxTime = 45f, CatchDistance = 2f, StepInterval = 0.5f, StepIntensity = 0.5f;

        public struct Config
        {
            public string Name;
            public bool RouteLead, Gate, EtaGait, RunToIntercept;
            public PlayerEtaSpeed PlayerSpeed;
            public float RunAdvMin;
            public AdvantageMode AdvMode;
            public float Prox, Dir, Adv, Heat;
            public Config(string name, bool routeLead, bool gate, float prox, float dir, float adv, float heat,
                          bool etaGait = true, bool runToIntercept = false, PlayerEtaSpeed playerSpeed = PlayerEtaSpeed.Shrunk)
            {
                Name = name; RouteLead = routeLead; Gate = gate; Prox = prox; Dir = dir; Adv = adv; Heat = heat;
                EtaGait = etaGait; RunToIntercept = runToIntercept; PlayerSpeed = playerSpeed; RunAdvMin = 0f; AdvMode = AdvantageMode.Reward;
            }
            public Config With(string name, PlayerEtaSpeed playerSpeed, float runAdvMin = 0f) =>
                new(name, RouteLead, Gate, Prox, Dir, Adv, Heat, EtaGait, RunToIntercept, playerSpeed) { RunAdvMin = runAdvMin };
        }

        public struct Result
        {
            public int Episodes, Catches, Intercepts, Picks, LatePicks, OffLinePicks;
            public double MissSum, CatchTimeSum;
            public bool[] Caught;
            public float CatchRate => (float)Catches / Episodes;
            public float InterceptRate => (float)Intercepts / Episodes;
            public float MeanMiss => (float)(MissSum / Episodes);
            public float MeanCatchTime => Catches > 0 ? (float)(CatchTimeSum / Catches) : float.NaN;
            public float LateShare => Picks > 0 ? (float)LatePicks / Picks : 0f;
            public float OffLineShare => Picks > 0 ? (float)OffLinePicks / Picks : 0f;
            /// Binomial standard error of the catch rate.
            public float CatchSE => Mathf.Sqrt(CatchRate * (1f - CatchRate) / Episodes);
        }

        /// ETA model on the adopted phase-3 settings (gated adv, straight lead, 1.2/1.0/1.0/0), reference E0:
        ///   E0 route / runSpeed (phase 3; the Tracker may then walk to a node it claimed at run speed)
        ///   E1 calibrated TrackerMotion ETA at the gait the confidence band issues (walk in the Walk band)
        ///   E2 calibrated run ETA, and the Tracker runs to every interception it claims (adv > 0)
        public static readonly Config[] EtaModels =
        {
            new("E0 ETA = route / runSpeed (phase 3)",   false, true, 1.2f, 1.0f, 1.0f, 0f, etaGait: false),
            new("E1 ETA at the band's gait",              false, true, 1.2f, 1.0f, 1.0f, 0f, etaGait: true),
            new("E2 run to claimed interceptions",        false, true, 1.2f, 1.0f, 1.0f, 0f, etaGait: true, runToIntercept: true),
        };

        /// ETA_player speed on the adopted ETA model (Scenario a x10 in Unity, 15 Oct: Tracker ETA within 0.2 s, runner
        /// ETA -4.05 s mean error = the shrunk |v| is slow, so adv was optimistic):
        ///   P0 |shrunk v| (phase 3)   P1 unshrunk WLS speed   P2 shrunk towards a moving player's speed (velocityPrior)
        ///   P3/P4 = P0/P2 but the Tracker runs to every scored goal (runAdvMin -1), so the gait no longer hangs on adv's sign
        /// (heading and prediction keep the shrunk v in all three)
        static Config[] PlayerEta(Config eta) => new[]
        {
            eta.With("P0 ETA_player from shrunk |v|", PlayerEtaSpeed.Shrunk),
            eta.With("P1 ETA_player from raw WLS speed", PlayerEtaSpeed.Raw),
            eta.With("P2 ETA_player shrunk to moving speed", PlayerEtaSpeed.Moving),
            eta.With("P3 shrunk |v|, run to every scored goal", PlayerEtaSpeed.Shrunk, -1f),
            eta.With("P4 moving speed, run to every scored goal", PlayerEtaSpeed.Moving, -1f),
        };

        /// Time advantage as reward vs constraint (scenario a, 15 Oct: node 30 one cell off the runner's line, next to
        /// where its path ends, picked with adv 0.31 - the runner never went there), on the adopted variant:
        ///   A0 reward (current)  A1 adv > 0 required, prox + dir choose  A2 adv' capped at 0.2
        ///   A3 adv > 0 and on the line (cos >= 0.9) required, prox + dir choose
        static Config[] AdvModes(Config v)
        {
            var modes = new[] { (AdvantageMode.Reward, "A0 adv as reward (current)"), (AdvantageMode.Constraint, "A1 adv > 0 constraint"),
                                (AdvantageMode.Saturated, "A2 adv capped at 0.2"), (AdvantageMode.ConstraintOnLine, "A3 adv > 0 + on-line constraint") };
            var w = new TrackerSettings();       // on the shipped weights (heat 0), not the variant stage's phase-1 weights
            var list = new Config[modes.Length];
            for (int i = 0; i < modes.Length; i++)
            {
                list[i] = v.With(modes[i].Item2, v.PlayerSpeed, v.RunAdvMin);
                list[i].Prox = w.wProx; list[i].Dir = w.wDir; list[i].Adv = w.wAdv; list[i].Heat = w.wHeat;
                list[i].AdvMode = modes[i].Item1;
            }
            return list;
        }

        public static readonly Config[] Variants =
        {
            new("V0 straight lead, plain adv (phase 1)", false, false, 1.2f, 1.0f, 1.0f, 0.4f),
            new("V1 route lead",                         true,  false, 1.2f, 1.0f, 1.0f, 0.4f),
            new("V2 direction-gated adv",                false, true,  1.2f, 1.0f, 1.0f, 0.4f),
            new("V3 route lead + gated adv",             true,  true,  1.2f, 1.0f, 1.0f, 0.4f),
        };

        /// Reference = the current settings (heat 0 since phase 3), so a weight only changes on evidence.
        public static Config[] WeightSets(bool routeLead, bool gate, Config eta) => new[]
        {
            new Config("W4 no heat 1.2/1.0/1.0/0 (current)", routeLead, gate, 1.2f, 1.0f, 1.0f, 0f, eta.EtaGait, eta.RunToIntercept, eta.PlayerSpeed) { RunAdvMin = eta.RunAdvMin, AdvMode = eta.AdvMode },
            new Config("W0 1.2/1.0/1.0/0.4 (phase-1)",  routeLead, gate, 1.2f, 1.0f, 1.0f, 0.4f, eta.EtaGait, eta.RunToIntercept, eta.PlayerSpeed) { RunAdvMin = eta.RunAdvMin, AdvMode = eta.AdvMode },
            new Config("W1 prox-heavy 1.6/1.0/0.6/0.4",  routeLead, gate, 1.6f, 1.0f, 0.6f, 0.4f, eta.EtaGait, eta.RunToIntercept, eta.PlayerSpeed) { RunAdvMin = eta.RunAdvMin, AdvMode = eta.AdvMode },
            new Config("W2 adv-heavy 1.0/0.8/1.6/0.4",   routeLead, gate, 1.0f, 0.8f, 1.6f, 0.4f, eta.EtaGait, eta.RunToIntercept, eta.PlayerSpeed) { RunAdvMin = eta.RunAdvMin, AdvMode = eta.AdvMode },
            new Config("W3 dir-heavy 1.0/1.6/1.0/0.4",   routeLead, gate, 1.0f, 1.6f, 1.0f, 0.4f, eta.EtaGait, eta.RunToIntercept, eta.PlayerSpeed) { RunAdvMin = eta.RunAdvMin, AdvMode = eta.AdvMode },
        };

        /// The variants (V0-V3) with the adopted ETA model.
        static Config[] VariantsWith(Config eta)
        {
            var list = new Config[Variants.Length];
            for (int i = 0; i < list.Length; i++)
            {
                var v = Variants[i];
                list[i] = new Config(v.Name, v.RouteLead, v.Gate, v.Prox, v.Dir, v.Adv, v.Heat, eta.EtaGait, eta.RunToIntercept, eta.PlayerSpeed) { RunAdvMin = eta.RunAdvMin, AdvMode = eta.AdvMode };
            }
            return list;
        }

#if UNITY_EDITOR
        [UnityEditor.MenuItem("ECHO/Tracker/Run Weight Sweep")]
        static void RunFromMenu()
        {
            var sb = new StringBuilder();
            RunAll(line => sb.AppendLine(line));
            UnityEngine.Debug.Log("[TrackerWeightSweep]\n" + sb);
        }
#endif

        /// Runs the ETA model comparison (reference E0), the player-ETA speed (reference P0 = shrunk), then the variant comparison (reference V0) and the weight
        /// sets on the adopted variant (reference W4 = current), both with the adopted ETA model.
        /// Returns the adopted config: the best one that beats its reference with McNemar z >= the Bonferroni threshold.
        public static Config RunAll(Action<string> log)
        {
            var worlds = new[] { ("sandbox", SandboxTrackerLayout.BuildGraph(out _)), ("lattice", Lattice(10, 10, 6f, 12345)) };
            log($"{EpisodesPerWorld} episodes per world x {worlds.Length} worlds per config. Same seeds for every config.");
            var e = Pick(EtaModels, worlds, log);
            log($"Adopted ETA model: {e.Name}");
            var pe = PlayerEta(e);
            log($"Best player ETA speed by catch rate: {Pick(pe, worlds, log).Name}");
            // Shipped: the honest ETA (P4, decision 15 Oct - P3 keeps a biased ETA_player); later stages run on it.
            var shipped = new TrackerSettings();
            e = Array.Find(pe, c => c.PlayerSpeed == shipped.playerEtaSpeed && c.RunAdvMin == shipped.runAdvMin);
            log($"Shipped player ETA speed (later stages): {e.Name}");
            var v = Pick(VariantsWith(e), worlds, log);
            log($"Adopted variant: {v.Name}");
            var a = Pick(AdvModes(v), worlds, log);
            log($"Adopted advantage mode: {a.Name}");
            var w = Pick(WeightSets(v.RouteLead, v.Gate, a), worlds, log);
            log($"Adopted weight set: {w.Name}");
            return w;
        }

        static Config Pick(Config[] configs, (string, TrackerGraph)[] worlds, Action<string> log)
        {
            log(Header());
            var reference = Run(configs[0], worlds);
            log(Row(configs[0].Name, reference) + "  (reference)");
            Config adopted = configs[0]; float adoptedRate = reference.CatchRate;
            float zCrit = ZCrit(configs.Length - 1);
            log($"significance: z >= {zCrit:0.00} (Bonferroni, {configs.Length - 1} comparisons)");
            for (int i = 1; i < configs.Length; i++)
            {
                var r = Run(configs[i], worlds);
                float z = McNemar(reference, r, out int gained, out int lost);
                log(Row(configs[i].Name, r) + $"  vs ref: +{gained}/-{lost} z={z:0.00}{(z >= zCrit ? " *" : "")}");
                if (z >= zCrit && r.CatchRate > adoptedRate) { adopted = configs[i]; adoptedRate = r.CatchRate; }
            }
            return adopted;
        }

        /// Two-sided 5% critical z with a Bonferroni correction for k comparisons.
        static float ZCrit(int k) => k switch { <= 1 => 1.96f, 2 => 2.24f, 3 => 2.39f, 4 => 2.50f, 5 => 2.58f, _ => 2.64f };

        /// Paired test on the same episodes: gained = caught only with `b`, lost = caught only with `a`.
        static float McNemar(in Result a, in Result b, out int gained, out int lost)
        {
            gained = lost = 0;
            for (int i = 0; i < a.Caught.Length; i++)
            {
                if (b.Caught[i] && !a.Caught[i]) gained++;
                else if (a.Caught[i] && !b.Caught[i]) lost++;
            }
            return gained + lost == 0 ? 0f : (gained - lost) / Mathf.Sqrt(gained + lost);
        }

        static string Header() =>
            $"{"config",-40} {"catch",7} {"+-SE",6} {"intercept",9} {"miss m",7} {"t catch",7} {"late",6} {"off-line",8}";

        static string Row(string name, Result r) =>
            $"{name,-40} {r.CatchRate,7:P1} {r.CatchSE,6:P1} {r.InterceptRate,9:P1} {r.MeanMiss,7:0.00} {r.MeanCatchTime,7:0.0} {r.LateShare,6:P0} {r.OffLineShare,8:P0}";

        public static Result Run(Config c, (string, TrackerGraph)[] worlds)
        {
            var total = new Result { Caught = new bool[EpisodesPerWorld * worlds.Length] };
            foreach (var (_, g) in worlds)
            {
                for (int e = 0; e < EpisodesPerWorld; e++) Episode(c, g, 1000 + e, ref total);
            }
            return total;
        }

        // ---------------------------------------------------------------- episode

        static void Episode(Config c, TrackerGraph g, int seed, ref Result res)
        {
            var rng = new System.Random(seed);
            var s = new TrackerSettings
            {
                routeLead = c.RouteLead, gateAdvantage = c.Gate,
                wProx = c.Prox, wDir = c.Dir, wAdv = c.Adv, wHeat = c.Heat, etaFromGait = c.EtaGait, runToIntercept = c.RunToIntercept,
                playerEtaSpeed = c.PlayerSpeed, runAdvMin = c.RunAdvMin, advMode = c.AdvMode
            };
            var routes = new RouteService(g, s);

            // Runner route (>= 36 m) and Tracker start (15-35 m by route from the runner).
            int start, goal, trackerStart;
            var dist = new List<int>();
            while (true)
            {
                start = rng.Next(g.Count); goal = rng.Next(g.Count);
                var f = routes.Field(start).Dist;
                if (float.IsPositiveInfinity(f[goal]) || f[goal] < 36f) continue;
                dist.Clear();
                for (int i = 0; i < g.Count; i++) if (f[i] >= 15f && f[i] <= 35f) dist.Add(i);
                if (dist.Count == 0) continue;
                trackerStart = dist[rng.Next(dist.Count)];
                break;
            }
            var runner = new PathWalker(g, routes);
            runner.Plan(g.Position(start), g.Position(goal));
            float runnerSpeed = 2.5f + 2f * (float)rng.NextDouble();

            var tracker = new PathWalker(g, routes);
            Vector3 tPos = g.Position(trackerStart);
            tracker.Position = tPos;
            var d = new TrackerDecider(g, s, tPos);
            var percepts = new List<TrackerPercept>();
            float trackerSpeed = 0f, nextThink = 0f, nextStep = 0f, endAt = float.PositiveInfinity, minDist = float.PositiveInfinity;
            bool arrived = false, failed = false;
            bool first = true;

            for (float t = 0f; t < MaxTime && t < endAt; t += Dt)
            {
                // Runner moves and makes noise.
                Vector3 before = runner.Position;
                bool moving = !runner.Done;
                runner.Advance(runnerSpeed * Dt);
                Vector3 runnerVel = (runner.Position - before) / Dt;
                if (runner.Done && float.IsPositiveInfinity(endAt)) endAt = t + 3f;
                if (moving && t >= nextStep)
                {
                    Hear(first ? 1f : StepIntensity, runner.Position, tracker.Position, t, g, routes, rng, percepts);
                    first = false;
                    nextStep = t + StepInterval;
                }

                // Tracker moves.
                if (trackerSpeed > 0f && !tracker.Done)
                {
                    tracker.Advance(trackerSpeed * Dt);
                    if (tracker.Done) arrived = true;
                }

                float sep = Vector3.Distance(tracker.Position, runner.Position);
                minDist = Mathf.Min(minDist, sep);
                if (sep <= CatchDistance)
                {
                    res.Caught[res.Episodes] = true;
                    res.Catches++; res.CatchTimeSum += t;
                    Vector3 rel = tracker.Position - runner.Position; rel.y = 0f;
                    if (runnerVel.sqrMagnitude > 0.01f && Vector3.Dot(rel, runnerVel) > 0f) res.Intercepts++;
                    break;
                }

                if (t >= nextThink)
                {
                    nextThink = t + Think;
                    var input = new TrackerTickInput
                    {
                        Now = t, Self = tracker.Position, SelfSpeed = tracker.Done ? 0f : trackerSpeed, Arrived = arrived, Failed = failed
                    };
                    arrived = failed = false;
                    var cmd = d.Tick(input, percepts);
                    percepts.Clear();
                    switch (cmd.Kind)
                    {
                        case TrackerCommandKind.MoveTo:
                            if (!tracker.Plan(tracker.Position, cmd.Target)) { failed = true; trackerSpeed = 0f; break; }
                            trackerSpeed = cmd.Gait == TrackerGait.Run ? s.runSpeed : s.walkSpeed;
                            if (d.State == TrackerState.Intercept && d.HasVelocity && d.GoalNode >= 0) CountPick(d, ref res);
                            break;
                        case TrackerCommandKind.Stop:
                        case TrackerCommandKind.Attack:
                            trackerSpeed = 0f; tracker.Clear();
                            break;
                    }
                }
            }
            res.Episodes++;
            res.MissSum += Mathf.Max(0f, minDist <= CatchDistance ? 0f : minDist);
        }

        /// Records the two phase-1 findings for every interception goal: picked a node the player reaches first
        /// (adv < 0 = "late"), and picked a node off the player's line (dir < 0.9 = "off-line").
        static void CountPick(TrackerDecider d, ref Result res)
        {
            res.Picks++;
            int n = d.GoalNode;
            if (d.Scorer.Advantage[n] < 0f) res.LatePicks++;
            Vector3 off = d.World.Position(n) - d.Memory.Last.Position; off.y = 0f;
            Vector3 v = d.Velocity; v.y = 0f;
            if (off.magnitude > 0.5f && v.magnitude > 0.1f && Vector3.Dot(v.normalized, off.normalized) < 0.9f) res.OffLinePicks++;
        }

        static void Hear(float intensity, Vector3 source, Vector3 ear, float t, TrackerGraph g, RouteService routes,
                         System.Random rng, List<TrackerPercept> into)
        {
            float d = routes.Route(source, g.Nearest(source), ear, g.Nearest(ear));
            float r = intensity * 45f;
            if (d >= r) return;
            float x = 1f - d / r, loud = x * x;
            if (loud < 0.03f) return;
            float err = Mathf.Lerp(6f, 0.5f, loud);
            float ang = (float)(rng.NextDouble() * Math.PI * 2), rad = err * Mathf.Sqrt((float)rng.NextDouble());
            var p = source + new Vector3(Mathf.Cos(ang) * rad, 0f, Mathf.Sin(ang) * rad);
            into.Add(new TrackerPercept(p, err, t, intensity >= 1f ? HeardKind.Gunshot : HeardKind.Run));
        }

        // ---------------------------------------------------------------- helpers

        /// Moves along the shortest graph route (read off a Dijkstra field from the goal) to an exact point.
        sealed class PathWalker
        {
            readonly TrackerGraph g; readonly RouteService routes;
            readonly List<Vector3> points = new();
            int next;
            public Vector3 Position;
            public bool Done => next >= points.Count;
            public PathWalker(TrackerGraph g, RouteService routes) { this.g = g; this.routes = routes; }
            public void Clear() { points.Clear(); next = 0; }

            public bool Plan(Vector3 from, Vector3 to)
            {
                Clear(); Position = from;
                int s = g.Nearest(from), t = g.Nearest(to);
                var dist = routes.Field(t).Dist;
                if (float.IsPositiveInfinity(dist[s])) return false;
                int cur = s;
                points.Add(g.Position(cur));
                while (cur != t)
                {
                    int best = -1; float bc = float.PositiveInfinity;
                    for (int k = 0; k < g.NeighbourCount(cur); k++)
                    {
                        var e = g.Neighbour(cur, k);
                        float c = TrackerCosts.EdgeCost(e, 6.5f) + dist[e.To];
                        if (c < bc) { bc = c; best = e.To; }
                    }
                    if (best < 0 || points.Count > g.Count) return false;
                    cur = best; points.Add(g.Position(cur));
                }
                points.Add(to);
                // Skip the first node if it lies behind.
                if (points.Count > 2 && Vector3.Distance(from, points[1]) < Vector3.Distance(points[0], points[1])) points.RemoveAt(0);
                return true;
            }

            public void Advance(float step)
            {
                while (step > 0f && next < points.Count)
                {
                    Vector3 to = points[next];
                    float d = Vector3.Distance(Position, to);
                    if (d <= step) { Position = to; step -= d; next++; }
                    else { Position += (to - Position) / d * step; step = 0f; }
                }
            }
        }

        /// nx x nz lattice; random spanning tree (randomised DFS) + `extra` share of the remaining links.
        public static TrackerGraph Lattice(int nx, int nz, float step, int seed, float extra = 0.4f)
        {
            var rng = new System.Random(seed);
            var g = new TrackerGraph();
            for (int z = 0; z < nz; z++) for (int x = 0; x < nx; x++) g.AddNode(new Vector3(x * step, 0f, z * step));
            var linked = new HashSet<long>();
            var seen = new bool[nx * nz];
            var stack = new Stack<int>();
            stack.Push(0); seen[0] = true;
            int[] dx = { 1, -1, 0, 0 }, dz = { 0, 0, 1, -1 };
            var options = new List<int>(4);
            while (stack.Count > 0)
            {
                int cur = stack.Peek(), cx = cur % nx, cz = cur / nx;
                options.Clear();
                for (int k = 0; k < 4; k++)
                {
                    int x = cx + dx[k], z = cz + dz[k];
                    if (x >= 0 && z >= 0 && x < nx && z < nz && !seen[z * nx + x]) options.Add(z * nx + x);
                }
                if (options.Count == 0) { stack.Pop(); continue; }
                int nxt = options[rng.Next(options.Count)];
                seen[nxt] = true; g.Connect(cur, nxt); linked.Add(Key(cur, nxt)); stack.Push(nxt);
            }
            for (int z = 0; z < nz; z++)
            for (int x = 0; x < nx; x++)
            {
                int i = z * nx + x;
                if (x + 1 < nx && !linked.Contains(Key(i, i + 1)) && rng.NextDouble() < extra) g.Connect(i, i + 1);
                if (z + 1 < nz && !linked.Contains(Key(i, i + nx)) && rng.NextDouble() < extra) g.Connect(i, i + nx);
            }
            return g;
        }

        static long Key(int a, int b) => a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
    }
}
