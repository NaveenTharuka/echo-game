using System.Collections;
using Echo.AI;
using StarterAssets;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;

namespace Echo.Player
{
    /// Makes the camera behave like a head on a body instead of a tripod on rails. Everything is driven by something
    /// real, never by a free-running timer:
    ///   - head bob locked to PlayerNoise's stride: the head dips as each foot is planted, i.e. with every step sound;
    ///     weight shifts side to side over two steps. Walk / sprint / crouch each have their own amplitude.
    ///   - inertia: lean into strafes and fast turns; heavier sway when sprinting (FOV opens a little).
    ///   - landings: a spring that compresses with the fall speed and settles.
    ///   - breathing (PlayerExertion.BreathPhase): barely visible at rest, heaving and hunched when exhausted.
    ///   - fear: a fine tremor and narrowing vignette while a hunting agent is close (presentation only - it reads
    ///     AI state like TensionMusic does; the AI never reads the player).
    ///   - the torch is held in the left hand (gun in the right): offset low-left, aimed at the crosshair, trailing a little.
    ///   - death: snap towards what hit you, then collapse to the floor before the fade.
    /// One `intensity` scales all of it (0 = rock steady) for players who get motion sick; saved in PlayerPrefs.
    /// Added to the player automatically on every scene load, like PlayerFlashlight.
    [DefaultExecutionOrder(100)]   // after the FirstPersonController has turned the view this frame
    [DisallowMultipleComponent]
    public class PlayerCameraMotion : MonoBehaviour
    {
        public static bool AutoCreate = true;
        const string IntensityKey = "echo.cameraMotion";

        [Range(0f, 1f)] public float intensity = 1f;

        [Header("Eye height (m above the feet)")]
        public float standEyeHeight = 1.6f;
        public float crouchEyeHeight = 0.95f;

        [Header("Head bob: vertical m, sideways m, roll deg, nod deg")]
        public Vector4 crouchBob = new(0.012f, 0.014f, 0.35f, 0.25f);
        public Vector4 walkBob = new(0.03f, 0.02f, 0.6f, 0.4f);
        public Vector4 sprintBob = new(0.06f, 0.035f, 1.2f, 0.9f);

        [Header("Inertia")]
        [Tooltip("Roll (deg) per m/s of sideways speed, and per 100 deg/s of turning.")]
        public float strafeLean = 0.4f, turnLean = 0.35f;
        public float maxLean = 2.2f;
        public float sprintFovKick = 6f;

        [Header("Landing spring")]
        public float landDropPerSpeed = 0.35f;
        public float landNodPerSpeed = 7f;

        [Header("Fear (hunting agent nearby)")]
        public float fearRadius = 18f;
        public float tremorDegrees = 0.4f;

        [Header("Torch in the hand")]
        [Tooltip("Torch in the LEFT hand (the gun is in the right): keeps the gun out of the glare.")]
        public Vector3 torchOffset = new(-0.16f, -0.13f, 0.1f);
        [Tooltip("The beam converges on what the crosshair looks at, up to this distance (m).")]
        public float aimDistance = 18f;
        [Tooltip("How fast the torch catches up with the view (higher = stiffer wrist).")]
        public float torchFollow = 13f;

        /// 0..1, how afraid the body is right now (also drives the vignette).
        public float Fear { get; private set; }

        Transform root, cam, torch, player;
        Camera camera;
        CharacterController cc;
        PlayerNoise noise;
        PlayerExertion exertion;
        PlayerDeath death;
        FirstPersonController fpc;
        Volume volume;
        Vignette vignette;

        float baseFov, side = 1f, lastYaw, yawRate, lean, sprintBlend, eyeY, dying, smoothFootY;
        bool footYInit; float lastGroundedTime;
        Vector4 bobAmp;
        Spring dropSpring = new(140f, 0.55f), nodSpring = new(120f, 0.5f);
        Quaternion torchRot;
        readonly RaycastHit[] aimHits = new RaycastHit[8];
        Vector3 deathPos; Quaternion deathRot;
        bool isDead;

