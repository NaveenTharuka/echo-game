using System;
using System.Collections;
using Echo.Interaction;
using Echo.Navigation;
using Echo.UI;
using UnityEngine;

namespace Echo.DevTools
{
    /// Scripted Play Mode checks for the interaction core and doors, run in Sandbox_Interaction.
    /// Stands in for PlayMode tests while the project uses the default assembly.
    /// Run: enter Play Mode, then this component's context menu (⋮) > Run Self-Test,
    /// or tick Run On Play. Each check logs PASS, or an error on FAIL (with the observed values).
    ///
    /// Deterministic: player move/look is disabled for the run, and before every check the player is
    /// placed and the camera aimed at that check's target. Negative checks include a control (the same
    /// target IS focused once the blocking condition is removed), so they cannot pass vacuously.
    /// Everything is restored afterwards, so the sandbox stays playable and the test can be re-run.
    public class InteractionSelfTest : MonoBehaviour
    {
        [Tooltip("Run automatically when entering Play Mode. Off so the sandbox is playable by hand.")]
        public bool runOnPlay;
        public PlayerInteractor interactor;
        public InteractionPromptUI prompt;
        public InteractionProbe frontProbe, farProbe, occludedProbe, wrongLayerProbe;

        [Header("Doors (wired by ECHO > Sandbox > Build Door Test Area)")]
        public Door normalDoor;
        public Door lockedDoor;
        public SwitchInteractable unlockSwitch;

        [Header("Throwables (wired by ECHO > Sandbox > Build Throwable Test Area)")]
        public PhysicsCarrier carrier;
        public Carryable brick, basin, heavyCrate;
        public Collider thinWall;

        int passed, failed;
        bool running;
        NoiseRecord lastNoise;
        int noiseCount;

        PlayerTestRig rig;
        Transform player;
        Vector3 origin, eyeOffset;

        // The layout below is relative to the player's spawn, so record it before anything (input
        // that keeps running in the background, a manual walk) can move the player.
        Vector3 spawn;

        void Awake()
        {
            if (interactor != null) spawn = interactor.transform.position;
        }

        void Start()
        {
            if (runOnPlay) StartCoroutine(Run());
        }

        /// Also reachable from ECHO > Sandbox > Run Self-Test.
        [ContextMenu("Run Self-Test")]
        public void RunSelfTest()
        {
            if (!Application.isPlaying) { Debug.LogWarning("[SelfTest] Enter Play Mode first, then Run Self-Test.", this); return; }
            if (running) { Debug.LogWarning("[SelfTest] Already running.", this); return; }
            StartCoroutine(Run());
        }

        IEnumerator Run()
        {
            running = true;
            passed = failed = 0;
            InteractionNoise.Emitted += OnNoise;
            yield return null;

            FreezePlayer();
            yield return CoreChecks();
            yield return DoorChecks();
            yield return ThrowableChecks();
            RestorePlayer();

            InteractionNoise.Emitted -= OnNoise;
            running = false;
            string summary = $"[SelfTest] {passed} passed, {failed} failed. Sound core bound: {InteractionNoise.SoundCoreBound}";
            if (failed == 0) Debug.Log(summary, this); else Debug.LogError(summary, this);
        }

