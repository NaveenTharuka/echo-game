using System.Collections.Generic;
using Echo.Sound;
using UnityEngine;

namespace Echo.AI
{
    /// LISTENER (guide §9): reacts to the most important thing it heard recently, investigates it, searches the
    /// area, and goes back. Short memory on purpose: one target sound + habituation.
    ///
    ///   Idle --faint--> Suspicious --2nd sound / 2.5 s--> Investigate --arrived--> Search --done/20 s--> Return --> Idle
    ///   Idle --loud---> Alert (scream, 0.9 s) --> Investigate at Run (better sound, with hysteresis, re-targets at any time)
    ///   any state: sound within strikeDistance (fresh, precise) or touch --> Attack --> Search (tight)
    ///
    /// Hunting pressure (so a gunshot + walking away is not a free escape):
    ///   - trail following: while hunting, a fresh step/run/land sound replaces an older target at once
    ///     (hysteresis would otherwise keep it walking to the stale gunshot for ~10 s);
    ///   - a hunting Listener moves at Run and its ears are sharper (Hearing.alertBoost);
    ///   - search starts where the prey was heading, then the nearby exits, then a ring;
    ///   - after losing the trail it stays on edge for edgeDuration: no slow "Suspicious" phase, straight back in.
    public class ListenerBrain : AgentBrain
    {
        enum State { Idle, Suspicious, Alert, Investigate, Search, Return, Attack }

        [Header("Listener")]
        public float strikeDistance = 3f;
        [Tooltip("How far the alert scream carries as a pack call to other agents (intensity x 45 m).")]
        public float packCallIntensity = 0.9f;
        [Tooltip("Strike reflex only for sounds localised better than this (m).")]
        public float strikePrecision = 1.5f;
        [Tooltip("Ignore sounds whose priority is below this.")]
        public float noticeCutoff = 0.05f;
        public float suspiciousCutoff = 0.15f, runCutoff = 0.5f;
        public float suspiciousWait = 2.5f;
        [Tooltip("Seconds it stands and screams before charging at a loud sound heard while calm.")]
        public float alertDuration = 0.9f;
        public float searchDuration = 20f, searchPause = 1.5f;
        public int searchPoints = 4;
        [Header("Hunting pressure")]
        [Tooltip("While hunting, a step/run sound adopts over the current target once that target is this old (s).")]
        public float trailRefresh = 0.8f;
        [Tooltip("Seconds it stays on edge after losing the trail (sharper ears, charges straight at sounds).")]
        public float edgeDuration = 30f;
        [Tooltip("Exits (Doorway nodes) closer than this to the search centre are checked first (m).")]
        public float exitSearchRadius = 14f;

        State state = State.Idle;
        PerceivedSound target; float targetPriority; bool hasTarget;
        float stateTime, pauseUntil;
        List<Vector3> searchList = new(); int searchIndex; Vector3 searchCentre;
        int investigateFailures;
        float edgeUntil = -1f;
        Vector3 trailFrom; float trailFromTime = -999f;   // previous trail position -> heading of the prey
        bool headingPoint;                                // searchList[0] is "where it was heading"

        public override string DisplayName => "LISTENER";
        public override string StateName => IsAlive ? state.ToString() : "Dead";
        public override float AlertLevel => state switch { State.Investigate => 0.7f, State.Search => 0.6f,
                                                           State.Attack => 1f, State.Alert => 0.8f,
                                                           _ => OnEdge ? 0.4f : state == State.Suspicious ? 0.3f : 0f };
        bool OnEdge => Time.time < edgeUntil;
        bool Hunting => state is State.Alert or State.Investigate or State.Search or State.Attack || OnEdge;
        public override float CurrentInterest => hasTarget ? CurrentPriority() : 0f;
        public override string DebugText => hasTarget
            ? $"target {target.Type} p={CurrentPriority():0.00} err={target.PositionError:0.0}m age={Time.time - target.TimeHeard:0.0}s"
            : "no target";

        float CurrentPriority() => ListenerPriority.Score(target.Loudness, target.Type,
                                        Time.time - target.TimeHeard, Habituation.Factor(target));

