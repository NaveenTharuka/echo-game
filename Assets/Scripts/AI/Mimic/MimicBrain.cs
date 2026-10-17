using System.Collections;
using Echo.Navigation;
using Echo.Sound;
using UnityEngine;

namespace Echo.AI
{
    /// Agent 3 (guide §11): a manipulator. It barely hunts; it decides where and which fake sound to make so the other
    /// agents (simulated with their own public reaction rule) and the player end up where it wants them.
    /// Changes from the guide: the fake is voiced (stop, face the spot, Call animation, sound when the jaw opens);
    /// it holds a listening pose while observing; Cover trails run away from where it flees, not after it.
    public class MimicBrain : AgentBrain
    {
        enum State { Observe, Relocate, Generate, Cooldown, Flee }
        public enum Action { None, Summon, Flush, Cover }

        [Header("Mimic")]
        public float voiceRange = 18f, decisionInterval = 1f;
        public Vector2 cooldownRange = new(10f, 15f);
        [Tooltip("Seconds into the Call animation when the fake sound goes off (jaw fully open).")]
        public float callDelay = 0.75f, callDuration = 1.9f;
        [Tooltip("Gunshots closer than this make it flee (Cover).")]
        public float threatRadius = 12f;
        public bool logDecisions = true;

        State state = State.Observe;
        float stateTime, nextDecision, cooldownUntil;
        bool emitted;
        Vector3 playerEst; float playerEstTime = -999f, playerEstErr = 99f; SoundType playerLastType = SoundType.Footstep;
        Action plannedAction; int plannedNode = -1; SoundType plannedType; float plannedUtility;
        SoundType lastFakeType = SoundType.Machinery; int lastFakeNode = -1; float lastFakeTime = -999f;

        public override string DisplayName => "MIMIC";
        public override string StateName => state.ToString();
        public override float AlertLevel => state == State.Flee ? 0.6f : state == State.Generate ? 0.4f : 0.1f;
        public override string DebugText =>
            $"plan {plannedAction} node {plannedNode} {plannedType} U={plannedUtility:0.00}  player est age {Time.time - playerEstTime:0}s";
        public Action LastAction { get; private set; }

        protected override void Think()
        {
            // PERCEPTION -> MEMORY: one "best guess" of the player (it cannot tell who made a sound; agents' own
            // movement is filtered by Hearing, so what is left is the player, the environment and other fakes).
            foreach (var s in NewSounds)
            {
                if (ListenerPriority.TypeWeight(s.Type) < 0.3f) continue;
                playerEst = s.EstimatedPosition; playerEstTime = s.TimeHeard; playerEstErr = s.PositionError;
                playerLastType = s.Type;
                if (s.Type == SoundType.Gunshot && Vector3.Distance(s.EstimatedPosition, transform.position) < threatRadius)
                    StartCover("gunshot close by");
            }

            var g = NavGraph.Instance;
            if (g == null || g.Count == 0) return;   // nothing to plan on yet

            stateTime += thinkInterval;
            switch (state)
            {
                case State.Observe:
                    if (Time.time >= nextDecision && Time.time >= cooldownUntil)
                    {
                        nextDecision = Time.time + decisionInterval;
                        if (SelectDeception()) BeginRelocateOrGenerate();
                    }
                    break;
                case State.Relocate:
                    if (Arrived || InVoiceRange(plannedNode)) BeginGenerate();
                    else if (Failed || stateTime > 12f) Enter(State.Observe);
                    break;
                case State.Generate:
                    Motor.LookAt(g.Position(plannedNode));
                    if (!emitted && stateTime >= callDelay) { Emit(plannedNode, plannedType); emitted = true; }
                    if (stateTime >= callDuration)
                    {
                        cooldownUntil = Time.time + Random.Range(cooldownRange.x, cooldownRange.y);
                        Enter(State.Cooldown);
                        Motor.MoveTo(HidingSpotAwayFrom(playerEst), AgentMotor.Gait.Sneak);
                    }
                    break;
                case State.Cooldown:
                    if (Time.time >= cooldownUntil) Enter(State.Observe);
                    break;
                case State.Flee:
                    if (Arrived || Failed || stateTime > 8f)
                    {
                        cooldownUntil = Mathf.Max(cooldownUntil, Time.time + cooldownRange.x);
                        Enter(State.Cooldown);
                    }
                    break;
            }
        }

