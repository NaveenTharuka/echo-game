using System.Collections;
using System.Collections.Generic;
using Echo.Interaction;
using Echo.Navigation;
using Echo.Player;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;

namespace Echo.DevTools
{
    /// Player checks of the sandbox self-test: movement over steps and through a door frame (CharacterController),
    /// footsteps / sprint / jump / landing noises (the real FirstPersonController driven through its input fields),
    /// the hitscan weapon, and one-hit death. Death is the very last check: it reloads the scene, so its result and
    /// the run summary are logged from a static sceneLoaded handler after the reload.
    public partial class InteractionSelfTest
    {
        [Header("Player (wired by ECHO > Sandbox > Build Player Test Area)")]
        public Transform playerArea;

        static int pendingPassed, pendingFailed;
        static float killedAt, reloadFade;
        static string reloadPath;

        IEnumerator PlayerChecks()
        {
            var noise = interactor.GetComponent<PlayerNoise>();
            var weapon = interactor.GetComponent<HitscanWeapon>();
            if (playerArea == null || noise == null || weapon == null)
            {
                Check("player test area present", false, "run ECHO > Sandbox > Build Player Test Area");
                yield break;
            }
            var cc = interactor.GetComponent<CharacterController>();
            var records = new List<NoiseRecord>();
            void Record(NoiseRecord r) { if (r.Source == NoiseSource.Player || r.Type == NoiseType.BulletImpact) records.Add(r); }
            InteractionNoise.Emitted += Record;

            // ---------------------------------------------------------------- movement (CharacterController)
            Transform stairs = playerArea.Find("StairsStart");
            Collider platform = playerArea.Find("Platform").GetComponent<Collider>();
            Aim(stairs.position + Vector3.up * 0.05f, stairs.position + Vector3.right * 3f + eyeOffset);
            yield return rig.Walk(Vector3.right, 5f, 2f);
            float topY = platform.bounds.max.y;
            Check("climbs stairs (0.18 m risers) to the 0.9 m platform - CharacterController step offset 0.25 m",
                  Mathf.Abs(rig.Player.position.y - topY) < 0.1f && cc.isGrounded,
                  $"feetY={rig.Player.position.y:F2} platformTop={topY:F2} grounded={cc.isGrounded}");

            Transform ledgeStart = playerArea.Find("LedgeStart");
            Collider ledge = playerArea.Find("Ledge_35cm").GetComponent<Collider>();
            Aim(ledgeStart.position + Vector3.up * 0.05f, ledgeStart.position + Vector3.right * 3f + eyeOffset);
            yield return rig.Walk(Vector3.right, 3f, 2f);
            float gap = ledge.bounds.min.x - rig.Player.position.x;
            Check("a 0.35 m ledge blocks the player (higher than the step offset)",
                  rig.Player.position.y < 0.1f && gap > cc.radius - 0.05f,
                  $"feetY={rig.Player.position.y:F2} capsuleCentreToLedge={gap:F2} (radius {cc.radius:F2})");

            if (normalDoor != null)
            {
                normalDoor.SetOpen(true, gameObject, false);
                yield return WaitFor(() => normalDoor.CurrentState == Door.State.Open, normalDoor.openDuration + 0.5f);
                Vector3 front = Approach(normalDoor, 1.6f);
                Aim(front, normalDoor.DoorwayCenter);
                yield return rig.Walk(normalDoor.DoorwayCenter - front, 3.2f, 2f);
                float past = -Vector3.Dot(rig.Player.position - normalDoor.DoorwayCenter, normalDoor.transform.right);
                Check("walks through an open door frame without snagging", past > 1.2f, $"metresPastDoorway={past:F2}");
                Aim(front, normalDoor.DoorwayCenter);
                normalDoor.SetOpen(false, gameObject, false);
            }

            // ---------------------------------------------------------------- body noises (real controller)
            Vector3 lane = new(-4.5f, origin.y, -16f);   // 20 m clear lane along +Z, west of the props
            Aim(lane, lane + Vector3.forward * 5f + eyeOffset);
            records.Clear();
            rig.Drive(Vector2.up, false);
            yield return new WaitForSeconds(1.5f);
            Steps(records, NoiseType.Footstep, out int walkSteps, out float walkInterval, out float walkI);
            bool walkHadRun = records.Exists(r => r.Type == NoiseType.Run);
            records.Clear();
            rig.Drive(Vector2.up, true);
            yield return new WaitForSeconds(1.5f);
            Steps(records, NoiseType.Run, out int runSteps, out float runInterval, out float runI);
            rig.Drive(Vector2.zero, false);
            yield return new WaitForSeconds(0.5f);
            Check("walking emits Footstep 0.15 every ~0.4 s; sprinting emits louder Run steps (Player source)",
                  walkSteps >= 2 && !walkHadRun && walkInterval > 0.3f && walkInterval < 0.6f &&
                  runSteps >= 2 && runInterval > 0.25f && runInterval < 0.5f && runI > walkI,
                  $"walk: {walkSteps} steps every {walkInterval:F2} s at {walkI:F2} (run during walk={walkHadRun}) | " +
                  $"sprint: {runSteps} steps every {runInterval:F2} s at {runI:F2}");

            records.Clear();
            rig.Drive(Vector2.zero, false, jump: true);
            yield return WaitFor(() => records.Exists(r => r.Type == NoiseType.Land), 2f);
            NoiseRecord jump = records.Find(r => r.Type == NoiseType.Jump), land = records.Find(r => r.Type == NoiseType.Land);
            records.Clear();
            Vector3 high = rig.Player.position + Vector3.up * 4f;   // a 4 m drop, still the real controller's gravity
            Aim(high, high + Vector3.forward * 5f + eyeOffset);
            yield return WaitFor(() => records.Exists(r => r.Type == NoiseType.Land), 3f);
            NoiseRecord drop = records.Find(r => r.Type == NoiseType.Land);
            rig.Freeze();
            Check("jump and landing emit noises; landing scales with fall speed (4 m drop louder than a jump)",
                  jump.Type == NoiseType.Jump && land.Type == NoiseType.Land && drop.Type == NoiseType.Land &&
                  land.Intensity >= noise.minLandIntensity && drop.Intensity > land.Intensity,
                  $"jump={jump.Type == NoiseType.Jump}@{jump.Intensity:F2} land={land.Intensity:F2} drop={drop.Intensity:F2} " +
                  $"(fall speed {noise.LastLandSpeed:F1} m/s)");

            // ---------------------------------------------------------------- weapon
            yield return new WaitForSeconds(weapon.fireInterval);
            Vector3 brickAt = brick.Body.worldCenterOfMass;
            Vector3 shooter = brickAt + Vector3.forward * 3f;
            shooter.y = origin.y;
            Aim(shooter, brickAt);
            records.Clear();
            bool fired = weapon.TryFire();
            yield return new WaitForFixedUpdate();
            yield return new WaitForFixedUpdate();
            float pushed = brick.Body.linearVelocity.magnitude;
            NoiseRecord gun = records.Find(r => r.Type == NoiseType.Gunshot), hitNoise = records.Find(r => r.Type == NoiseType.BulletImpact);
            Check("a shot emits Gunshot 1.0 (Player) + BulletImpact at the hit, and pushes the prop it hits",
                  fired && gun.Type == NoiseType.Gunshot && Mathf.Approximately(gun.Intensity, 1f) && gun.Source == NoiseSource.Player &&
                  hitNoise.Type == NoiseType.BulletImpact && Vector3.Distance(hitNoise.Position, brickAt) < 0.5f && pushed > 0.3f,
                  $"fired={fired} gunshot={gun.Type == NoiseType.Gunshot}@{gun.Intensity:F2} impact={hitNoise.Type == NoiseType.BulletImpact} " +
                  $"impactToBrick={Vector3.Distance(hitNoise.Position, brickAt):F2} brickSpeed={pushed:F2} " +
                  $"(free-body {weapon.impactImpulse / brick.Body.mass:F1} m/s; resting on the floor and hit ~24 deg downward, part goes into the floor and friction)");

            // A shot calls IDamageable.TakeDamage on what it hits (agents), with the camera position as `from`.
            yield return new WaitForSeconds(weapon.fireInterval + 0.05f);
            Aim(shooter, shooter + Vector3.forward * 5f + eyeOffset);   // level, facing away from the brick
            Vector3 eye = interactor.viewPoint.position;
            var targetGo = GameObject.CreatePrimitive(PrimitiveType.Cube);
            targetGo.name = "_DamageTarget (runtime)";
            targetGo.transform.position = eye + interactor.viewPoint.forward * 3f;
            var target = targetGo.AddComponent<DamageProbe>();
            Physics.SyncTransforms();
            bool firedAtTarget = weapon.TryFire();
            Check("a shot calls IDamageable.TakeDamage(damage, camera position) on the target",
                  firedAtTarget && target.Hits == 1 && Mathf.Approximately(target.LastAmount, weapon.damage) && Vector3.Distance(target.LastFrom, eye) < 0.01f,
                  $"fired={firedAtTarget} hits={target.Hits} amount={target.LastAmount} fromError={Vector3.Distance(target.LastFrom, eye):F3}");
            Destroy(targetGo);

            yield return new WaitForSeconds(weapon.fireInterval + 0.05f);
            Aim(StandBy(basin, Vector3.forward), basin.Body.worldCenterOfMass);
            interactor.TryInteract();
            bool holding = carrier.Held == basin;
            records.Clear();
            bool firedHolding = weapon.TryFire();
            bool silent = !records.Exists(r => r.Type == NoiseType.Gunshot);
            carrier.Drop();
            bool control = weapon.TryFire();
            Check("no firing while carrying (control: fires once the prop is dropped)",
                  holding && !firedHolding && silent && control,
                  $"holding={holding} firedWhileHolding={firedHolding} control={control}");

            InteractionNoise.Emitted -= Record;
            yield return new WaitForSeconds(1f);   // let the basin land
        }