        protected override void Think()
        {
            // --- 3. reflex: very close, precise, fresh sound => strike
            foreach (var s in NewSounds)
                if (s.PositionError < strikePrecision &&
                    Vector3.Distance(s.EstimatedPosition, transform.position) <= strikeDistance)
                { Strike(s.EstimatedPosition); return; }

            // --- 4. decision: pick the best new sound, apply hysteresis (not mid-swing)
            if (!Body.IsAttacking)
            {
                PerceivedSound best = default; float bestP = 0f;
                foreach (var s in NewSounds)
                {
                    float p = ListenerPriority.Score(s.Loudness, s.Type, 0f, Habituation.Factor(s));
                    if (p > bestP) { bestP = p; best = s; }
                }
                bool freshTrail = Hunting && hasTarget && ListenerPriority.IsTrail(best.Type) &&
                                  Time.time - target.TimeHeard >= trailRefresh;
                if (bestP > noticeCutoff &&
                    (!hasTarget || freshTrail ||
                     ListenerPriority.ShouldSwitch(bestP, CurrentPriority(), Time.time - target.TimeHeard)))
                    Adopt(best, bestP);
            }

            // --- 5. state tick
            stateTime += thinkInterval;
            switch (state)
            {
                case State.Suspicious:
                    if (stateTime > suspiciousWait)
                    {
                        if (targetPriority >= ListenerPriority.InvestigateCutoff) BeginInvestigate(AgentMotor.Gait.Sneak);
                        else { hasTarget = false; Enter(State.Idle); }
                    }
                    break;

                case State.Alert:
                    if (stateTime >= alertDuration) BeginInvestigate(AgentMotor.Gait.Run);
                    break;

                case State.Investigate:
                    if (Arrived) BeginSearch(target.EstimatedPosition, target.PositionError + 2f, Heading());
                    else if (Failed)
                    {
                        // Can't reach the estimate: try a point 2 m closer to us, then give up and search here.
                        if (++investigateFailures >= 2) BeginSearch(transform.position, 4f);
                        else Motor.MoveTo(Vector3.MoveTowards(target.EstimatedPosition, transform.position, 2f), Motor.CurrentGait);
                    }
                    break;

                case State.Search:
                    if (stateTime > searchDuration || searchIndex >= searchList.Count) { EndInvestigation(); break; }
                    if (Time.time < pauseUntil) break;                       // listening pause
                    if (!Motor.IsMoving)
                    {
                        if (Arrived || Failed)
                        {
                            searchIndex++; pauseUntil = Time.time + searchPause;
                            if (Anim) Anim.SetListening(true);
                        }
                        else if (searchIndex < searchList.Count)
                        {
                            if (Anim) Anim.SetListening(false);
                            // The first point (where the prey was heading) is taken at a run.
                            Motor.MoveTo(searchList[searchIndex],
                                         searchIndex == 0 && headingPoint ? AgentMotor.Gait.Run : AgentMotor.Gait.Walk);
                        }
                    }
                    break;

                case State.Return:
                    if (Arrived || Failed) { Motor.LookAt(transform.position + HomeRotation * Vector3.forward * 10f); Enter(State.Idle); }   // far point: small settling doesn't skew the angle
                    break;

                case State.Attack:
                    if (stateTime > 1.8f && !Body.IsAttacking) BeginSearch(transform.position, 3f, Heading());
                    break;
            }
        }

        void Adopt(PerceivedSound s, float p)
        {
            bool hunting = Hunting;
            bool wasCalm = state is State.Idle or State.Return or State.Suspicious;
            if (hasTarget && ListenerPriority.IsTrail(target.Type)) { trailFrom = target.EstimatedPosition; trailFromTime = target.TimeHeard; }
            target = s; targetPriority = p; hasTarget = true; investigateFailures = 0;
            if (hunting)
            {
                // Already after something (or on edge): no listening pause, no scream - go for it.
                if (state != State.Alert) BeginInvestigate(AgentMotor.Gait.Run);
            }
            else if (p < suspiciousCutoff && state is State.Idle or State.Return)
            {
                Enter(State.Suspicious);
                Motor.Stop();
                Motor.LookAt(target.EstimatedPosition);
                if (Anim) Anim.SetListening(true);
            }
            else
            {
                var gait = p >= runCutoff ? AgentMotor.Gait.Run : AgentMotor.Gait.Walk;
                if (wasCalm && gait == AgentMotor.Gait.Run) BeginAlert();          // scream before the charge
                else if (state != State.Alert) BeginInvestigate(gait);              // (Alert already ends in a Run)
            }
        }