        // ------------------------------------------------------------------ auto-attach

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Boot()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            SceneManager.sceneLoaded += OnSceneLoaded;
            Attach();
        }

        static void OnSceneLoaded(Scene scene, LoadSceneMode mode) => Attach();

        static void Attach()
        {
            if (!AutoCreate) return;
            var p = FindFirstObjectByType<FirstPersonController>();
            if (p != null && !p.TryGetComponent(out PlayerCameraMotion _))
                p.gameObject.AddComponent<PlayerCameraMotion>();
        }

        // ------------------------------------------------------------------ lifecycle

        void Awake()
        {
            player = transform;
            fpc = GetComponent<FirstPersonController>();
            cc = GetComponent<CharacterController>();
            TryGetComponent(out death);
            camera = GetComponentInChildren<Camera>(true);
            if (camera == null) { enabled = false; return; }
            cam = camera.transform;
            root = fpc != null && fpc.CinemachineCameraTarget != null ? fpc.CinemachineCameraTarget.transform : cam.parent;
            if (root == cam) root = cam.parent;
            var t = cam.Find("Flashlight");
            if (t != null) torch = t;
            baseFov = camera.fieldOfView;
            eyeY = standEyeHeight;
            lastYaw = player.eulerAngles.y;
            torchRot = cam.rotation;
            intensity = PlayerPrefs.GetFloat(IntensityKey, intensity);
            BuildVolume();
        }

        void Start()
        {
            TryGetComponent(out noise);
            TryGetComponent(out exertion);
            if (noise != null) { noise.Stepped += OnStep; noise.Landed += OnLanded; }
        }

        void OnEnable() { if (death != null) death.Died += OnDied; }
        void OnDisable() { if (death != null) death.Died -= OnDied; }

        void OnDestroy()
        {
            if (noise != null) { noise.Stepped -= OnStep; noise.Landed -= OnLanded; }
            if (volume != null) { Destroy(volume.profile); Destroy(volume.gameObject); }
        }

        /// Settings hook: 0 = no camera motion at all, 1 = full. Saved for the next session.
        public void SetIntensity(float value)
        {
            intensity = Mathf.Clamp01(value);
            PlayerPrefs.SetFloat(IntensityKey, intensity);
        }

        // ------------------------------------------------------------------ events

        void OnStep(float stepIntensity)
        {
            side = -side;   // weight onto the other foot
            if (stepIntensity >= 0.25f) nodSpring.Kick(-6f);   // sprint footfalls jolt the view a little
        }

        void OnLanded(float fallSpeed)
        {
            if (fallSpeed < 1.5f) return;
            dropSpring.Kick(-Mathf.Min(4.5f, fallSpeed * landDropPerSpeed * 2.2f));
            nodSpring.Kick(-Mathf.Min(70f, fallSpeed * landNodPerSpeed));
        }

        void OnDied()
        {
            if (isDead) return;
            isDead = true;
            StartCoroutine(DeathFall(death != null ? death.Killer : null));
        }

        // ------------------------------------------------------------------ per frame