        // ======================================================================== interaction core
        IEnumerator CoreChecks()
        {
            InteractionNoise.MuteFor(0.25f);
            Check("settle mute suppresses noise",
                  !InteractionNoise.Emit(NoiseType.Switch, transform.position, 0.3f, NoiseSource.Environment, gameObject));
            while (InteractionNoise.IsMuted) yield return null;

            Aim(origin, frontProbe.transform.position);
            Check("focuses the probe in range", ReferenceEquals(interactor.Focus, frontProbe), FocusInfo());
            yield return null;   // let the prompt UI update
            Check("prompt shows key hint and verb", prompt != null &&
                  prompt.CurrentText.Contains(frontProbe.verb) && prompt.CurrentText.Contains(interactor.InteractKeyHint),
                  $"prompt='{(prompt != null ? prompt.CurrentText : "<none>")}'");

            int count = frontProbe.InteractCount, noises = noiseCount;
            bool used = interactor.TryInteract();
            Check("TryInteract triggers the focused object", used && frontProbe.InteractCount == count + 1,
                  $"used={used} countDelta={frontProbe.InteractCount - count}");
            Check("interaction emits a Switch noise from the probe",
                  noiseCount == noises + 1 && lastNoise.Type == NoiseType.Switch && lastNoise.Emitter == frontProbe.gameObject,
                  NoiseInfo(noises) + $" probeAccepted={frontProbe.LastNoiseAccepted}");

            count = frontProbe.InteractCount; noises = noiseCount;
            used = interactor.TryInteract();
            Check("per-object cooldown suppresses the repeat noise",
                  used && frontProbe.InteractCount == count + 1 && !frontProbe.LastNoiseAccepted && noiseCount == noises,
                  $"used={used} countDelta={frontProbe.InteractCount - count} " + NoiseInfo(noises));

            frontProbe.interactable = false;
            Aim(origin, frontProbe.transform.position);
            bool cleared = interactor.Focus == null && !interactor.TryInteract();
            frontProbe.interactable = true;
            Aim(origin, frontProbe.transform.position);
            Check("CanInteract=false clears focus", cleared && ReferenceEquals(interactor.Focus, frontProbe), FocusInfo());

            Vector3 toFar = farProbe.transform.position - origin; toFar.y = 0f;
            Vector3 near = farProbe.transform.position - toFar.normalized * (interactor.range * 0.6f);
            near.y = origin.y;
            Aim(near, farProbe.transform.position);
            bool controlFar = ReferenceEquals(interactor.Focus, farProbe);
            Aim(origin, farProbe.transform.position);
            Check("ignores a probe beyond range", controlFar && interactor.Focus == null, $"control={controlFar} " + FocusInfo());

            Aim(origin, occludedProbe.transform.position);
            bool blockedFocus = interactor.Focus != null;
            Collider wall = FirstBlocker(occludedProbe.transform);
            bool controlWall = false;
            if (wall != null)
            {
                wall.enabled = false;
                Aim(origin, occludedProbe.transform.position);
                controlWall = ReferenceEquals(interactor.Focus, occludedProbe);
                wall.enabled = true;
            }
            Aim(origin, occludedProbe.transform.position);
            Check("ignores a probe behind a wall", wall != null && controlWall && !blockedFocus && interactor.Focus == null,
                  $"wall={(wall != null ? wall.name : "<none>")} control={controlWall} " + FocusInfo());

            LayerMask mask = interactor.interactableMask;
            interactor.interactableMask = mask | (1 << wrongLayerProbe.gameObject.layer);
            Aim(origin, wrongLayerProbe.transform.position);
            bool controlLayer = ReferenceEquals(interactor.Focus, wrongLayerProbe);
            interactor.interactableMask = mask;
            Aim(origin, wrongLayerProbe.transform.position);
            Check("ignores a probe on a non-interactable layer", controlLayer && interactor.Focus == null,
                  $"control={controlLayer} " + FocusInfo());

            yield return null;
            Check("prompt hides without focus", prompt != null && interactor.Focus == null && prompt.CurrentText.Length == 0,
                  $"prompt='{(prompt != null ? prompt.CurrentText : "<none>")}'");

            Aim(origin, frontProbe.transform.position);
            Check("re-acquires focus", ReferenceEquals(interactor.Focus, frontProbe), FocusInfo());
        }

