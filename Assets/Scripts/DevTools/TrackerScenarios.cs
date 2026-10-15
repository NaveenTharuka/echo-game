using System.Collections;
using System.Collections.Generic;
using System.Text;
using Echo.AI.Tracker;
using Echo.Interaction;
using Echo.Navigation;
using Echo.Player;
using UnityEngine;
using UnityEngine.SceneManagement;
using L = Echo.AI.Tracker.SandboxTrackerLayout;

namespace Echo.DevTools
{
    /// Play Mode scenarios for the Tracker in Sandbox_Tracker (ECHO > Tracker > Run Scenarios). A scripted
    /// "ghost" emits noises through InteractionNoise, so the Tracker hears it exactly like the player; the real
    /// player is frozen and parked in the west room (invulnerable until the death scenario).
    ///   a straight run  - from a listening post 6 m north of the junction, the Tracker reaches an interception
    ///                     node on the runner's line before the runner does (adv > 0). (From the home room the
    ///                     runner is first audible 3.6 m before the junction and no node is reachable first.)
    ///   b fake sound    - an audible but impossible gunshot mid-trail (the node the Tracker can hear that is
    ///                     farthest by route from the runner) is heard, rejected as a suspect, and the trail and
    ///                     goal stay with the runner
    ///   c player quiet  - best-first search (>= 1 node visited), then Idle with memory cleared
    ///   d locked door   - door locked while the Tracker heads through it: it re-routes via the loop and arrives
    ///   e player death  - Reset on Died (Idle, memory empty); after the reload a fresh Tracker is Idle
    /// The runner survives the scene reload of (e) (DontDestroyOnLoad) and logs one report.
    /// ECHO > Tracker > Run Scenario A x10: (a) only, repeated (perception noise makes single runs vary); reports the
    /// pass rate and, for every interception arrival the runner also reached, ETA predicted at the pick vs taken,
    /// for the Tracker and for the runner separately.
    public class TrackerScenarios : MonoBehaviour
    {
        const float GhostSpeed = 3f, StepInterval = 0.5f, StepIntensity = 0.6f;

        readonly StringBuilder report = new();
        int passed, total;
        TrackerStubAgent agent;
        Door door;
        PlayerTestRig rig;
        Transform ghost;
        Vector3 home;

        readonly List<float> etaErrTracker = new(), etaErrRunner = new();

        public static void Launch()
        {
            var go = new GameObject("_TrackerScenarios");
            DontDestroyOnLoad(go);
            go.AddComponent<TrackerScenarios>().StartCoroutine(go.GetComponent<TrackerScenarios>().RunAll());
        }

        public static void LaunchA(int repeats)
        {
            var go = new GameObject("_TrackerScenarios");
            go.AddComponent<TrackerScenarios>().StartCoroutine(go.GetComponent<TrackerScenarios>().RunA(repeats));
        }

        IEnumerator RunA(int repeats)
        {
            yield return Setup();
            if (agent == null) { Destroy(gameObject); yield break; }
            for (int i = 0; i < repeats; i++) { yield return A_StraightRun(); yield return Between(); }
            rig.Restore();
#if UNITY_EDITOR
            PlayerDeath.Current.SetInvulnerable(false);
#endif
            string msg = $"[TrackerScenarios] (a) x{repeats}: {passed}/{total} passed\n" +
                         $"ETA error (taken - predicted at the pick), arrivals both reached, n={etaErrTracker.Count}: " +
                         $"Tracker {Stats(etaErrTracker)}; runner {Stats(etaErrRunner)}\n{report}";
            if (passed == total) Debug.Log(msg); else Debug.LogError(msg);
            Destroy(ghost.gameObject);
            Destroy(gameObject);
        }

        static string Stats(List<float> xs)
        {
            if (xs.Count == 0) return "-";
            float sum = 0f, max = 0f;
            foreach (var x in xs) { sum += x; max = Mathf.Max(max, Mathf.Abs(x)); }
            float mean = sum / xs.Count, ss = 0f;
            foreach (var x in xs) ss += (x - mean) * (x - mean);
            return $"mean {mean:+0.00;-0.00} s, sd {Mathf.Sqrt(ss / Mathf.Max(1, xs.Count - 1)):0.00} s, max |err| {max:0.00} s";
        }

        IEnumerator RunAll()
        {
            yield return Setup();
            if (agent == null) { Destroy(gameObject); yield break; }

            yield return A_StraightRun();   yield return Between();
            yield return B_FakeSound();     yield return Between();
            yield return C_GoesQuiet();     yield return Between();
            yield return D_LockedDoor();    yield return Between();
            yield return E_PlayerDeath();

            string msg = $"[TrackerScenarios] {passed}/{total} passed\n{report}";
            if (passed == total) Debug.Log(msg); else Debug.LogError(msg);
            Destroy(gameObject);
        }