        void LateUpdate()
        {
            float dt = Time.deltaTime;
            if (dt <= 0f || isDead) return;
            float k = intensity;

            Vector3 v = cc != null ? cc.velocity : Vector3.zero;
            Vector3 flat = new(v.x, 0f, v.z);
            float speed = flat.magnitude;
            bool grounded = cc == null || cc.isGrounded;
            float crouch = exertion != null ? exertion.CrouchAmount : 0f;
            bool sprinting = exertion != null ? exertion.IsSprinting : speed > 4.6f;
            float walkSpeed = exertion != null ? exertion.walkSpeed : 4f;
            float sprintSpeed = exertion != null ? exertion.sprintSpeed : 6f;

            // --- eye height (crouch), and a slight hunch when gasping
            float exhausted = exertion != null && exertion.IsExhausted ? 1f : 0f;
            float targetEye = Mathf.Lerp(standEyeHeight, crouchEyeHeight, Smooth(crouch)) - 0.05f * exhausted;
            eyeY = Mathf.Lerp(eyeY, targetEye, 1f - Mathf.Exp(-10f * dt));
            // Stairs: the body drops ~0.2 m per step; the eyes glide down instead (bigger changes - jumps, falls,
            // teleports - are followed at once).
            // A stair = on the ground (the controller snaps down each step), or only just off it and not moving fast
            // vertically (a jump leaves at ~5 m/s).
            float footY = player.position.y;
            if (grounded) lastGroundedTime = Time.time;
            bool stairLike = grounded || (Time.time - lastGroundedTime < 0.3f && Mathf.Abs(v.y) < 3.5f);
            if (!footYInit || !stairLike || Mathf.Abs(footY - smoothFootY) > 1f) { smoothFootY = footY; footYInit = true; }
            else smoothFootY = Mathf.Lerp(smoothFootY, footY, 1f - Mathf.Exp(-16f * dt));
            var rp = root.localPosition; rp.y = eyeY + (smoothFootY - footY); root.localPosition = rp;

            // --- head bob amplitude for the current gait (fades out when slow or airborne)
            Vector4 gait = crouch > 0.5f ? crouchBob
                         : Vector4.Lerp(walkBob, sprintBob, Mathf.InverseLerp(walkSpeed, sprintSpeed, speed));
            float moving = grounded ? Mathf.Clamp01(speed / (crouch > 0.5f ? 1.2f : walkSpeed * 0.8f)) : 0f;
            bobAmp = Vector4.MoveTowards(bobAmp, gait * moving, dt * 0.25f);
            float p = noise != null ? noise.StepProgress : Mathf.Repeat(Time.time * 2f, 1f);
            float upDown = -0.5f * Mathf.Cos(2f * Mathf.PI * p);   // lowest when the foot is planted
            float sway = Mathf.Sin(Mathf.PI * p) * side;           // over the standing foot
            Vector3 bobPos = new(bobAmp.y * sway, bobAmp.x * upDown, 0f);
            Vector3 bobRot = new(bobAmp.w * upDown, 0f, bobAmp.z * sway);   // tips forward on the plant

            // --- breathing: chest rise on the inhale
            float breath = 0f, breathAmp = 0.004f, breathNod = 0.15f;
            if (exertion != null)
            {
                float bp = exertion.BreathPhase;
                breath = bp < 0.45f ? Smooth(bp / 0.45f) : 1f - Smooth((bp - 0.45f) / 0.55f);
                breathAmp = Mathf.Lerp(0.004f, 0.022f, exertion.Exertion);
                breathNod = Mathf.Lerp(0.15f, 1.3f, exertion.Exertion);
            }
            else breath = 0.5f + 0.5f * Mathf.Sin(Time.time * 2f * Mathf.PI * 0.25f);
            Vector3 breathPos = new(0f, breathAmp * (breath - 0.5f), 0f);
            Vector3 breathRot = new(breathNod * (breath - 0.5f) - 2f * exhausted, 0f, 0f);   // head up on the inhale

            // --- inertia: lean into strafes and turns
            float yaw = player.eulerAngles.y;
            yawRate = Mathf.Lerp(yawRate, Mathf.DeltaAngle(lastYaw, yaw) / dt, 1f - Mathf.Exp(-12f * dt));
            lastYaw = yaw;
            float lateral = Vector3.Dot(flat, player.right);
            float targetLean = Mathf.Clamp(-lateral * strafeLean - yawRate * 0.01f * turnLean, -maxLean, maxLean);
            lean = Mathf.Lerp(lean, targetLean, 1f - Mathf.Exp(-6f * dt));

            // --- fear: a hunting agent close by
            Fear = Mathf.MoveTowards(Fear, FearNow(), dt * (FearNow() > Fear ? 1.2f : 0.25f));
            float tt = Time.time * 6f;
            Vector3 tremor = new Vector3((Mathf.PerlinNoise(tt, 0.3f) - 0.5f), (Mathf.PerlinNoise(0.7f, tt) - 0.5f), 0f)
                             * (2f * tremorDegrees * Fear);
            // a hand-held camera is never perfectly still
            float d = Time.time * 0.35f;
            Vector3 drift = new Vector3(Mathf.PerlinNoise(d, 5.1f) - 0.5f, Mathf.PerlinNoise(9.3f, d) - 0.5f, 0f) * 0.3f;

            // --- springs (landings, heavy footfalls)
            dropSpring.Step(dt); nodSpring.Step(dt);

            // --- sprint FOV
            sprintBlend = Mathf.MoveTowards(sprintBlend, sprinting ? 1f : 0f, dt * 2.5f);
            camera.fieldOfView = baseFov + sprintFovKick * Smooth(sprintBlend) * k;

            Vector3 pos = (bobPos + breathPos + new Vector3(0f, dropSpring.X, 0f)) * k;
            Vector3 rot = (bobRot + breathRot + tremor + drift) * k
                        + new Vector3(nodSpring.X, 0f, lean) * k;
            cam.localPosition = pos;
            cam.localRotation = Quaternion.Euler(-rot.x, rot.y, rot.z);

            UpdateTorch(dt, sway * bobAmp.z);
            UpdateVolume(exertion != null ? exertion.Exertion : 0f);
        }