        // =================================================================================== doors
        IEnumerator DoorChecks()
        {
            if (normalDoor == null || lockedDoor == null || unlockSwitch == null)
            {
                Check("door test area present", false, "run ECHO > Sandbox > Build Door Test Area");
                yield break;
            }
            float settle = normalDoor.openDuration + 0.5f;

            // 1. opens
            Vector3 front = Approach(normalDoor, 1.6f);
            Aim(front, normalDoor.DoorwayCenter);
            int noises = noiseCount;
            bool focused = ReferenceEquals(interactor.Focus, normalDoor);
            bool used = interactor.TryInteract();
            yield return WaitFor(() => normalDoor.CurrentState == Door.State.Open, settle);
            bool noiseOk = noiseCount == noises + 1 && lastNoise.Type == NoiseType.Door && Mathf.Approximately(lastNoise.Intensity, normalDoor.openIntensity);
            Check("door opens (focus, Door 0.5 noise, slides fully)",
                  focused && used && normalDoor.CurrentState == Door.State.Open && normalDoor.isOpen && noiseOk && LeafTravel(normalDoor) > normalDoor.SlideDistance * 0.95f,
                  $"focused={focused} used={used} state={normalDoor.CurrentState} travel={LeafTravel(normalDoor):F2}/{normalDoor.SlideDistance:F2} " + NoiseInfo(noises));

            // 2. closes (aimed at the open doorway: the use volume keeps it targetable)
            Aim(front, normalDoor.DoorwayCenter);
            noises = noiseCount;
            focused = ReferenceEquals(interactor.Focus, normalDoor);
            string verb = normalDoor.GetPrompt(interactor);
            used = interactor.TryInteract();
            yield return WaitFor(() => normalDoor.CurrentState == Door.State.Closed, settle + normalDoor.retryInterval);
            noiseOk = noiseCount == noises + 1 && lastNoise.Type == NoiseType.Door && Mathf.Approximately(lastNoise.Intensity, normalDoor.closeIntensity);
            Check("door closes (open doorway targetable, Door 0.6 noise)",
                  focused && verb == "Close" && used && normalDoor.CurrentState == Door.State.Closed && !normalDoor.isOpen && noiseOk && LeafTravel(normalDoor) < 0.01f,
                  $"focused={focused} verb={verb} used={used} state={normalDoor.CurrentState} travel={LeafTravel(normalDoor):F2} " + NoiseInfo(noises));

            // 3. locked stays shut
            Vector3 lockedFront = Approach(lockedDoor, 1.6f);
            Aim(lockedFront, lockedDoor.DoorwayCenter);
            noises = noiseCount;
            focused = ReferenceEquals(interactor.Focus, lockedDoor);
            verb = lockedDoor.GetPrompt(interactor);
            used = interactor.TryInteract();
            lockedDoor.SetOpen(true, gameObject, true);   // an agent trying it too
            yield return new WaitForSeconds(settle);
            noiseOk = noiseCount == noises + 1 && lastNoise.Type == NoiseType.Door && Mathf.Approximately(lastNoise.Intensity, lockedDoor.rattleIntensity);
            Check("locked door stays shut (prompt Locked, rattle 0.2, agents can't open)",
                  focused && verb == "Locked" && used && lockedDoor.isLocked && !lockedDoor.isOpen && LeafTravel(lockedDoor) < 0.01f && noiseOk,
                  $"focused={focused} verb={verb} state={lockedDoor.CurrentState} travel={LeafTravel(lockedDoor):F2} " + NoiseInfo(noises));

            // 4. switch unlocks it (through its UnityEvent), then it opens
            Aim(SwitchApproach(), unlockSwitch.GetComponentInChildren<Collider>().bounds.center);
            focused = ReferenceEquals(interactor.Focus, unlockSwitch);
            used = interactor.TryInteract();
            bool unlocked = !lockedDoor.isLocked && lockedDoor.CurrentState == Door.State.Closed;
            Aim(lockedFront, lockedDoor.DoorwayCenter);
            bool usedDoor = interactor.TryInteract();
            yield return WaitFor(() => lockedDoor.CurrentState == Door.State.Open, settle);
            Check("switch unlocks the door, which then opens",
                  focused && used && unlockSwitch.Activated && unlocked && usedDoor && lockedDoor.CurrentState == Door.State.Open,
                  $"switchFocused={focused} used={used} unlocked={unlocked} usedDoor={usedDoor} state={lockedDoor.CurrentState}");

            // 5. won't close on the player
            normalDoor.SetOpen(true, gameObject, false);
            yield return WaitFor(() => normalDoor.CurrentState == Door.State.Open, settle);
            Vector3 inDoorway = normalDoor.DoorwayCenter; inDoorway.y = origin.y;
            Aim(inDoorway, inDoorway + normalDoor.transform.forward + Vector3.up * eyeOffset.y);
            normalDoor.SetOpen(false, interactor.gameObject, false);
            yield return new WaitForSeconds(normalDoor.retryInterval * 3f + 0.1f);
            bool heldOpen = normalDoor.CurrentState == Door.State.Open && normalDoor.IsCloseBlocked && LeafTravel(normalDoor) > normalDoor.SlideDistance * 0.95f;
            Aim(front, normalDoor.DoorwayCenter);   // step out of the doorway
            yield return WaitFor(() => normalDoor.CurrentState == Door.State.Closed, settle + normalDoor.retryInterval * 2f);
            Check("door won't close on the player, closes once clear",
                  heldOpen && normalDoor.CurrentState == Door.State.Closed && !normalDoor.IsCloseBlocked,
                  $"heldOpen={heldOpen} state={normalDoor.CurrentState} blocked={normalDoor.IsCloseBlocked}");

            // restore: close + relock the locked door, reset its switch
            lockedDoor.SetOpen(false, gameObject, false);
            yield return WaitFor(() => lockedDoor.CurrentState == Door.State.Closed, settle + lockedDoor.retryInterval);
            lockedDoor.Lock();
            unlockSwitch.ResetSwitch();
        }