        // ---------- decision (guide §11.4) ----------
        bool SelectDeception()
        {
            var g = NavGraph.Instance;
            float playerAge = Time.time - playerEstTime;
            if (playerAge > 40f) return false;                                       // no idea where the player is

            AmbusherBrain ambusher = null;
            foreach (var a in AgentRegistry.All) if (a is AmbusherBrain ab && ab.IsAlive) ambusher = ab;

            plannedUtility = 0.15f; plannedAction = Action.None;
            float risk = Vector3.Distance(transform.position, playerEst) < 8f ? 0.6f : 0f;   // don't reveal itself
            SoundType mimicry = PreferMimicry(SoundType.MetalImpact);
            for (int n = 0; n < g.Count; n++)
            {
                Vector3 np = g.Position(n);
                float travel = Mathf.Max(0f, Vector3.Distance(transform.position, np) - voiceRange);

                // SUMMON: fake near the player's estimate to pull hunters in. The player's own last sound is preferred
                // (small bonus); louder types are the fallback when it would not carry to any hunter.
                if (playerAge < 15f && Vector3.Distance(np, playerEst) < 3f + playerEstErr)
                    foreach (var k in SummonTypes)
                        Consider(Action.Summon, n, k, SummonGain(np, k) + (k == mimicry ? 0.05f : 0f), risk, travel);

                // FLUSH: player silent (hiding) -> a door slam on the far side scares them towards the ambush
                if (playerAge > 12f && ambusher != null && ambusher.HasAmbushPoint)
                {
                    Vector3 away = playerEst - np; away.y = 0f;
                    Vector3 toAmbush = ambusher.AmbushPoint - playerEst; toAmbush.y = 0f;
                    float dist = Vector3.Distance(np, playerEst);
                    if (dist > 4f && dist < 12f && away.sqrMagnitude > 0.01f && toAmbush.sqrMagnitude > 0.01f)
                    {
                        float align = Vector3.Dot(away.normalized, toAmbush.normalized);
                        Consider(Action.Flush, n, SoundType.Door, Mathf.Max(0f, align) * (ambusher.IsWaiting ? 1f : 0.4f), risk, travel);
                    }
                }
            }
            if (plannedAction != Action.None && logDecisions)
                Debug.Log($"[Mimic] t={Time.time:0.0} {plannedAction} with {plannedType} at node {plannedNode} " +
                          $"({g.Node(plannedNode).zone}), U={plannedUtility:0.00}, player est {playerAge:0}s old ±{playerEstErr:0.0} m", this);
            return plannedAction != Action.None;
        }

        void Consider(Action a, int node, SoundType type, float gain, float risk, float travel)
        {
            float rep = (type == lastFakeType ? 0.2f : 0f) + (node == lastFakeNode && Time.time - lastFakeTime < 30f ? 0.3f : 0f);
            float u = gain - risk - rep - 0.02f * travel;
            if (u > plannedUtility) { plannedUtility = u; plannedAction = a; plannedNode = node; plannedType = type; }
        }

        /// Forward model: simulate each hunter's own reaction rule (the Listener's public priority + hysteresis).
        /// Fix vs the guide: sigma(8 x drive) is 0.5 at drive 0, i.e. it predicted a 50 % reaction to sounds the hunter
        /// cannot even hear, and a calm Listener only walks over for priority >= InvestigateCutoff. Below the hunter's
        /// hearing threshold it is 0; above it a steep curve around "makes it move / beats its current interest".
        float SummonGain(Vector3 at, SoundType type)
        {
            float intensity = IntensityOf(type), gain = 0f;
            foreach (var h in AgentRegistry.All)
            {
                if (h == this || h is MimicBrain || !h.IsAlive) continue;
                var ear = h.GetComponent<Hearing>();
                float loud = Acoustics.Loudness(intensity, ear.EffectiveSensitivity,
                                                Acoustics.EffectiveDistance(at, h.transform.position + Vector3.up * ear.earHeight));
                if (loud < ear.threshold) continue;
                float bar = Mathf.Max(ListenerPriority.InvestigateCutoff, ListenerPriority.SwitchFactor * h.CurrentInterest);
                float drive = loud * ListenerPriority.TypeWeight(type) - bar;   // must make it GO, not just turn its head
                float reacts = 1f / (1f + Mathf.Exp(-40f * drive));
                float improvement = Vector3.Distance(h.transform.position, playerEst) - Vector3.Distance(at, playerEst);
                gain += reacts * Mathf.Max(0f, improvement) / 20f;
            }
            return gain;
        }

        static readonly SoundType[] SummonTypes =
            { SoundType.ObjectDrop, SoundType.Door, SoundType.MetalImpact, SoundType.GlassBreak };

        /// The creepy part: the player hears their own last noise again, somewhere else.
        SoundType PreferMimicry(SoundType fallback) =>
            playerLastType is SoundType.Door or SoundType.MetalImpact or SoundType.ObjectDrop or SoundType.GlassBreak
                ? playerLastType : fallback;