        float FearNow()
        {
            float fear = 0f;
            Vector3 me = player.position;
            foreach (var a in AgentRegistry.All)
            {
                if (a == null || !a.IsAlive) continue;
                float dist = Vector3.Distance(a.transform.position, me);
                float near = 1f - Mathf.Clamp01(dist / fearRadius);
                float hunting = Mathf.InverseLerp(0.3f, 0.9f, a.AlertLevel);
                fear = Mathf.Max(fear, near * near * Mathf.Lerp(0.35f, 1f, hunting));
            }
            return fear;
        }

        void UpdateTorch(float dt, float swing)
        {
            if (torch == null) return;
            torch.localPosition = torchOffset * Mathf.Max(0.35f, intensity);
            // The hand points the torch at what the eyes look at, so the hotspot sits on the crosshair even though the
            // torch is held off to the side (parallax corrected up to aimDistance).
            Vector3 aim = cam.position + cam.forward * aimDistance;
            int n = Physics.RaycastNonAlloc(cam.position, cam.forward, aimHits, aimDistance, ~0, QueryTriggerInteraction.Ignore);
            float nearest = aimDistance;
            for (int i = 0; i < n; i++)
                if (!aimHits[i].transform.IsChildOf(player) && aimHits[i].distance < nearest) nearest = aimHits[i].distance;
            aim = cam.position + cam.forward * Mathf.Max(0.6f, nearest);
            Quaternion want = Quaternion.LookRotation(aim - torch.position, cam.up);
            // The wrist trails the view, then catches up; plus a little swing with the steps.
            torchRot = Quaternion.Slerp(torchRot, want, 1f - Mathf.Exp(-torchFollow * dt));
            if (Quaternion.Angle(torchRot, want) > 25f)
                torchRot = Quaternion.RotateTowards(want, torchRot, 25f);
            torch.rotation = Quaternion.Slerp(want, torchRot, intensity) * Quaternion.Euler(swing * 1.5f, swing * 2f, 0f);
        }

        // ------------------------------------------------------------------ post effects

