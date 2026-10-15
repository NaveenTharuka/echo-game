using System;
using System.Collections.Generic;
using UnityEngine;

namespace Echo.AI.Tracker
{
    public enum TrackerState { Idle, Analyse, Intercept, Hold, Search, Attack }
    /// Why the last interception MoveTo was issued (diagnostics: overlay, scenarios, checks).
    public enum TrackerReplan { New, Band, World, GoalMoved, Failed }
    public enum TrackerGait { Walk, Run }
    public enum TrackerCommandKind { None, MoveTo, Stop, Attack }

    /// What the decider asks the body to do this tick. The body (mover, attack, animation) is not
    /// the decider's business: it only outputs goals.
    public readonly struct TrackerCommand
    {
        public readonly TrackerCommandKind Kind;
        public readonly Vector3 Target;
        public readonly TrackerGait Gait;

        public TrackerCommand(TrackerCommandKind kind, Vector3 target = default, TrackerGait gait = TrackerGait.Walk)
        {
            Kind = kind; Target = target; Gait = gait;
        }

        public static readonly TrackerCommand None = new(TrackerCommandKind.None);
        public static readonly TrackerCommand Stop = new(TrackerCommandKind.Stop);
    }

    public struct TrackerTickInput
    {
        public float Now;
        public Vector3 Self;
        /// Current ground speed (m/s): a standing start costs the ETA model its startup time.
        public float SelfSpeed;
        /// One-shot mover events since the previous tick.
        public bool Arrived, Failed;
        /// Touch sense: something brushed the Tracker this tick.
        public bool Touched;
        public Vector3 TouchPoint;
    }

    /// The Tracker's brain as plain C#: perception -> memory -> prediction -> confidence -> state.
    ///   Idle --credible sound--> Analyse (one tick) --> Intercept (run if C high, walk if medium)
    ///   Intercept --arrived--> Hold (listen) --nothing--> Search (best-first) --budget spent--> Idle
    ///   Intercept --C low / no candidate--> Search;   any --close precise sound or touch--> Attack
    /// Re-plans only when a door changes, the mover fails (then the failed node is banned for this trail), the
    /// current goal stops being a candidate, or a node > replanDistance away beats the current goal's CURRENT score
    /// by switchMargin (commitment: near-equal candidates no longer flip the goal on perception noise), or a goal
    /// picked as an interception loses its claim (the player got there first). A band or gait change re-issues the
    /// same goal at the new gait. Scoring is anchored on the fitted trail (fitAnchor), not the raw latest percept.
    public sealed class TrackerDecider
    {
        public readonly TrackerSettings Settings;
        public readonly ITrackerWorld World;
        public readonly RouteService Routes;
        public readonly TrackerMemory Memory;
        public readonly InterceptScorer Scorer = new();
        public readonly ColdTrailSearch Search = new();
        public readonly HeatMap Heat = new();

        public TrackerState State { get; private set; } = TrackerState.Idle;
        public TrackerReplan LastReplan { get; private set; }
        /// The trail point the last evaluation scored from: the latest sound, at the fitted position if fitAnchor.
        public TrackerObservation Anchor { get; private set; }
        /// Interception MoveTos issued since the last Reset.
        public int IssueCount { get; private set; }
        public ConfidenceParts Confidence { get; private set; }
        public ConfidenceBand Band { get; private set; }
        public bool HasVelocity { get; private set; }
        public Vector3 Velocity { get; private set; }
        /// The player's speed behind ETA_player (settings.playerEtaSpeed; see VelocityEstimator).
        public float PlayerSpeed { get; private set; }
        public Vector3 Predicted { get; private set; }
        public bool HasGoal { get; private set; }
        public Vector3 Goal { get; private set; }
        public int GoalNode { get; private set; } = -1;
        /// The goal is a scored interception the Tracker claims to reach first (adv > 0), not the latest sound.
        public bool InterceptGoal => interceptGoal;
        public Vector3 Home { get; set; }

        // Perception counters (overlay / checks).
        public int Accepted { get; private set; }
        public int Suspects { get; private set; }
        public int Resets { get; private set; }
        public int Filtered { get; private set; }
        public ObservationResult LastResult { get; private set; }
        public float LastCredibility { get; private set; }

        bool[] banned = Array.Empty<bool>();
        float stateStart, rms, holdUntil;
        bool searchHolding, needHome, interceptGoal, runGoal, scoredPick, goalScored, goalClaimed;
        int selfNode, goalVersion;
        TrackerGait goalGait;