        // ============================================================================== throwables
        IEnumerator ThrowableChecks()
        {
            if (carrier == null || brick == null || basin == null || heavyCrate == null || thinWall == null)
            {
                Check("throwable test area present", false, "run ECHO > Sandbox > Build Throwable Test Area");
                yield break;
            }
            var cc = interactor.GetComponent<CharacterController>();
            Carryable[] props = { brick, basin, heavyCrate };
            var start = new Pose[props.Length];
            for (int i = 0; i < props.Length; i++) start[i] = new Pose(props[i].Body.position, props[i].Body.rotation);

            // Wall plane, normal pointing at the props' side.
            Vector3 wallCentre = thinWall.bounds.center, wallN = thinWall.transform.forward;
            if (Vector3.Dot(brick.Body.position - wallCentre, wallN) < 0f) wallN = -wallN;
            float WallSide(Vector3 p) => Vector3.Dot(p - wallCentre, wallN);

            // 1. pick up
            Vector3 stand = StandBy(brick, wallN);
            Aim(stand, brick.Body.worldCenterOfMass);
            bool focused = ReferenceEquals(interactor.Focus, brick);
            string verb = brick.GetPrompt(interactor);
            bool used = interactor.TryInteract();
            bool ignoresPlayer = Physics.GetIgnoreCollision(brick.Colliders[0], cc);
            Check("picks up the 2 kg brick (prompt Pick up, gravity off, interpolated, ignores the player's capsule)",
                  focused && verb == "Pick up" && used && carrier.Held == brick && !brick.Body.useGravity &&
                  brick.CurrentPhase == Carryable.Phase.Held && brick.Body.interpolation == RigidbodyInterpolation.Interpolate && ignoresPlayer,
                  $"focused={focused} verb={verb} held={(carrier.Held != null ? carrier.Held.name : "none")} gravity={brick.Body.useGravity} phase={brick.CurrentPhase} interp={brick.Body.interpolation} ignore={ignoresPlayer}");

            // 2. hold follows the camera, also after a one-frame 90 degree turn (plus looking up from the floor). That
            //    flick puts the hold point beyond the 1.5 m drop distance for a moment: the snag grace must keep it held.
            yield return new WaitForSeconds(0.6f);
            float before = Vector3.Distance(carrier.HoldPoint, brick.Body.worldCenterOfMass);
            Vector3 heldAt = brick.Body.position;
            Vector3 side = Quaternion.Euler(0f, 90f, 0f) * -wallN;
            Aim(stand, stand + side * 3f + Vector3.up * (eyeOffset.y + 1.5f));
            float jump = Vector3.Distance(carrier.HoldPoint, brick.Body.worldCenterOfMass);
            yield return new WaitForSeconds(0.6f);
            float after = Vector3.Distance(carrier.HoldPoint, brick.Body.worldCenterOfMass);
            float moved = Vector3.Distance(heldAt, brick.Body.position);
            verb = brick.GetPrompt(interactor);
            Check("held brick follows the camera (< 0.15 m from the hold point; a 90 deg flick past the 1.5 m drop distance doesn't drop it; prompt Drop)",
                  carrier.Held == brick && before < 0.15f && jump > carrier.dropDistance && after < 0.15f && moved > 0.5f &&
                  ReferenceEquals(interactor.Focus, brick) && verb.StartsWith("Drop"),
                  $"offsetBefore={before:F3} flickOffset={jump:F2} offsetAfter={after:F3} moved={moved:F2} held={carrier.Held == brick} verb={verb}");

            // 3. full-charge throw at the 5 cm wall
            Vector3 throwFrom = wallCentre + wallN * 3.5f;
            throwFrom.y = origin.y;
            Aim(throwFrom, new Vector3(wallCentre.x, throwFrom.y + eyeOffset.y, wallCentre.z));
            // The rig teleports the player; carry the prop along as walking there would have.
            brick.Body.position = carrier.HoldPoint;
            yield return new WaitForSeconds(0.6f);
            NoiseRecord firstHit = default;
            bool hit = false;
            void OnBrickNoise(NoiseRecord r) { if (!hit && r.Emitter == brick.gameObject) { hit = true; firstHit = r; } }
            InteractionNoise.Emitted += OnBrickNoise;
            carrier.Throw(1f);
            yield return new WaitForFixedUpdate();
            yield return new WaitForFixedUpdate();
            float launch = brick.Body.linearVelocity.magnitude;
            Carryable.Phase flightPhase = brick.CurrentPhase;
            RigidbodyInterpolation flightInterp = brick.Body.interpolation;
            CollisionDetectionMode flightMode = brick.Body.collisionDetectionMode;
            yield return WaitFor(() => hit, 1.5f);
            CollisionDetectionMode afterHitMode = brick.Body.collisionDetectionMode;
            yield return new WaitForSeconds(1f);
            InteractionNoise.Emitted -= OnBrickNoise;
            Check("full-charge throw: ~12 m/s, ContinuousDynamic + Interpolate in flight, back to Discrete after the first hit",
                  carrier.Held == null && launch > 11f && launch < 13f && flightPhase == Carryable.Phase.Flight &&
                  flightMode == CollisionDetectionMode.ContinuousDynamic && flightInterp == RigidbodyInterpolation.Interpolate &&
                  hit && afterHitMode == CollisionDetectionMode.Discrete,
                  $"launch={launch:F2} phase={flightPhase} mode={flightMode} interp={flightInterp} hit={hit} afterHit={afterHitMode}");
            Check("impact noise through InteractionNoise (Environment, 0.15-0.7, at the wall)",
                  hit && firstHit.Type == brick.impactNoise && firstHit.Source == NoiseSource.Environment &&
                  firstHit.Intensity >= brick.minIntensity && firstHit.Intensity <= brick.maxIntensity && Mathf.Abs(WallSide(firstHit.Position)) < 0.2f,
                  $"hit={hit} type={firstHit.Type} source={firstHit.Source} intensity={firstHit.Intensity:F2} distFromWall={WallSide(firstHit.Position):F2}");
            Check("no tunnelling: the brick stays on the thrower's side of the 5 cm wall",
                  hit && WallSide(brick.Body.position) > 0f, $"signedDistanceFromWall={WallSide(brick.Body.position):F2}");

            // 4. kill plane
            yield return WaitFor(() => brick.Body.IsSleeping(), 2f);
            Vector3 valid = brick.LastValidPosition;
            bool respawned = false;
            void OnRespawn(Carryable _) => respawned = true;
            brick.Respawned += OnRespawn;
            brick.Body.position = new Vector3(valid.x, Carryable.KillPlaneY - 5f, valid.z);
            brick.Body.linearVelocity = Vector3.down * 5f;
            brick.Body.WakeUp();
            yield return new WaitForFixedUpdate();
            yield return new WaitForFixedUpdate();
            brick.Respawned -= OnRespawn;
            Check("kill plane (y < -50) respawns the brick at its last rest position (at rest: Discrete, no interpolation)",
                  respawned && Vector3.Distance(brick.Body.position, valid) < 0.05f && brick.Body.position.y > Carryable.KillPlaneY &&
                  brick.Body.interpolation == RigidbodyInterpolation.None && brick.Body.collisionDetectionMode == CollisionDetectionMode.Discrete,
                  $"respawned={respawned} pos={brick.Body.position} lastValid={valid} interp={brick.Body.interpolation} mode={brick.Body.collisionDetectionMode}");

            // 5. too heavy (control: the same crate is liftable when the limit is raised)
            Aim(StandBy(heavyCrate, wallN), heavyCrate.Body.worldCenterOfMass);
            focused = ReferenceEquals(interactor.Focus, heavyCrate);
            verb = heavyCrate.GetPrompt(interactor);
            interactor.TryInteract();
            float limit = carrier.liftMassLimit;
            carrier.liftMassLimit = heavyCrate.Body.mass + 1f;
            bool control = heavyCrate.GetPrompt(interactor) == "Pick up";
            carrier.liftMassLimit = limit;
            Check("40 kg crate shows Too heavy and is not lifted",
                  focused && verb == "Too heavy" && carrier.Held == null && heavyCrate.Body.mass >= 40f && heavyCrate.Body.useGravity && control,
                  $"focused={focused} verb={verb} mass={heavyCrate.Body.mass} held={(carrier.Held != null ? carrier.Held.name : "none")} control={control}");

            // 6. metal basin: lifts, E drops it, the player collision comes back once clear
            Aim(StandBy(basin, wallN), basin.Body.worldCenterOfMass);
            interactor.TryInteract();
            bool lifted = carrier.Held == basin;
            yield return new WaitForSeconds(0.4f);
            bool dropped = interactor.TryInteract() && carrier.Held == null && basin.Body.useGravity;
            yield return new WaitForSeconds(1.2f);
            bool restored = !Physics.GetIgnoreCollision(basin.Colliders[0], cc);
            Check("3 kg metal basin lifts, E drops it, player collision restored once clear",
                  lifted && dropped && restored, $"lifted={lifted} dropped={dropped} collisionRestored={restored}");

            // restore the props
            carrier.Drop();
            for (int i = 0; i < props.Length; i++)
            {
                Rigidbody rb = props[i].Body;
                rb.linearVelocity = Vector3.zero;
                rb.angularVelocity = Vector3.zero;
                rb.position = start[i].position;
                rb.rotation = start[i].rotation;
                props[i].transform.SetPositionAndRotation(start[i].position, start[i].rotation);
            }
        }