        void BuildVolume()
        {
            var go = new GameObject("PlayerBodyFX (runtime)");
            volume = go.AddComponent<Volume>();
            volume.isGlobal = true;
            volume.priority = 50f;     // above the level's global volume; weight blends it in
            volume.weight = 0f;
            volume.profile = ScriptableObject.CreateInstance<VolumeProfile>();
            vignette = volume.profile.Add<Vignette>(true);
            vignette.intensity.Override(0.52f);
            vignette.smoothness.Override(0.55f);
            vignette.color.Override(Color.black);
            var ca = volume.profile.Add<ChromaticAberration>(true);
            ca.intensity.Override(0.45f);
            var colour = volume.profile.Add<ColorAdjustments>(true);
            colour.saturation.Override(-30f);
        }

        void UpdateVolume(float exertionLevel)
        {
            if (volume == null) return;
            float w = Mathf.Clamp01(Mathf.InverseLerp(0.45f, 1f, exertionLevel) * 0.7f + Fear * 0.75f + dying);
            volume.weight = Mathf.Lerp(volume.weight, w, 1f - Mathf.Exp(-4f * Time.deltaTime));
        }

        // ------------------------------------------------------------------ death

        IEnumerator DeathFall(GameObject killer)
        {
            // Snap towards whatever struck (sideways from the blow), then fold to the floor.
            Vector3 toKiller = killer != null ? killer.transform.position - player.position : -player.forward;
            toKiller.y = 0f;
            float turn = toKiller.sqrMagnitude > 0.01f
                ? Mathf.Clamp(Vector3.SignedAngle(cam.forward.WithY0(), toKiller, Vector3.up), -70f, 70f) : 0f;
            float rollSide = turn >= 0f ? -1f : 1f;
            Vector3 startPos = root.localPosition;
            Quaternion startRot = cam.localRotation;
            const float jolt = 0.14f, fall = 0.95f;

            for (float t = 0f; t < jolt; t += Time.deltaTime)
            {
                float u = Smooth(t / jolt);
                cam.localRotation = startRot * Quaternion.Euler(-6f * u, turn * 0.6f * u, 4f * rollSide * u);
                dying = u * 0.6f;
                volume.weight = Mathf.Max(volume.weight, dying);
                yield return null;
            }
            Quaternion fromRot = cam.localRotation;
            for (float t = 0f; t < fall; t += Time.deltaTime)
            {
                float u = t / fall;
                float drop = u * u;                                            // legs give way, then gravity
                var p = startPos; p.y = Mathf.Lerp(startPos.y, 0.3f, drop);
                root.localPosition = p;
                cam.localRotation = fromRot * Quaternion.Euler(-12f * drop, turn * 0.25f * u, 78f * rollSide * drop);
                dying = Mathf.Lerp(0.6f, 1f, u);
                volume.weight = dying;
                yield return null;
            }
            // hit the floor: a small bounce
            for (float t = 0f; t < 0.25f; t += Time.deltaTime)
            {
                var p = root.localPosition; p.y = 0.3f + 0.04f * Mathf.Sin(Mathf.PI * t / 0.25f);
                root.localPosition = p;
                yield return null;
            }
        }

        static float Smooth(float x) { x = Mathf.Clamp01(x); return x * x * (3f - 2f * x); }

        /// Damped spring around 0 (semi-implicit Euler; stable at game frame rates for these stiffnesses).
        struct Spring
        {
            readonly float stiffness, damping;
            public float X, V;
            public Spring(float stiffness, float dampingRatio)
            {
                this.stiffness = stiffness; damping = 2f * Mathf.Sqrt(stiffness) * dampingRatio; X = V = 0f;
            }
            public void Kick(float velocity) => V += velocity;
            public void Step(float dt)
            {
                dt = Mathf.Min(dt, 1f / 30f);
                V += (-stiffness * X - damping * V) * dt;
                X += V * dt;
            }
        }
    }

    static class VectorExt
    {
        public static Vector3 WithY0(this Vector3 v) => new(v.x, 0f, v.z);
    }
}
