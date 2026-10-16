using System.Collections;
using System.Collections.Generic;
using System.Text;
using Echo.AI.Tracker;
using Echo.Interaction;
using Echo.Navigation;
using Echo.Player;
using Echo.Sound;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Echo.DevTools
{
    /// Play Mode scenarios for the integrated Tracker (TrackerBrain on AgentBrain/AgentMotor/Hearing) in dev's
    /// Sandbox_Tracker (two 15 x 20 m rooms, one doorway at x 0, z 3.2..6.8; real NavGraph). ECHO > Tracker >
    /// Run NavGraph Scenarios. A scripted ghost makes noises through InteractionNoise -> Noise.Make -> SoundBus, so the
    /// Tracker perceives it only through its own Hearing (estimate + error). The player is frozen in the east room's
    /// NE corner and invulnerable until (e).
    ///   a interception - from a post in the west room, the Tracker reaches a claimed interception node (adv > 0) on a
    ///                    runner's line along the west wall before the runner does
    ///   b fake sound   - an audible gunshot far (by route) from the runner mid-trail: heard, rejected as a suspect,
    ///                    the trail and the goal stay with the runner
    ///   c player quiet - best-first search (>= 1 node visited), then Idle with memory cleared
    ///   d locked door  - one door spawned in the only doorway and locked, after a graph rebuild: it must gate every
    ///                    edge that crosses the doorway; gunshots in the east room: no goal behind the locked door and no
    ///                    crossing; after the unlock it opens the door itself and reaches the room.
    ///                    Also checks Door -> NavGraph.OnDoorChanged (Version bumps on lock and unlock)
    ///   e player death - Reset on Died (Idle, memory empty); after the reload a fresh Tracker is Idle
    public class TrackerNavGraphScenarios : MonoBehaviour
    {
        const float GhostSpeed = 3f, StepInterval = 0.5f, StepIntensity = 0.6f;
        static readonly Vector3 Post = new(-3f, 0f, -1f);                              // west room, east side
        static readonly Vector3 RunFrom = new(-12.5f, 0f, -9f), RunTo = new(-12.5f, 0f, 9f);   // along the west wall
        static readonly Vector3 PlayerPark = new(13f, 0f, 8.5f);
        static readonly Vector3 DoorCentre = new(0f, 0f, 5f);

        readonly StringBuilder report = new();
        int passed, total;
        TrackerBrain agent;
        PlayerTestRig rig;
        Transform ghost;
        Vector3 home;

        public static void Launch()
        {
            if (TrackerScenarios.RefuseIfBusy("NavGraph scenarios a-e")) return;
            var go = new GameObject("_TrackerNavGraphScenarios");
            DontDestroyOnLoad(go);                                                     // survives the reload of (e)
            var run = go.AddComponent<TrackerNavGraphScenarios>();
            TrackerScenarios.Claim(run, "NavGraph scenarios a-e");
            run.StartCoroutine(run.RunAll());
        }

        IEnumerator RunAll()
        {
            yield return Setup();
            if (agent == null) { Destroy(gameObject); yield break; }

            yield return A_Interception();  yield return Between();
            yield return B_FakeSound();     yield return Between();
            yield return C_GoesQuiet();     yield return Between();
            yield return D_LockedDoor();    yield return Between();
            yield return E_PlayerDeath();

            string msg = $"[TrackerNavGraphScenarios] {passed}/{total} passed\n{report}";
            if (passed == total) Debug.Log(msg); else Debug.LogError(msg);
            Destroy(gameObject);
        }

        IEnumerator Setup()
        {
            agent = FindFirstObjectByType<TrackerBrain>();
            var interactor = FindFirstObjectByType<PlayerInteractor>();
            if (agent == null || interactor == null || PlayerDeath.Current == null || NavGraph.Instance == null)
            {
                Debug.LogError("[TrackerNavGraphScenarios] Needs Sandbox_Tracker in Play Mode (Tracker with TrackerBrain, NavGraph, player).");
                yield break;
            }
            home = agent.transform.position;
            rig = new PlayerTestRig(interactor);
            rig.Freeze();
            rig.Player.position = new Vector3(PlayerPark.x, rig.Player.position.y, PlayerPark.z);
            Physics.SyncTransforms();
#if UNITY_EDITOR
            PlayerDeath.Current.SetInvulnerable(true);
#endif
            var g = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            g.name = "Ghost";
            Destroy(g.GetComponent<Collider>());
            g.transform.localScale = Vector3.one * 0.5f;
            ghost = g.transform;
            ghost.gameObject.SetActive(false);
            while (InteractionNoise.IsMuted || agent.Decider == null) yield return null;   // settle mute, graph built
            agent.ResetTo(home, true);
            yield return new WaitForSeconds(0.5f);
        }

        IEnumerator Between()
        {
            ghost.gameObject.SetActive(false);
            agent.ResetTo(home, true);
            yield return new WaitForSeconds(1f);
        }

        void Check(string name, bool ok, string observed)
        {
            total++; if (ok) passed++;
            report.AppendLine($"{(ok ? "PASS" : "FAIL")}  {name}  ({observed})");
        }

        static Vector3 Feet(Transform t) { var p = t.position; p.y = 0f; return p; }

        /// Moves the ghost from a to b at GhostSpeed (a gunshot first if `shot`), a run step every StepInterval.
        /// `perFrame` runs every frame (return true to stop early).
        IEnumerator Run(Vector3 a, Vector3 b, bool shot, System.Func<bool> perFrame = null)
        {
            ghost.position = a + Vector3.up * 0.5f;
            ghost.gameObject.SetActive(true);
            if (shot) InteractionNoise.Emit(NoiseType.Gunshot, a, 1f, NoiseSource.Player, ghost.gameObject);
            float nextStep = Time.time + StepInterval;
            Vector3 p = a;
            while ((p - b).sqrMagnitude > 0.01f)
            {
                p = Vector3.MoveTowards(p, b, GhostSpeed * Time.deltaTime);
                ghost.position = p + Vector3.up * 0.5f;
                if (Time.time >= nextStep)
                {
                    InteractionNoise.Emit(NoiseType.Run, p, StepIntensity, NoiseSource.Player, ghost.gameObject);
                    nextStep = Time.time + StepInterval;
                }
                if (perFrame != null && perFrame()) yield break;
                yield return null;
            }
        }

        /// Would this Tracker's ear pick up a sound of `intensity` at `p` right now? (Hearing's own model.)
        bool WouldHear(Vector3 p, float intensity)
        {
            var ear = agent.GetComponent<Hearing>();
            float d = Acoustics.EffectiveDistance(p, agent.transform.position + Vector3.up * ear.earHeight);
            return Acoustics.Loudness(intensity, ear.EffectiveSensitivity, d) >= ear.threshold;
        }

        // ------------------------------------------------------------------ a
        IEnumerator A_Interception()
        {
            var d = agent.Decider;
            agent.ResetTo(Post, true);
            var ghostAt = new float[d.World.Count];
            for (int i = 0; i < ghostAt.Length; i++) ghostAt[i] = -1f;
            var arrivals = new List<(int node, float t, float adv)>();
            TrackerState prev = d.State;
            int goal = -1; float goalAdv = 0f; bool claimed = false;
            float closest = float.PositiveInfinity;
            var log = new StringBuilder();
            int issues = d.IssueCount;
            var nav = agent.GetComponent<UnityEngine.AI.NavMeshAgent>();
            System.Func<bool> watch = () =>
            {
                if (d.IssueCount != issues)                                    // every MoveTo the decider issued, and why
                {
                    issues = d.IssueCount;
                    int b = d.GoalNode; var sc = d.Scorer; Vector3 me = Feet(agent.transform);
                    bool ok = b >= 0 && b < sc.Scores.Length;
                    log.Append($"\n    t={Time.time:0.0} {d.LastReplan} goal={b} claimed={d.InterceptGoal} S={(ok ? sc.Scores[b] : 0f):0.00} " +
                               $"adv={(ok ? sc.Advantage[b] : 0f):0.00} etaT={(ok ? sc.EtaTracker[b] : 0f):0.0} etaP={(ok ? sc.EtaPlayer[b] : 0f):0.0} " +
                               $"band={d.Band} C={d.Confidence.Value:0.00} cand={sc.CandidateCount} me=({me.x:0.0},{me.z:0.0}) spd={nav.velocity.magnitude:0.0} " +
                               $"pred=({d.Predicted.x:0.0},{d.Predicted.z:0.0}) ghost=({ghost.position.x:0.0},{ghost.position.z:0.0})");
                }
                if (d.State == TrackerState.Intercept && d.GoalNode >= 0 && d.GoalNode != goal)
                {
                    goal = d.GoalNode; claimed = d.InterceptGoal;
                    goalAdv = goal < d.Scorer.Advantage.Length ? d.Scorer.Advantage[goal] : 0f;
                }
                if (d.State == TrackerState.Hold && prev == TrackerState.Intercept && goal >= 0)
                {
                    if (claimed) arrivals.Add((goal, Time.time, goalAdv));
                    log.Append($"\n    t={Time.time:0.0} arrived at {goal} (claimed {claimed})");
                    goal = -1;
                }
                if (d.State != prev) log.Append($"\n    t={Time.time:0.0} {prev} -> {d.State}");
                prev = d.State;
                Vector3 gp = Feet(ghost);
                closest = Mathf.Min(closest, Vector3.Distance(gp, Feet(agent.transform)));
                for (int i = 0; i < ghostAt.Length; i++)                       // runner's first contact with each node
                    if (ghostAt[i] < 0f && Vector3.Distance(gp, d.World.Position(i)) <= 1.5f) ghostAt[i] = Time.time;
                return false;
            };
            yield return Run(RunFrom, RunTo, true, watch);
            float until = Time.time + 2f;
            while (Time.time < until) { watch(); yield return null; }

            var sb = new StringBuilder();
            bool pass = false;
            foreach (var x in arrivals)
            {
                float g = ghostAt[x.node];
                bool first = g > x.t;
                pass |= first && x.adv > 0f;
                sb.Append($"node {x.node} {d.World.Position(x.node)}: Tracker t={x.t:0.0} runner t={(g < 0 ? "never" : g.ToString("0.0"))} adv={x.adv:0.00}; ");
            }
            Check("a interception: Tracker reaches a claimed interception node before the runner",
                  pass, (arrivals.Count == 0 ? "no claimed interception arrival; " : sb.ToString()) +
                        $"closest {closest:0.0} m, heard {d.Accepted}, state {d.State}" + (pass ? "" : $"\n  log:{log}"));
        }

        // ------------------------------------------------------------------ b
        IEnumerator B_FakeSound()
        {
            var d = agent.Decider;
            agent.ResetTo(Post, true);
            Vector3 fake = default;
            int suspectsBefore = 0, accepted0 = d.Accepted;
            bool fired = false, audible = false;
            float firedAt = 0f, fakeRoute = 0f, worstGoalToFake = float.PositiveInfinity, trailToGhost = 0f;
            System.Func<bool> watch = () =>
            {
                if (!fired && d.Accepted - accepted0 >= 3)               // a real trail exists
                {
                    var w = d.World; Vector3 runner = Feet(ghost), me = Feet(agent.transform);
                    int rn = w.Nearest(runner); float best = -1f;
                    for (int n = 0; n < w.Count; n++)
                    {
                        Vector3 p = w.Position(n);
                        if (Vector3.Distance(p, me) < 12f || (d.HasGoal && Vector3.Distance(p, d.Goal) < 12f) || !WouldHear(p, 1f)) continue;
                        float r = d.Routes.Route(runner, rn, p, n);
                        if (r > best) { best = r; fake = p; }
                    }
                    if (best < 0f) return false;
                    suspectsBefore = d.Suspects; fakeRoute = best; audible = true;
                    var f = new GameObject("FakeSource");
                    InteractionNoise.Emit(NoiseType.Gunshot, fake, 1f, NoiseSource.Player, f);   // what a Mimic would do
                    Destroy(f);
                    fired = true; firedAt = Time.time;
                }
                if (fired && Time.time > firedAt + 0.3f && Time.time < firedAt + 2.5f)
                {
                    if (d.HasGoal) worstGoalToFake = Mathf.Min(worstGoalToFake, Vector3.Distance(d.Goal, fake));
                    if (d.Memory.HasLast) trailToGhost = Mathf.Max(trailToGhost, Vector3.Distance(d.Memory.Last.Position, Feet(ghost)));
                }
                return fired && Time.time > firedAt + 2.5f;
            };
            yield return Run(RunFrom, RunTo, true, watch);
            Check("b fake sound: heard, rejected as a suspect, trail and goal stay with the runner",
                  fired && audible && d.Suspects > suspectsBefore && trailToGhost < 8f && worstGoalToFake > 10f,
                  $"fired={fired} at {fake} ({fakeRoute:0} m by route from the runner) audible={audible} suspects +{d.Suspects - suspectsBefore} " +
                  $"resets={d.Resets} last-obs-to-runner max {trailToGhost:0.0} m, " +
                  $"goal-to-fake min {(float.IsPositiveInfinity(worstGoalToFake) ? "-" : worstGoalToFake.ToString("0.0"))} m");
        }

        // ------------------------------------------------------------------ c
        IEnumerator C_GoesQuiet()
        {
            var d = agent.Decider;
            agent.ResetTo(Post, true);
            yield return Run(RunFrom, new Vector3(RunFrom.x, 0f, 0f), true);
            ghost.gameObject.SetActive(false);
            float quietAt = Time.time, searchAt = -1f, idleAt = -1f;
            int maxVisits = 0;
            while (Time.time < quietAt + 60f)
            {
                if (d.State == TrackerState.Search) { if (searchAt < 0f) searchAt = Time.time; maxVisits = Mathf.Max(maxVisits, d.Search.Visits); }
                if (searchAt > 0f && d.State == TrackerState.Idle) { idleAt = Time.time; break; }
                yield return null;
            }
            Check("c player goes quiet: best-first search, then Idle with memory cleared",
                  searchAt > 0f && maxVisits >= 1 && idleAt > 0f && d.Memory.Count == 0,
                  $"search after {(searchAt < 0 ? "-" : (searchAt - quietAt).ToString("0.0"))} s, visits {maxVisits}, " +
                  $"idle after {(idleAt < 0 ? "-" : (idleAt - quietAt).ToString("0.0"))} s, memory {d.Memory.Count}");
        }

        // ------------------------------------------------------------------ d
        IEnumerator D_LockedDoor()
        {
            var d = agent.Decider;
            var graph = NavGraph.Instance;
            // dev's Auto-Link joins every node pair with a near-straight NavMesh walk, so many edges cross the doorway,
            // not only the doorway-node pair. ONE door, sides = the doorway nodes: it must attach itself to all of them.
            var crossing = new StringBuilder();
            int crossingEdges = 0;                                            // both directions
            for (int i = 0; i < graph.Count; i++)
                foreach (var e in graph.Neighbours(i))
                    if (graph.Position(i).x * graph.Position(e.To).x < 0f) { crossingEdges++; if (e.To > i) crossing.Append($"{i}-{e.To} "); }
            int na = graph.Nearest(DoorCentre + new Vector3(-1.5f, 0f, 0f)), nb = graph.Nearest(DoorCentre + new Vector3(1.5f, 0f, 0f));
            var door = SpawnDoor(graph.Node(na), graph.Node(nb));
            int v0 = graph.Version;
            door.Lock();
            int vLocked = graph.Version;
            graph.Build();                                                    // a rebuild: the door must re-attach itself
            yield return null;                                                // (Door.Update, before agents plan)
            bool onEdge = true;
            for (int i = 0; i < d.World.Count; i++)
                for (int k = 0; k < d.World.NeighbourCount(i); k++)
                {
                    var e = d.World.Neighbour(i, k);
                    if (d.World.Position(i).x * d.World.Position(e.To).x < 0f && e.Door != DoorState.Locked) onEdge = false;
                }

            Vector3 target = new(4f, 0f, 2f);                                 // east room, behind the divider
            agent.ResetTo(new Vector3(-3f, 0f, 2f), true);
            float start = Time.time, nextShot = start, maxX = float.NegativeInfinity;
            int goalsBehind = 0, heard0 = d.Accepted + d.Suspects + d.Resets;
            while (Time.time < start + 20f)
            {
                if (Time.time >= nextShot) { InteractionNoise.Emit(NoiseType.Gunshot, target, 1f, NoiseSource.Player, ghost.gameObject); nextShot = Time.time + 2f; }
                maxX = Mathf.Max(maxX, agent.transform.position.x);
                if (d.HasGoal && d.Goal.x > 0.5f) goalsBehind++;
                yield return null;
            }
            int heardLocked = d.Accepted + d.Suspects + d.Resets - heard0;
            int vBeforeUnlock = graph.Version;
            door.Unlock();
            int vUnlocked = graph.Version;
            float unlockAt = Time.time, minDist = float.PositiveInfinity; bool opened = false;
            while (Time.time < unlockAt + 25f)
            {
                if (Time.time >= nextShot) { InteractionNoise.Emit(NoiseType.Gunshot, target, 1f, NoiseSource.Player, ghost.gameObject); nextShot = Time.time + 2f; }
                opened |= door.isOpen;
                Vector3 p = Feet(agent.transform);
                if (p.x > 1.5f) minDist = Mathf.Min(minDist, Vector3.Distance(p, target));
                if (minDist <= 6f) break;
                yield return null;
            }
            Check("d one door gates every crossing edge after a rebuild; Version bumps on lock and unlock",
                  vLocked > v0 && vUnlocked > vBeforeUnlock && onEdge && door.AttachedEdges == crossingEdges,
                  $"version {v0} -> lock {vLocked}, {vBeforeUnlock} -> unlock {vUnlocked}; door on {door.AttachedEdges}/{crossingEdges} " +
                  $"directed crossing edges ({crossing}), all locked={onEdge}");
            Check("d locked door: heard, no goal behind it, never crossed; after unlock reaches the room",
                  heardLocked > 0 && goalsBehind == 0 && maxX < 0.3f && minDist <= 6f,
                  $"heard while locked {heardLocked}, goal frames behind door {goalsBehind}, max x {maxX:0.0}, " +
                  $"closest in east room {(float.IsPositiveInfinity(minDist) ? "-" : minDist.ToString("0.0"))} m " +
                  $"{Time.time - unlockAt:0.0} s after unlock, state {d.State}");
            Check("d after unlock the Tracker opens the door itself (AgentMotor), never walks through it closed",
                  opened, $"door opened {opened}");

            Destroy(door.gameObject);
            yield return null;
            graph.Build();                                                    // back to dev's door-less graph
        }

        /// A minimal sliding door in the sandbox doorway (the scene itself stays untouched): leaf on Environment so it
        /// occludes sound, rises 3 m when open.
        static Door SpawnDoor(NavNode a, NavNode b)
        {
            var root = new GameObject("TestDoor");
            root.SetActive(false);
            root.transform.position = DoorCentre;
            var leaf = GameObject.CreatePrimitive(PrimitiveType.Cube);
            leaf.name = "Leaf";
            leaf.transform.SetParent(root.transform, false);
            leaf.transform.localPosition = new Vector3(0f, 1.5f, 0f);
            leaf.transform.localScale = new Vector3(0.3f, 3f, 3.6f);
            int env = LayerMask.NameToLayer("Environment");
            if (env >= 0) leaf.layer = env;
            leaf.AddComponent<Rigidbody>();
            var door = root.AddComponent<Door>();
            door.sideA = a; door.sideB = b;
            door.slideDirection = Vector3.up;
            root.SetActive(true);                                             // Awake with the fields set
            return door;
        }

        // ------------------------------------------------------------------ e
        IEnumerator E_PlayerDeath()
        {
            var d = agent.Decider;
            agent.ResetTo(Post, true);
            yield return Run(RunFrom, new Vector3(RunFrom.x, 0f, 0f), true);
            int memBefore = d.Memory.Count;
            var stateBefore = d.State;
#if UNITY_EDITOR
            PlayerDeath.Current.SetInvulnerable(false);
#endif
            bool killed = PlayerDeath.Current.Kill(agent.gameObject);
            Check("e player death: Tracker resets at once (Idle, memory empty)",
                  killed && d.State == TrackerState.Idle && d.Memory.Count == 0,
                  $"killed={killed} before: {stateBefore}/{memBefore} obs, after: {d.State}/{d.Memory.Count} obs");

            int scene = SceneManager.GetActiveScene().handle;
            float t0 = Time.time;
            while (SceneManager.GetActiveScene().handle == scene && Time.time < t0 + 10f) yield return null;
            TrackerBrain fresh = null;
            t0 = Time.time;
            while (Time.time < t0 + 3f && (fresh == null || fresh.Decider == null))
            {
                fresh = FindFirstObjectByType<TrackerBrain>();
                yield return null;
            }
            Check("e after the reload: a fresh Tracker is Idle with no memory",
                  fresh != null && fresh != agent && fresh.Decider != null && fresh.Decider.State == TrackerState.Idle && fresh.Decider.Memory.Count == 0,
                  fresh == null ? "no Tracker after reload" : $"state {fresh.StateName}, memory {(fresh.Decider == null ? -1 : fresh.Decider.Memory.Count)}");
        }
    }
}