        /// Feet 1.4 m from the prop, on the side away from the wall.
        Vector3 StandBy(Carryable c, Vector3 awayFromWall)
        {
            Vector3 p = c.Body.position + awayFromWall * 1.4f;
            p.y = origin.y;
            return p;
        }

        Vector3 Approach(Door d, float distance)
        {
            // The door's local +X faces the spawn side of the wall.
            Vector3 p = d.DoorwayCenter + d.transform.right * distance;
            p.y = origin.y;
            return p;
        }

        Vector3 SwitchApproach()
        {
            // The switch is mounted flush on a wall; its local +X faces out of the wall.
            Transform s = unlockSwitch.transform;
            Vector3 p = s.position + s.right * 1.4f;
            p.y = origin.y;
            return p;
        }

        // Physics pose, not the interpolated render transform.
        float LeafTravel(Door d) => Vector3.Distance(d.leaf.position, d.ClosedLeafPosition);

        // ================================================================================= helpers
        void OnDestroy() => InteractionNoise.Emitted -= OnNoise;

        void OnNoise(NoiseRecord r) { lastNoise = r; noiseCount++; }

        static IEnumerator WaitFor(Func<bool> done, float timeout)
        {
            float end = Time.time + timeout;
            while (!done() && Time.time < end) yield return null;
        }

