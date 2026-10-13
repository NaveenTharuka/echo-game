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
