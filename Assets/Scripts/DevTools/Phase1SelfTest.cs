using System.Collections;
using System.Collections.Generic;
using System.Text;
using Echo.AI;
using Echo.Player;
using Echo.Sound;
using StarterAssets;
using UnityEngine;
using UnityEngine.AI;

namespace Echo.DevTools
{
    /// Scripted Play Mode checks for Phase 1 (stamina, crouch, camera, chase). Drives the real player through its input
    /// fields; logs "[Phase1]" lines. Start in Play Mode:
    ///   new GameObject("Phase1SelfTest").AddComponent<Phase1SelfTest>().Run("body" | "chase" | "crouch")
    /// body   : agents off; sprint in a circle until exhausted, recover, crouch-walk, a 3 m drop.
    /// chase  : a gunshot 11 m from the Listener, then the player sprints away from it (holding Shift is the strategy
    ///          being tested - it should no longer work).
    /// crouch : the same gunshot, then the player crouch-walks away and stops (the counterplay).
    /// Needs ECHO > Dev > Run In Background when Unity is not focused.
    public class Phase1SelfTest : MonoBehaviour
    {
        FirstPersonController fpc;
        StarterAssetsInputs input;
        CharacterController cc;
        PlayerExertion body;
        PlayerCameraMotion camMotion;
        PlayerDeath death;
        Camera cam;
        readonly List<SoundEvent> playerSounds = new();
        int feetSteps;   // footsteps at the feet when emitted (panting comes from the head)
        float feetStepIntensity;

        public void Run(string test)
        {
            fpc = FindFirstObjectByType<FirstPersonController>();
            input = fpc.GetComponent<StarterAssetsInputs>();
            cc = fpc.GetComponent<CharacterController>();
            body = fpc.GetComponent<PlayerExertion>();
            camMotion = fpc.GetComponent<PlayerCameraMotion>();
            death = fpc.GetComponent<PlayerDeath>();
            cam = fpc.GetComponentInChildren<Camera>();
            if (fpc.TryGetComponent(out UnityEngine.InputSystem.PlayerInput pi)) pi.enabled = false;   // no live input
            SoundBus.Emitted += e =>
            {
                if (e.Source != SoundSource.Player) return;
                playerSounds.Add(e);
                if (e.Type == SoundType.Footstep && e.Position.y < fpc.transform.position.y + 0.5f) { feetSteps++; feetStepIntensity = e.Intensity; }
            };
            StartCoroutine(test == "body" ? Body() : test == "stairs" ? Stairs() : Chase(test == "crouch"));
        }

        static void Log(string s) => Debug.Log("[Phase1] " + s);

        // ------------------------------------------------------------------ body