        public TrackerDecider(ITrackerWorld world, TrackerSettings settings, Vector3 home)
        {
            World = world; Settings = settings; Home = home;
            Routes = new RouteService(world, settings);
            Memory = new TrackerMemory(settings);
        }

        public float StateTime(float now) => now - stateStart;
        public bool IsBanned(int node) => node >= 0 && node < banned.Length && banned[node];

        /// Player died / scene reset: forget everything and go home.
        public void Reset()
        {
            Memory.Clear(); Heat.Clear(); Search.Stop();
            Array.Clear(banned, 0, banned.Length);
            HasVelocity = false; HasGoal = false; GoalNode = -1; Confidence = default; Band = ConfidenceBand.Search;
            State = TrackerState.Idle; needHome = true;
        }

        /// `percepts` is sorted in place by time. Allocation-free.
        public TrackerCommand Tick(in TrackerTickInput input, List<TrackerPercept> percepts)
        {
            var s = Settings;
            float now = input.Now;
            int n = World.Count;
            if (banned.Length != n) banned = new bool[n];
            Heat.EnsureSize(n);
            Heat.Decay(s.heatDecay);
            selfNode = World.Nearest(input.Self);

            // PERCEPTION -> MEMORY (oldest first, so the credibility chain is in time order)
            SortByTime(percepts);
            bool newCredible = false, strike = false;
            Vector3 strikeAt = default;
            for (int i = 0; i < percepts.Count; i++)
            {
                var p = percepts[i];
                if (TrackerSoundFilter.IsTrail(p.Kind) && p.Error <= s.strikeMaxError &&
                    Vector3.Distance(p.Position, input.Self) <= s.strikeDistance)
                { strike = true; strikeAt = p.Position; }

                int node = World.Nearest(p.Position);
                var r = Memory.Add(p, node, Routes, out float cred);
                LastResult = r; LastCredibility = cred;
                switch (r)
                {
                    case ObservationResult.Filtered: Filtered++; break;
                    case ObservationResult.Suspect: Suspects++; break;
                    case ObservationResult.Reset:
                        Resets++; newCredible = true; Heat.Add(node);
                        Array.Clear(banned, 0, banned.Length);
                        break;
                    default: Accepted++; newCredible = true; Heat.Add(node); break;
                }
            }
            Memory.Prune(now);

            if (input.Touched) { strike = true; strikeAt = input.TouchPoint; }
            if (strike)
            {
                Enter(TrackerState.Attack, now);
                HasGoal = false;
                return new TrackerCommand(TrackerCommandKind.Attack, strikeAt);
            }

            switch (State)
            {
                case TrackerState.Idle:
                    if (newCredible) return ToAnalyse(now);
                    if (needHome)
                    {
                        needHome = false;
                        if (Vector3.Distance(input.Self, Home) > 1f)
                            return new TrackerCommand(TrackerCommandKind.MoveTo, Home, TrackerGait.Walk);
                    }
                    return TrackerCommand.None;

                case TrackerState.Analyse:
                {
                    bool has = EvaluateForGait(now, input, ConfidenceBand.Search, false, out var target, out int node, out var band);
                    Band = band;
                    if (!has || Band == ConfidenceBand.Search) return EnterSearch(now);
                    Enter(TrackerState.Intercept, now);
                    LastReplan = TrackerReplan.New;
                    return Issue(target, node);
                }

                case TrackerState.Intercept:
                {
                    bool has = EvaluateForGait(now, input, Band, true, out var target, out int node, out var band);
                    bool bandChanged = band != Band;
                    Band = band;
                    if (!has || Band == ConfidenceBand.Search) return EnterSearch(now);
                    if (input.Failed)
                    {
                        if (GoalNode >= 0) banned[GoalNode] = true;
                        if (!Evaluate(now, input.Self, input.SelfSpeed, s.runToIntercept ? s.runSpeed : TrackerMotion.GaitSpeed(Band, s), out target, out node))
                            return EnterSearch(now);
                        LastReplan = TrackerReplan.Failed;
                        return Issue(target, node);
                    }
                    if (input.Arrived)
                    {
                        Enter(TrackerState.Hold, now);
                        HasGoal = false;
                        return TrackerCommand.Stop;
                    }
                    if (s.switchMargin > 0f && HasGoal && World.Version == goalVersion && KeepGoal(target))
                    {
                        // Committed: same goal; only the gait may change (band change or the goal's adv crossing runAdvMin).
                        interceptGoal = Scorer.Claimed[GoalNode];
                        runGoal = Scorer.Advantage[GoalNode] > s.runAdvMin;
                        if (!bandChanged && GaitFor() == goalGait) return TrackerCommand.None;
                        LastReplan = TrackerReplan.Band;
                        return Issue(Goal, GoalNode, kept: true);
                    }
                    if (!HasGoal || bandChanged || World.Version != goalVersion ||
                        Vector3.Distance(target, Goal) > s.replanDistance)
                    {
                        LastReplan = !HasGoal ? TrackerReplan.New : bandChanged ? TrackerReplan.Band
                                   : World.Version != goalVersion ? TrackerReplan.World : TrackerReplan.GoalMoved;
                        return Issue(target, node);
                    }
                    return TrackerCommand.None;
                }

                case TrackerState.Hold:
                    if (newCredible) return ToAnalyse(now);
                    if (now - stateStart >= s.holdTime) return EnterSearch(now);
                    return TrackerCommand.None;

                case TrackerState.Search:
                    if (newCredible) { Search.Stop(); return ToAnalyse(now); }
                    if (Search.ClockStarted && now - Search.StartTime > s.searchMaxTime) return EnterIdle();
                    if (searchHolding)
                        return now >= holdUntil ? NextSearchTarget(now) : TrackerCommand.None;
                    if (input.Failed) return NextSearchTarget(now);
                    if (input.Arrived)
                    {
                        Search.StartClock(now);
                        searchHolding = true; holdUntil = now + s.searchHold; HasGoal = false;
                        return TrackerCommand.Stop;
                    }
                    return TrackerCommand.None;

                case TrackerState.Attack:
                    if (now - stateStart < s.attackTime) return TrackerCommand.None;
                    return ToAnalyse(now);
            }
            return TrackerCommand.None;
        }