        static float IntensityOf(SoundType t) => t switch
        {
            SoundType.GlassBreak => 0.8f, SoundType.MetalImpact => 0.6f, SoundType.Door => 0.55f,
            SoundType.ObjectDrop => 0.5f, SoundType.Run => 0.3f, SoundType.Footstep => 0.15f,
            SoundType.AgentVoice => 0.5f, _ => 0.4f
        };

        // ---------- action ----------
        void BeginRelocateOrGenerate()
        {
            if (InVoiceRange(plannedNode)) { BeginGenerate(); return; }
            Enter(State.Relocate);
            Vector3 target = NavGraph.Instance.Position(plannedNode);
            Vector3 standOff = target + (transform.position - target).normalized * (voiceRange * 0.7f);
            if (!Motor.MoveTo(standOff, AgentMotor.Gait.Sneak)) Motor.MoveTo(target, AgentMotor.Gait.Sneak);
        }

        void BeginGenerate()
        {
            Motor.Stop();
            Enter(State.Generate);
            emitted = false;
            LastAction = plannedAction;
            if (Anim) Anim.Alert();          // Mimic's Alert state plays the "Call" clip (inhale, jaw opens, throat trembles)
        }

        bool InVoiceRange(int node) => node >= 0 && Vector3.Distance(transform.position, NavGraph.Instance.Position(node)) <= voiceRange;

        void Emit(int node, SoundType type)
        {
            Vector3 p = NavGraph.Instance.Position(node);
            // The fake is REAL audio: the player hears it too.
            Noise.Make(type, p, IntensityOf(type), SoundSource.Mimic, gameObject);
            lastFakeType = type; lastFakeNode = node; lastFakeTime = Time.time;
            if (logDecisions) Debug.Log($"[Mimic] t={Time.time:0.0} fake {type} at {p}", this);
        }

        void StartCover(string why)
        {
            if (state == State.Flee || NavGraph.Instance == null || NavGraph.Instance.Count == 0) return;
            Vector3 hide = HidingSpotAwayFrom(playerEst);
            Enter(State.Flee);
            LastAction = Action.Cover;
            Motor.MoveTo(hide, AgentMotor.Gait.Run);
            Vector3 away = transform.position - hide; away.y = 0f;
            if (away.sqrMagnitude < 0.01f) away = -transform.forward;
            StartCoroutine(FakeTrail(transform.position, transform.position + away.normalized * 12f));
            if (logDecisions) Debug.Log($"[Mimic] t={Time.time:0.0} Cover ({why}): flees to {hide}, fake run trail the other way", this);
        }

        IEnumerator FakeTrail(Vector3 from, Vector3 to)
        {
            for (int i = 1; i <= 4; i++)
            {
                Vector3 p = Vector3.Lerp(from, to, i / 4f);
                if (UnityEngine.AI.NavMesh.SamplePosition(p, out var hit, 3f, UnityEngine.AI.NavMesh.AllAreas)) p = hit.position;
                Noise.Make(SoundType.Run, p, 0.3f, SoundSource.Mimic, gameObject);
                yield return new WaitForSeconds(0.5f);
            }
        }

        Vector3 HidingSpotAwayFrom(Vector3 threat)
        {
            var g = NavGraph.Instance; int best = 0; float bestScore = float.MinValue;
            for (int n = 0; n < g.Count; n++)
            {
                float d = Vector3.Distance(g.Position(n), threat);
                float near = Vector3.Distance(g.Position(n), transform.position);
                float s = Mathf.Min(d, 30f) - 0.3f * near + ((g.Node(n).tags & NodeTag.Room) != 0 ? 3f : 0f);
                if (s > bestScore) { bestScore = s; best = n; }
            }
            return g.Position(best);
        }

        public override void OnHurt(Vector3 from)
        {
            playerEst = from; playerEstTime = Time.time; playerEstErr = 1f;
            StartCover("got hurt");
        }

        /// Cornered: swipe, then bolt once the swing is done (fleeing during the windup would drag the claws away).
        public override void OnContact(Vector3 where)
        {
            Body.TryAttack(where);
            StartCoroutine(FleeAfter(Body.windup + 0.4f));
        }

        IEnumerator FleeAfter(float seconds)
        {
            yield return new WaitForSeconds(seconds);
            if (IsAlive) StartCover("touched");
        }

        public override void ResetAgent()
        {
            base.ResetAgent();
            StopAllCoroutines();
            playerEstTime = -999f; cooldownUntil = 0f; lastFakeNode = -1;
            Enter(State.Observe);
        }

        void Enter(State s)
        {
            state = s; stateTime = 0f;
            if (Anim) Anim.SetListening(s is State.Observe or State.Cooldown && !Motor.IsMoving);
        }
    }
}