        IEnumerator Body()
        {
            SetAgents(false);
            Log($"components: exertion {body != null}, camera {camMotion != null}; walk {fpc.MoveSpeed} sprint {fpc.SprintSpeed}");
            // Room to run a 3.5 m circle: the NavMesh point furthest from any edge near the player.
            Vector3 best = fpc.transform.position; float bestClear = 0f;
            for (int x = -12; x <= 12; x += 2)
            for (int z = -12; z <= 12; z += 2)
            {
                Vector3 c = fpc.transform.position + new Vector3(x, 0f, z);
                if (!NavMesh.SamplePosition(c, out var h, 1f, NavMesh.AllAreas)) continue;
                if (NavMesh.FindClosestEdge(h.position, out var edge, NavMesh.AllAreas) && edge.distance > bestClear)
                { bestClear = edge.distance; best = h.position; }
            }
            cc.enabled = false; fpc.transform.position = best + Vector3.up * 0.05f; cc.enabled = true;
            Log($"test spot {best} ({bestClear:0.0} m from the nearest wall)");
            yield return new WaitForSeconds(0.5f);

            // Sprint in a circle (~3.5 m radius) until exhausted.
            float t0 = Time.time, sprintTime = -1f, maxFov = 0f, minY = 9f, maxY = -9f, speedSum = 0f; int n = 0;
            float baseFov = cam.fieldOfView;
            while (Time.time - t0 < 9f && !body.IsExhausted)
            {
                Steer(new Vector2(0f, 1f), true, 95f);
                float v = Flat(cc.velocity);
                if (Time.time - t0 > 1f) { speedSum += v; n++; }
                maxFov = Mathf.Max(maxFov, cam.fieldOfView);
                minY = Mathf.Min(minY, cam.transform.localPosition.y); maxY = Mathf.Max(maxY, cam.transform.localPosition.y);
                yield return null;
            }
            sprintTime = Time.time - t0;
            Log($"sprint: exhausted after {sprintTime:0.0}s (target ~5.5 + accel), mean speed {speedSum / Mathf.Max(1, n):0.00} m/s, " +
                $"FOV {baseFov:0}->{maxFov:0.0}, head bob y {minY:0.000}..{maxY:0.000} m");

            // Exhausted: keep holding sprint, measure speed and panting the AI can hear.
            playerSounds.Clear();
            float s0 = Time.time, exSpeed = 0f; n = 0;
            while (Time.time - s0 < 3f) { Steer(new Vector2(0f, 1f), true, 95f); if (Time.time - s0 > 1f) { exSpeed += Flat(cc.velocity); n++; } yield return null; }
            int pants = playerSounds.FindAll(e => e.Type == SoundType.Footstep && e.Position.y > fpc.transform.position.y + 1f).Count;
            Log($"exhausted, Shift held: speed {exSpeed / Mathf.Max(1, n):0.00} m/s (target {body.walkSpeed * body.exhaustedSpeedFactor:0.00}), " +
                $"exertion {body.Exertion:0.00}, audible pants in 3 s: {pants} (intensity {body.pantIntensity})");

            // Recover standing still.
            float r0 = Time.time;
            while (body.IsExhausted && Time.time - r0 < 10f) { Steer(Vector2.zero, false, 0f); yield return null; }
            Log($"recovered (stamina {body.Stamina:0.00}) after {Time.time - r0:0.0}s standing still");

            // Crouch walk (Shift released first: sprinting stands you up).
            Steer(Vector2.zero, false, 0f);
            yield return null;
            body.SetCrouched(true);
            playerSounds.Clear(); feetSteps = 0;
            yield return new WaitForSeconds(0.6f);
            float c0 = Time.time, cSpeed = 0f; n = 0;
            while (Time.time - c0 < 3f) { Steer(new Vector2(0f, 1f), false, 60f); if (Time.time - c0 > 1f) { cSpeed += Flat(cc.velocity); n++; } yield return null; }

            Log($"crouch: speed {cSpeed / Mathf.Max(1, n):0.00} m/s, capsule {cc.height:0.00} m, eye {cam.transform.parent.localPosition.y:0.00} m, " +
                $"{feetSteps} steps of intensity {feetStepIntensity:0.00} (walk 0.15)");
            body.SetCrouched(false);
            Steer(Vector2.zero, false, 0f);
            yield return new WaitForSeconds(0.8f);
            Log($"stand: capsule {cc.height:0.00} m, eye {cam.transform.parent.localPosition.y:0.00} m");

            // Drop from as high as the ceiling allows (max 3 m): landing spring.
            var p = fpc.transform.position;
            float room = Physics.Raycast(p + Vector3.up * 2.1f, Vector3.up, out var roof, 10f) ? roof.distance : 10f;
            float height = Mathf.Min(3f, room - 0.2f);
            if (height < 0.8f) Log($"drop skipped: only {room:0.0} m above the head here");
            else
            {
                cc.enabled = false; fpc.transform.position = p + Vector3.up * height; cc.enabled = true;
                float drop = 0f, l0 = Time.time;
                while (Time.time - l0 < 2f) { drop = Mathf.Min(drop, cam.transform.localPosition.y); yield return null; }
                Log($"{height:0.0} m drop: camera dipped {-drop:0.000} m on landing");
            }
            SetAgents(true);
            Log("body test done");
        }

        // ------------------------------------------------------------------ stairs

        /// SampleScene: from the spawn, walk south (down the stairs) for 4 s, once walking and once crouched.
        IEnumerator Stairs()
        {
            SetAgents(false);
            foreach (bool crouch in new[] { false, true })
            {
                cc.enabled = false; fpc.transform.SetPositionAndRotation(new Vector3(24f, 0.1f, 68.6f), Quaternion.Euler(0f, 180f, 0f)); cc.enabled = true;
                body.SetCrouched(crouch);
                yield return new WaitForSeconds(0.8f);
                playerSounds.Clear(); feetSteps = 0;
                float y0 = fpc.transform.position.y, t0 = Time.time, maxJump = 0f, lastEye = cam.transform.position.y;
                int snaps = 0;
                while (Time.time - t0 < (crouch ? 6f : 3.5f))
                {
                    Steer(new Vector2(0f, 1f), false, 0f);
                    yield return null;
                    float eye = cam.transform.position.y;
                    float rate = Mathf.Abs(eye - lastEye) / Mathf.Max(1e-4f, Time.deltaTime);
                    if (rate > 4f) { snaps++; Log($"  eye moved {eye - lastEye:+0.00;-0.00} m in one frame at y {fpc.transform.position.y:0.00} (grounded {cc.isGrounded})"); }
                    maxJump = Mathf.Max(maxJump, rate);
                    lastEye = eye;
                }
                Steer(Vector2.zero, false, 0f);
                int lands = playerSounds.FindAll(e => e.Type == SoundType.Land).Count;
                Log($"stairs {(crouch ? "crouched" : "walking")}: went down {y0 - fpc.transform.position.y:0.0} m, {feetSteps} steps, " +
                    $"{lands} landing noises (should be 0), fastest eye movement {maxJump:0.0} m/s, {snaps} frames over 4 m/s");
            }
            body.SetCrouched(false);
            SetAgents(true);
            Log("stairs test done");
        }