        IEnumerator Setup()
        {
            agent = FindFirstObjectByType<TrackerStubAgent>();
            door = FindFirstObjectByType<Door>();
            var interactor = FindFirstObjectByType<PlayerInteractor>();
            if (agent == null || door == null || interactor == null || PlayerDeath.Current == null)
            {
                Debug.LogError("[TrackerScenarios] Needs Sandbox_Tracker in Play Mode (Tracker, Door_South, player).");
                yield break;
            }
            home = agent.transform.position;
            rig = new PlayerTestRig(interactor);
            rig.Freeze();
            rig.Player.position = new Vector3(L.Centre(0, 5).x, rig.Player.position.y, L.Centre(0, 5).z);
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
            while (InteractionNoise.IsMuted || agent.Decider == null) yield return null;   // settle mute after load
            agent.ResetAgent(home, true);
            yield return new WaitForSeconds(0.5f);
        }

        IEnumerator Between()
        {
            ghost.gameObject.SetActive(false);
            if (door.isLocked) door.Unlock();
            if (door.isOpen) door.SetOpen(false, null, false);
            agent.ResetAgent(home, true);
            yield return new WaitForSeconds(1f);
        }

        void Check(string name, bool ok, string observed)
        {
            total++; if (ok) passed++;
            report.AppendLine($"{(ok ? "PASS" : "FAIL")}  {name}  ({observed})");
        }

        static Vector3 C(int x, int z) => L.Centre(x, z);

        /// Moves the ghost from a to b at GhostSpeed, a gunshot first if `shot`, a step noise every StepInterval.
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

        static Vector3 Feet(Transform t) { var p = t.position; p.y = 0f; return p; }

        // ------------------------------------------------------------------ a
        IEnumerator A_StraightRun()
        {
            var d = agent.Decider;
            var nav = agent.GetComponent<UnityEngine.AI.NavMeshAgent>();
            var arrivals = new List<Arrival>();
            TrackerState prev = d.State;
            int goal = -1; float goalAdv = 0f, pickedAt = 0f, walk = 0f, stopped = 0f, attack = 0f, predT = 0f, predP = 0f;
            bool scored = false;
            agent.ResetAgent(C(4, 5), true);                  // listening post: interception is feasible from here
            // Every MoveTo the decider issues (printed only if the run fails): why, where, and how close the race was.
            var issues = new StringBuilder();
            int issueCount = d.IssueCount, issueLines = 0;
            System.Func<bool> watch = () =>
            {
                if (d.IssueCount != issueCount)
                {
                    issueCount = d.IssueCount;
                    if (issueLines++ < 80)
                    {
                        var sc = d.Scorer; int b = d.GoalNode, s2 = sc.Second;
                        Vector3 me = Feet(agent.transform);
                        issues.Append($"\n    t={Time.time:0.0} {d.LastReplan} goal={b} S={(b >= 0 && b < sc.Scores.Length ? sc.Scores[b] : 0f):0.00} " +
                                      $"2nd={s2} S2={(s2 >= 0 ? sc.Scores[s2] : 0f):0.00} adv={(b >= 0 && b < sc.Advantage.Length ? sc.Advantage[b] : 0f):0.00} " +
                                      $"band={d.Band} C={d.Confidence.Value:0.00} me=({me.x:0.0},{me.z:0.0}) spd={nav.velocity.magnitude:0.0} " +
                                      $"pred=({d.Predicted.x:0.0},{d.Predicted.z:0.0}) ghost=({ghost.position.x:0.0},{ghost.position.z:0.0})");
                    }
                }
                if (d.State == TrackerState.Intercept && d.HasVelocity && d.GoalNode >= 0 && d.GoalNode != goal)
                {
                    goal = d.GoalNode; goalAdv = d.Scorer.Advantage[goal]; pickedAt = Time.time; walk = stopped = attack = 0f;
                    scored = d.InterceptGoal; predT = d.Scorer.EtaTracker[goal]; predP = d.Scorer.EtaPlayer[goal];
                }
                // Where the time between the pick and the arrival goes (the ETA assumes uninterrupted motion).
                if (goal >= 0)
                {
                    if (d.State == TrackerState.Attack) attack += Time.deltaTime;
                    else if (nav.velocity.sqrMagnitude < 0.25f) stopped += Time.deltaTime;
                    else if (nav.speed < agent.settings.runSpeed - 0.01f) walk += Time.deltaTime;
                }
                if (d.State == TrackerState.Hold && prev == TrackerState.Intercept && goal >= 0)
                    arrivals.Add(new Arrival { Node = goal, Tracker = Time.time, Ghost = -1f, Adv = goalAdv, Picked = pickedAt,
                                               Walk = walk, Stopped = stopped, Attack = attack, Scored = scored, PredT = predT, PredP = predP });
                if (d.State == TrackerState.Hold && prev == TrackerState.Intercept) goal = -1;   // a re-issue after Hold is a new pick
                prev = d.State;
                Vector3 gp = Feet(ghost);
                for (int i = 0; i < arrivals.Count; i++)
                    if (arrivals[i].Ghost < 0f && Vector3.Distance(gp, d.World.Position(arrivals[i].Node)) <= 1.5f)
                    { var a = arrivals[i]; a.Ghost = Time.time; arrivals[i] = a; }
                return false;
            };
            yield return Run(C(0, 4), C(8, 4), true, watch);
            float until = Time.time + 2f;
            while (Time.time < until) { watch(); yield return null; }

            int hit = arrivals.FindIndex(x => x.Ghost > x.Tracker);
            var sb = new StringBuilder();
            foreach (var x in arrivals)
            {
                sb.Append($"node {x.Node} at {d.World.Position(x.Node)}{(x.Scored ? "" : " [latest sound, not scored]")}: " +
                          $"Tracker t={x.Tracker:0.0} ghost t={(x.Ghost < 0 ? "never" : x.Ghost.ToString("0.0"))} adv={x.Adv:0.00} " +
                          $"(picked t={x.Picked:0.0}; Tracker ETA {x.PredT:0.0} s took {x.Tracker - x.Picked:0.0} s; ");
                if (x.Ghost >= 0f) sb.Append($"runner ETA {x.PredP:0.0} s took {x.Ghost - x.Picked:0.0} s; ");
                sb.Append($"walked {x.Walk:0.0} s, stopped {x.Stopped:0.0} s, attack {x.Attack:0.0} s); ");
                if (x.Scored && x.Ghost >= 0f && x.Attack == 0f)
                { etaErrTracker.Add(x.Tracker - x.Picked - x.PredT); etaErrRunner.Add(x.Ghost - x.Picked - x.PredP); }
            }
            bool pass = hit >= 0 && arrivals[hit].Adv > 0f;
            if (!pass) sb.Append($"\n  MoveTo log ({issueLines}):{issues}");
            Check("a straight run: Tracker reaches an interception node before the runner (adv > 0)",
                  pass, arrivals.Count == 0 ? $"no interception arrival; state={d.State} heard={d.Accepted}{(pass ? "" : sb.ToString())}" : sb.ToString());
        }