        void BeginAlert()
        {
            Enter(State.Alert);
            Motor.Stop();
            Motor.LookAt(target.EstimatedPosition);
            if (Anim) Anim.Alert();
            // The scream is a real sound the player hears. Other agents ignore AgentBody sounds, but the scream is
            // also a pack call: whoever can hear it learns where the Listener thinks the prey is.
            Vector3 mouth = transform.position + Vector3.up * 1.7f;
            Noise.Make(SoundType.AgentVoice, mouth, 0.5f, SoundSource.AgentBody, gameObject);
            int heard = PackCall.Raise(this, mouth, packCallIntensity, target.EstimatedPosition, target.PositionError);
            if (heard > 0) Debug.Log($"[Listener] scream heard by {heard} agent(s) - pack hunt", this);
        }

        void BeginInvestigate(AgentMotor.Gait gait)
        {
            Enter(State.Investigate);
            Motor.MoveTo(target.EstimatedPosition, gait);
        }

        /// Direction the prey was moving: from the previous trail sound to the current target (zero if unknown).
        Vector3 Heading()
        {
            if (!hasTarget || target.TimeHeard - trailFromTime > 6f) return Vector3.zero;
            Vector3 d = target.EstimatedPosition - trailFrom; d.y = 0f;
            return d.sqrMagnitude > 1f ? d.normalized : Vector3.zero;
        }

        void BeginSearch(Vector3 centre, float radius, Vector3 heading = default)
        {
            Enter(State.Search);
            searchCentre = centre;
            // 1. where it was heading, 2. the exits it could have left by, 3. a ring around the spot.
            searchList = new List<Vector3>();
            Vector3 ahead = centre;
            headingPoint = heading != Vector3.zero &&
                           SearchPattern.TryReachable(centre, centre + heading * 6f, out ahead);
            if (headingPoint) searchList.Add(ahead);
            searchList.AddRange(SearchPattern.Exits(centre, transform.position, exitSearchRadius, 2));
            searchList.AddRange(SearchPattern.Ring(centre, Mathf.Clamp(radius, 2f, 8f), searchPoints));
            searchIndex = 0; pauseUntil = Time.time + searchPause;
            Motor.Stop();
            if (Anim) Anim.SetListening(true);
        }

        void EndInvestigation()
        {
            if (hasTarget && target.Type is SoundType.Machinery or SoundType.Generator
                                         or SoundType.ObjectDrop or SoundType.Switch)
                Habituation.MarkBoring(target.Type, target.EstimatedPosition);
            hasTarget = false;
            edgeUntil = Time.time + edgeDuration;     // lost the trail, but it knows something is out there
            Enter(State.Return);
            Motor.MoveTo(Home, AgentMotor.Gait.Walk);
        }

        void Strike(Vector3 where) { Enter(State.Attack); Body.TryAttack(where); }

        public override void OnContact(Vector3 where) => Strike(where);

        public override void OnHurt(Vector3 from)
        {
            // Being shot = a loud, precise sound at the shooter.
            Adopt(new PerceivedSound { Type = SoundType.Gunshot, EstimatedPosition = from,
                                       PositionError = 2f, Loudness = 1f, TimeHeard = Time.time }, 1f);
        }

        public override void ResetAgent()
        {
            base.ResetAgent(); hasTarget = false; edgeUntil = -1f; trailFromTime = -999f; Enter(State.Idle);
        }

        void Enter(State s)
        {
            state = s; stateTime = 0f;
            Arrived = Failed = false;   // a motor event from the previous state must not end the new one
            if (Anim) Anim.SetListening(s == State.Suspicious);
        }

        void OnDrawGizmosSelected()
        {
            if (!Application.isPlaying) return;
            if (hasTarget)
            {
                Gizmos.color = new Color(1f, 0.5f, 0f, 0.9f);
                Gizmos.DrawLine(transform.position + Vector3.up, target.EstimatedPosition);
                Gizmos.DrawWireSphere(target.EstimatedPosition, Mathf.Max(0.2f, target.PositionError));
            }
            if (state == State.Search)
            {
                Gizmos.color = Color.yellow;
                Gizmos.DrawWireSphere(searchCentre, 0.3f);
                for (int i = 0; i < searchList.Count; i++)
                    Gizmos.DrawWireCube(searchList[i] + Vector3.up * 0.2f, Vector3.one * (i == searchIndex ? 0.5f : 0.3f));
            }
        }
    }
}