        // ------------------------------------------------------------------ chase

        IEnumerator Chase(bool sneak)
        {
            var listener = FindFirstObjectByType<ListenerBrain>();
            if (listener == null) { Log("no Listener in this scene"); yield break; }
            var motor = listener.GetComponent<AgentMotor>();
            var nav = listener.GetComponent<NavMeshAgent>();
            yield return new WaitForSeconds(0.5f);

            // Put the player 11 m from the Listener with a clear line between them, then make a loud noise there.
            Vector3 lp = listener.transform.position, start = lp;
            for (int i = 0; i < 24; i++)
            {
                Vector3 d = Quaternion.Euler(0f, i * 15f, 0f) * Vector3.forward;
                if (NavMesh.SamplePosition(lp + d * 11f, out var hit, 1f, NavMesh.AllAreas) &&
                    !NavMesh.Raycast(lp, hit.position, out _, NavMesh.AllAreas)) { start = hit.position; break; }
            }
            cc.enabled = false; fpc.transform.position = start; cc.enabled = true;
            Physics.SyncTransforms();
            Face(start - lp);
            yield return null;
            Noise.Make(SoundType.Gunshot, start + Vector3.up * 1.4f, 1f, SoundSource.Player, fpc.gameObject);
            Log($"{(sneak ? "crouch" : "chase")} test: gunshot 11 m from the Listener at {start}");

            bool died = false; death.Died += () => died = true;
            float t0 = Time.time, nextLog = 0f, maxNav = 0f; int lunges = 0; bool wasAttacking = false;
            if (sneak) body.SetCrouched(true);
            var listenerBody = listener.GetComponent<AgentBody>();
            while (Time.time - t0 < (sneak ? 25f : 20f) && !died)
            {
                float t = Time.time - t0;
                Vector3 away = fpc.transform.position - listener.transform.position; away.y = 0f;
                bool moving = !sneak || t < 6f;     // sneak: crouch away for 6 s, then freeze
                if (moving) FleeFrom(away, !sneak); else Steer(Vector2.zero, false, 0f);
                maxNav = Mathf.Max(maxNav, nav.velocity.magnitude);
                if (listenerBody.IsAttacking && !wasAttacking) lunges++;
                wasAttacking = listenerBody.IsAttacking;
                if (t >= nextLog)
                {
                    nextLog += 1f;
                    Log($"t={t:0.0} dist {away.magnitude:0.0} m | Listener {listener.StateName} v {nav.velocity.magnitude:0.0} " +
                        $"(chasing {motor.IsChasing}) | player v {Flat(cc.velocity):0.0} stamina {body.Stamina:0.00}" +
                        $"{(body.IsExhausted ? " EXHAUSTED" : "")}{(body.IsCrouching ? " crouched" : "")}");
                }
                yield return null;
            }
            body.SetCrouched(false);
            Steer(Vector2.zero, false, 0f);
            Log($"{(sneak ? "crouch" : "chase")} result: {(died ? $"KILLED after {Time.time - t0:0.0}s" : "survived")}, " +
                $"Listener top speed {maxNav:0.0} m/s, attacks started {lunges}");
        }

        // ------------------------------------------------------------------ driving

        void FleeFrom(Vector3 away, bool sprint)
        {
            // Run away from the threat; turn along walls (as a panicking player would).
            Vector3 dir = away.sqrMagnitude > 0.01f ? away.normalized : fpc.transform.forward;
            Vector3 p = fpc.transform.position;
            for (int i = 0; i < 12; i++)
            {
                Vector3 d = Quaternion.Euler(0f, (i % 2 == 0 ? 1 : -1) * (i + 1) / 2 * 30f, 0f) * dir;
                if (!NavMesh.Raycast(p, p + d * 3f, out _, NavMesh.AllAreas)) { dir = d; break; }
            }
            Face(dir);
            Steer(new Vector2(0f, 1f), sprint, 0f);
        }

        void Face(Vector3 dir)
        {
            dir.y = 0f;
            if (dir.sqrMagnitude < 0.01f) return;
            fpc.transform.rotation = Quaternion.RotateTowards(fpc.transform.rotation, Quaternion.LookRotation(dir), 400f * Time.deltaTime);
        }

        void Steer(Vector2 move, bool sprint, float turnDegPerSec)
        {
            input.move = move; input.sprint = sprint; input.jump = false;
            input.look = Vector2.zero;
            // Turn the body directly: with PlayerInput off the controller treats look as a stick (x deltaTime).
            if (turnDegPerSec != 0f) fpc.transform.Rotate(0f, turnDegPerSec * Time.deltaTime, 0f);
        }

        static float Flat(Vector3 v) => new Vector2(v.x, v.z).magnitude;

        static void SetAgents(bool on)
        {
            foreach (var a in FindObjectsByType<AgentBrain>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                a.gameObject.SetActive(on);
        }
    }
}