        struct Arrival { public int Node; public float Tracker, Ghost, Adv, Picked, Walk, Stopped, Attack, PredT, PredP; public bool Scored; }

        // ------------------------------------------------------------------ b
        IEnumerator B_FakeSound()
        {
            var d = agent.Decider;
            var ears = agent.GetComponent<TrackerPerceptionStub>();
            Vector3 fake = default;
            int suspectsBefore = 0; bool fired = false, heard = false; float firedAt = 0f, fakeRoute = 0f;
            float worstGoalToFake = float.PositiveInfinity, trailToGhost = 0f;
            int accepted0 = d.Accepted;
            System.Func<bool> watch = () =>
            {
                if (!fired && d.Accepted - accepted0 >= 3)   // a real trail exists
                {
                    // The fake must reach the credibility gate: audible to the Tracker, far by route from the runner,
                    // and clear of the Tracker (no strike) and of its current goal (so "goal moved to the fake" is
                    // measurable).
                    var w = d.World; Vector3 runner = Feet(ghost), me = Feet(agent.transform);
                    int rn = w.Nearest(runner); float best = -1f;
                    for (int n = 0; n < w.Count; n++)
                    {
                        Vector3 p = w.Position(n);
                        if (Vector3.Distance(p, me) < 15f || (d.HasGoal && Vector3.Distance(p, d.Goal) < 15f) || !ears.WouldHear(p, 1f)) continue;
                        float r = d.Routes.Route(runner, rn, p, n);
                        if (r > best) { best = r; fake = p; }
                    }
                    if (best < 0f) return false;                  // nothing suitable yet; try next frame
                    suspectsBefore = d.Suspects; fakeRoute = best;
                    int heardBefore = ears.HeardCount;
                    var f = new GameObject("FakeSource");
                    InteractionNoise.Emit(NoiseType.Gunshot, fake, 1f, NoiseSource.Player, f);   // what a Mimic would do
                    Destroy(f);
                    heard = ears.HeardCount > heardBefore;
                    fired = true; firedAt = Time.time;
                }
                if (fired && Time.time > firedAt + 0.3f && Time.time < firedAt + 2.5f)
                {
                    if (d.HasGoal) worstGoalToFake = Mathf.Min(worstGoalToFake, Vector3.Distance(d.Goal, fake));
                    if (d.Memory.HasLast) trailToGhost = Mathf.Max(trailToGhost, Vector3.Distance(d.Memory.Last.Position, Feet(ghost)));
                }
                return fired && Time.time > firedAt + 2.5f;
            };
            yield return Run(C(1, 4), C(8, 4), true, watch);
            Check("b fake sound: heard, rejected as a suspect, trail and goal stay with the runner",
                  fired && heard && d.Suspects > suspectsBefore && trailToGhost < 8f && worstGoalToFake > 12f,
                  $"fired={fired} at {fake} ({fakeRoute:0} m by route from the runner) heard={heard} suspects +{d.Suspects - suspectsBefore} " +
                  $"resets={d.Resets} last-obs-to-runner max {trailToGhost:0.0} m, " +
                  $"goal-to-fake min {(float.IsPositiveInfinity(worstGoalToFake) ? "-" : worstGoalToFake.ToString("0.0"))} m");
        }