        string FocusInfo()
        {
            var o = interactor.Focus as UnityEngine.Object;
            return $"focus={(o != null ? o.name : "none")}";
        }

        string NoiseInfo(int before) =>
            $"noiseDelta={noiseCount - before} last={lastNoise.Type}@{lastNoise.Intensity:F2} " +
            $"from={(lastNoise.Emitter != null ? lastNoise.Emitter.name : "null")} muted={InteractionNoise.IsMuted}";

        /// Stops the FPS controller (no movement, no mouse look) and records the start pose.
        void FreezePlayer()
        {
            rig = new PlayerTestRig(interactor);
            player = rig.Player;
            origin = spawn;
            eyeOffset = rig.EyeOffset;
        }

        void RestorePlayer() => rig.Restore();

        void Aim(Vector3 feet, Vector3 target) => rig.Aim(feet, target);

        /// The first collider between the camera and the target (the wall), or null.
        Collider FirstBlocker(Transform target)
        {
            Vector3 eye = interactor.viewPoint.position;
            Vector3 d = target.position - eye;
            foreach (RaycastHit h in Physics.RaycastAll(eye, d.normalized, d.magnitude, ~0, QueryTriggerInteraction.Ignore))
                if (h.collider.transform != target && !h.collider.transform.IsChildOf(player)) return h.collider;
            return null;
        }

        void Check(string name, bool ok, string details = null)
        {
            if (ok) { passed++; Debug.Log("[SelfTest] PASS " + name, this); }
            else { failed++; Debug.LogError($"[SelfTest] FAIL {name}" + (details != null ? "  | " + details : ""), this); }
        }
    }
}