        static void Steps(List<NoiseRecord> records, NoiseType type, out int count, out float meanInterval, out float intensity)
        {
            count = 0;
            float first = 0f, last = 0f;
            intensity = 0f;
            foreach (NoiseRecord r in records)
            {
                if (r.Type != type) continue;
                if (count == 0) first = r.Time;
                last = r.Time;
                intensity = r.Intensity;
                count++;
            }
            meanInterval = count > 1 ? (last - first) / (count - 1) : 0f;
        }

        // ------------------------------------------------------------------------------------ death
        /// Runs the pre-reload death checks and kills the player. Returns false if there is no PlayerDeath.
        bool StartDeathCheck()
        {
            var death = interactor.GetComponent<PlayerDeath>();
            if (death == null) return false;

            rig.Look(basin.Body.worldCenterOfMass);
            interactor.TryInteract();
            bool holding = carrier.Held == basin;

#if UNITY_EDITOR
            death.SetInvulnerable(true);
            bool blocked = !death.Kill(gameObject) && !death.IsDead && carrier.Held == basin;
            death.SetInvulnerable(false);
            Check("F9 invulnerability blocks Kill in the editor", holding && blocked,
                  $"holding={holding} isDead={death.IsDead} stillHolding={carrier.Held == basin}");
#endif

            bool killed = death.Kill(gameObject);
            var playerInput = interactor.GetComponent<PlayerInput>();
            bool dropped = carrier.Held == null && basin.Body.useGravity;
            bool controlOff = !interactor.enabled && !carrier.enabled && (playerInput == null || !playerInput.enabled);
            Check("Kill drops the held object and disables control", holding && killed && death.IsDead && dropped && controlOff,
                  $"killed={killed} dropped={dropped} controlOff={controlOff}");

            pendingPassed = passed;
            pendingFailed = failed;
            killedAt = Time.realtimeSinceStartup;
            reloadFade = death.fadeSeconds;
            reloadPath = SceneManager.GetActiveScene().path;
            SceneManager.sceneLoaded -= OnReloadedAfterKill;
            SceneManager.sceneLoaded += OnReloadedAfterKill;
            Debug.Log($"[SelfTest] Kill called: the scene should reload after the {reloadFade:F1} s fade...");
            return true;
        }

        static void OnReloadedAfterKill(Scene scene, LoadSceneMode mode)
        {
            SceneManager.sceneLoaded -= OnReloadedAfterKill;
            float t = Time.realtimeSinceStartup - killedAt;
            bool ok = scene.path == reloadPath && t >= reloadFade * 0.9f && t < reloadFade + 5f;
            string name = $"Kill fades to black and reloads the active scene  | reloaded '{scene.name}' after {t:F2} s (fade {reloadFade:F1} s)";
            if (ok) { pendingPassed++; Debug.Log("[SelfTest] PASS " + name); }
            else { pendingFailed++; Debug.LogError("[SelfTest] FAIL " + name); }
            LogSummary(pendingPassed, pendingFailed);
        }
    }
}