        TrackerCommand ToAnalyse(float now)
        {
            Enter(TrackerState.Analyse, now);
            HasGoal = false;
            return TrackerCommand.Stop;          // one tick standing still, listening
        }

        /// Evaluate + confidence band, with ETA_tracker at the speed of the gait that band will issue. Scored first
        /// at the current band's gait; if the resulting band asks for the other gait, scored again at that speed and
        /// the band kept, so the ETA behind the goal is the ETA of the gait the MoveTo really uses.
        bool EvaluateForGait(float now, in TrackerTickInput input, ConfidenceBand current, bool hysteresis,
                             out Vector3 target, out int node, out ConfidenceBand band)
        {
            var s = Settings;
            // runToIntercept: every interception it claims (adv > 0) is run to, so it is scored at run speed.
            float speed = s.runToIntercept ? s.runSpeed : TrackerMotion.GaitSpeed(current, s);
            bool has = Evaluate(now, input.Self, input.SelfSpeed, speed, out target, out node);
            band = hysteresis ? ConfidenceModel.Classify(Confidence.Value, current, s) : ConfidenceModel.Classify(Confidence.Value, s);
            if (has && band != ConfidenceBand.Search && s.etaFromGait && !s.runToIntercept && TrackerMotion.GaitSpeed(band, s) != speed)
            {
                has = Evaluate(now, input.Self, input.SelfSpeed, TrackerMotion.GaitSpeed(band, s), out target, out node);
                if (!has) band = ConfidenceBand.Search;
            }
            return has;
        }

        /// Prediction + confidence. With >= 2 credible sounds spanning >= minSpan: scored interception
        /// node. Otherwise: investigate the latest sound itself (no direction known).
        bool Evaluate(float now, Vector3 self, float selfSpeed, float gaitSpeed, out Vector3 target, out int node)
        {
            var s = Settings;
            target = default; node = -1;
            if (!Memory.HasLast) { HasVelocity = false; Confidence = default; return false; }
            var last = Memory.Last;

            HasVelocity = VelocityEstimator.TryEstimate(Memory, now, s, out var v, out rms, out float raw, out float moving, out var anchor);
            Velocity = HasVelocity ? v : Vector3.zero;
            PlayerSpeed = !HasVelocity ? 0f : s.playerEtaSpeed switch
            {
                PlayerEtaSpeed.Raw => raw, PlayerEtaSpeed.Moving => moving, _ => Velocity.magnitude
            };
            float margin = 0f;
            bool has = false;
            interceptGoal = runGoal = scoredPick = false;
            if (HasVelocity && s.fitAnchor)
            {
                last.Position = new Vector3(anchor.x, last.Position.y, anchor.z);
                last.Node = World.Nearest(last.Position);
            }
            Anchor = last;
            if (HasVelocity &&
                Scorer.Evaluate(Routes, last, Velocity, self, selfNode, now, Heat, banned, s, gaitSpeed, selfSpeed,
                               PlayerSpeed) &&
                Scorer.BestScore >= s.minInterceptScore)
            {
                has = scoredPick = true;
                node = Scorer.Best; target = World.Position(node);
                interceptGoal = Scorer.Claimed[node];
                runGoal = Scorer.Advantage[node] > s.runAdvMin;
                margin = Scorer.Margin;
                Predicted = Scorer.Predicted;
            }
            else
            {
                // No usable prediction (single sound, or no node both plausible and explained by the trail):
                // investigate the latest sound itself rather than commit to a near-zero tie-break.
                node = last.Node; target = last.Position; Predicted = last.Position;
                has = node >= 0 && !banned[node] &&
                      !float.IsPositiveInfinity(Routes.Field(selfNode).Dist[node]);
                margin = HasVelocity ? 0f : 1f;  // single sound: unambiguous; failed prediction: ambiguous
            }
            Confidence = ConfidenceModel.Compute(now - last.Time, Memory.Count, HasVelocity, rms, margin, s);
            return has;
        }