        // ------------------------------------------------------------------ c
        IEnumerator C_GoesQuiet()
        {
            var d = agent.Decider;
            yield return Run(C(1, 4), C(4, 4), true);
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
            Vector3 junction = C(4, 4), target = C(4, 1);
            agent.ResetAgent(junction, true);
            ghost.position = target + Vector3.up * 0.5f;
            ghost.gameObject.SetActive(true);
            float start = Time.time, nextShot = start, lockedAt = -1f, maxX = 0f, minDist = float.PositiveInfinity;
            bool crossedLocked = false;
            while (Time.time < start + 50f)
            {
                if (Time.time >= nextShot) { InteractionNoise.Emit(NoiseType.Gunshot, target, 1f, NoiseSource.Player, ghost.gameObject); nextShot = Time.time + 2f; }
                Vector3 p = Feet(agent.transform);
                if (lockedAt < 0f && d.HasGoal && p.z < junction.z - 2f) { door.Lock(); lockedAt = Time.time; }   // heading through the door
                if (lockedAt > 0f)
                {
                    maxX = Mathf.Max(maxX, p.x);
                    if (p.z < L.DoorA.y * L.Cell - 0.5f && Mathf.Abs(p.x - junction.x) < 3f && p.z > L.DoorB.y * L.Cell) crossedLocked = true;
                }
                minDist = Mathf.Min(minDist, Vector3.Distance(p, target));
                if (lockedAt > 0f && minDist <= 4f) break;
                yield return null;
            }
            Check("d locked door on the best route: re-route via the loop and arrive",
                  lockedAt > 0f && minDist <= 4f && maxX > C(7, 0).x && !crossedLocked,
                  $"locked at {(lockedAt < 0 ? "-" : (lockedAt - start).ToString("0.0"))} s, closest {minDist:0.0} m after {Time.time - start:0.0} s, " +
                  $"max x {maxX:0.0} (loop x > {C(7, 0).x}), crossed locked door {crossedLocked}, state {d.State}");
        }

        // ------------------------------------------------------------------ e
        IEnumerator E_PlayerDeath()
        {
            var d = agent.Decider;
            yield return Run(C(1, 4), C(4, 4), true);
            int memBefore = d.Memory.Count;
            var stateBefore = d.State;
#if UNITY_EDITOR
            PlayerDeath.Current.SetInvulnerable(false);
#endif
            bool killed = PlayerDeath.Current.Kill(agent.gameObject);
            Check("e player death: Tracker resets at once (Idle, memory empty)",
                  killed && d.State == TrackerState.Idle && d.Memory.Count == 0,
                  $"killed={killed} before: {stateBefore}/{memBefore} obs, after: {d.State}/{d.Memory.Count} obs");

            // The scene reloads after the fade; a fresh Tracker must start Idle.
            int scene = SceneManager.GetActiveScene().handle;
            float t0 = Time.time;
            while (SceneManager.GetActiveScene().handle == scene && Time.time < t0 + 10f) yield return null;
            yield return new WaitForSeconds(0.5f);
            var fresh = FindFirstObjectByType<TrackerStubAgent>();
            Check("e after the reload: a fresh Tracker is Idle with no memory",
                  fresh != null && fresh != agent && fresh.Decider != null && fresh.Decider.State == TrackerState.Idle && fresh.Decider.Memory.Count == 0,
                  fresh == null ? "no Tracker after reload" : $"state {fresh.Decider?.State}, memory {fresh.Decider?.Memory.Count}");
        }
    }
}