        /// Commitment test for a scored goal against this tick's scored best `target`. Unscored goals (latest
        /// sound) keep the distance rule.
        bool KeepGoal(Vector3 target)
        {
            var s = Settings;
            if (!goalScored || !scoredPick || GoalNode < 0 || GoalNode >= Scorer.IsCandidate.Length) return false;
            if (!Scorer.IsCandidate[GoalNode] || banned[GoalNode]) return false;      // infeasible now
            if (goalClaimed && !Scorer.Claimed[GoalNode]) return false;              // interception lost (player passed it)
            if (Vector3.Distance(target, Goal) <= s.replanDistance) return true;      // same place, as before
            return Scorer.BestScore < Scorer.Scores[GoalNode] + s.switchMargin;      // not clearly better
        }

        TrackerGait GaitFor() =>
            Band == ConfidenceBand.Run || (Settings.runToIntercept && runGoal && Band != ConfidenceBand.Search)
                ? TrackerGait.Run : TrackerGait.Walk;

        TrackerCommand Issue(Vector3 target, int node, bool kept = false)
        {
            IssueCount++;
            if (!kept) { goalScored = scoredPick && node == Scorer.Best; goalClaimed = goalScored && interceptGoal; }
            Goal = target; GoalNode = node; HasGoal = true;
            goalGait = GaitFor();
            goalVersion = World.Version;
            return new TrackerCommand(TrackerCommandKind.MoveTo, target, goalGait);
        }

        TrackerCommand EnterSearch(float now)
        {
            if (!Memory.HasLast) return EnterIdle();
            Search.Begin(Routes, Memory.Last, HasVelocity ? Velocity : Vector3.zero, Heat, now, Settings, startClock: false);
            Enter(TrackerState.Search, now);
            Band = ConfidenceBand.Search;
            return NextSearchTarget(now);
        }

        TrackerCommand NextSearchTarget(float now)
        {
            searchHolding = false;
            var fromSelf = Routes.Field(selfNode).Dist;
            while (true)
            {
                int node = Search.Next(now);
                if (node < 0) return EnterIdle();
                if (banned[node] || float.IsPositiveInfinity(fromSelf[node])) continue;   // budget still counts it
                Goal = World.Position(node); GoalNode = node; HasGoal = true;
                goalGait = TrackerGait.Walk; goalVersion = World.Version;
                return new TrackerCommand(TrackerCommandKind.MoveTo, Goal, TrackerGait.Walk);
            }
        }

        TrackerCommand EnterIdle()
        {
            Memory.Clear(); Search.Stop();
            Array.Clear(banned, 0, banned.Length);
            HasVelocity = false; HasGoal = false; GoalNode = -1; Confidence = default; Band = ConfidenceBand.Search;
            State = TrackerState.Idle;
            needHome = false;
            return new TrackerCommand(TrackerCommandKind.MoveTo, Home, TrackerGait.Walk);
        }

        void Enter(TrackerState st, float now) { State = st; stateStart = now; searchHolding = false; }

        static void SortByTime(List<TrackerPercept> list)
        {
            for (int i = 1; i < list.Count; i++)
            {
                var x = list[i]; int j = i - 1;
                while (j >= 0 && list[j].Time > x.Time) { list[j + 1] = list[j]; j--; }
                list[j + 1] = x;
            }
        }
    }
}
