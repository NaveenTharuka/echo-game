using Echo.Interaction;
using Echo.Sound;
using StarterAssets;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;

namespace Echo.Player
{
    /// The player's body: stamina, exhaustion, crouching and breathing (Phase 1 "the threat is real").
    ///
    /// Sprint is a resource, not an escape button (Alien: Isolation rule: running is the risky choice):
    ///   - ~5.5 s of sprint, then exhausted: no sprint, slowed, and loud panting that agents can hear nearby
    ///     (Footstep-type SoundEvents from the player's position - the panting is where you are).
    ///   - Stamina comes back after a short pause; fastest standing still or crouched.
    ///   - Crouch (C toggles, Ctrl holds): slow and nearly silent steps (PlayerNoise.crouchIntensity), lower eyes and
    ///     capsule, cannot stand up under something.
    /// FirstPersonController asks TargetSpeed() and CanJump through its TargetSpeedOverride / JumpAllowed hooks. Breathing audio is synthesised (no human breath clip in
    /// the project yet); drop recorded clips into inhaleClips / exhaleClips to replace it.
    /// Added to the player automatically on every scene load, like PlayerFlashlight.
    [DisallowMultipleComponent]
    public class PlayerExertion : MonoBehaviour
    {
        public static bool AutoCreate = true;
        static bool hintShown;

        [Header("Speeds (m/s) - applied to the FirstPersonController")]
        public float walkSpeed = 3.2f;
        public float sprintSpeed = 5.6f;
        public float crouchSpeed = 1.7f;
        [Tooltip("Exhausted: top speed = walkSpeed x this.")]
        public float exhaustedSpeedFactor = 0.8f;
        [Tooltip("Lower = heavier body (FirstPersonController.SpeedChangeRate).")]
        public float acceleration = 7f;

        [Header("Stamina")]
        [Tooltip("Seconds of sprint from full.")]
        public float sprintSeconds = 5.5f;
        public float regenDelay = 0.9f;
        [Tooltip("Seconds from empty to full while walking / standing or crouched.")]
        public float regenSecondsWalking = 9f, regenSecondsResting = 5f;
        [Tooltip("Exhausted until stamina is back to this.")]
        public float recoverAt = 0.4f;
        public float jumpCost = 0.12f;

        [Header("Crouch")]
        public float standHeight = 2f;
        public float crouchHeight = 1.2f;
        public string crouchToggleBinding = "<Keyboard>/c";
        public string crouchHoldBinding = "<Keyboard>/leftCtrl";

        [Header("Breathing")]
        [Tooltip("Panting the AI can hear (intensity x 45 m x sensitivity, e.g. 0.08 = ~4 m for the Listener).")]
        public float pantIntensity = 0.08f;
        [Tooltip("Exertion above this makes audible panting.")]
        public float pantThreshold = 0.55f;
        public float breathVolume = 0.55f;
        public AudioClip[] inhaleClips, exhaleClips;

        /// 0 empty .. 1 full.
        public float Stamina { get; private set; } = 1f;
        public bool IsExhausted { get; private set; }
        public bool IsSprinting { get; private set; }
        public bool IsCrouching { get; private set; }
        /// Smoothed 0 standing .. 1 crouched (camera height).
        public float CrouchAmount { get; private set; }
        /// How out of breath, 0 calm .. 1 gasping. Rises while sprinting, falls slowly afterwards.
        public float Exertion { get; private set; }
        /// 0..1 through the current breath (0..0.45 inhale, the rest exhale). The camera breathes with it.
        public float BreathPhase { get; private set; }
        public bool CanJump => !IsCrouching && !IsExhausted && Stamina >= jumpCost;

        FirstPersonController fpc;
        StarterAssetsInputs input;
        CharacterController cc;
        PlayerDeath death;
        InputAction crouchToggle, crouchHold;
        AudioSource breathSource;
        float regenAt, breathRate = 0.25f, nextPantNoise, footOffset;
        bool wasGrounded = true, holdCrouch, toggledCrouch, inhaled;
        readonly Collider[] overlap = new Collider[8];

        // ------------------------------------------------------------------ auto-attach

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() => hintShown = false;

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
            var player = FindFirstObjectByType<FirstPersonController>();
            if (player != null && !player.TryGetComponent(out PlayerExertion _))
                player.gameObject.AddComponent<PlayerExertion>();
        }

        // ------------------------------------------------------------------ lifecycle

        void Awake()
        {
            fpc = GetComponent<FirstPersonController>();
            input = GetComponent<StarterAssetsInputs>();
            cc = GetComponent<CharacterController>();
            TryGetComponent(out death);
            if (fpc != null)
            {
                fpc.MoveSpeed = walkSpeed;
                fpc.SprintSpeed = sprintSpeed;
                fpc.SpeedChangeRate = acceleration;
                fpc.TargetSpeedOverride = TargetSpeed;
                fpc.JumpAllowed = () => CanJump;
            }
            if (cc != null) { standHeight = cc.height; footOffset = cc.center.y - cc.height * 0.5f; }   // keep the capsule's base where it was

            crouchToggle = new InputAction("CrouchToggle", InputActionType.Button, crouchToggleBinding);
            crouchToggle.performed += _ => toggledCrouch = !toggledCrouch;
            crouchHold = new InputAction("CrouchHold", InputActionType.Button, crouchHoldBinding);
            crouchHold.started += _ => holdCrouch = true;
            crouchHold.canceled += _ => holdCrouch = false;

            breathSource = BuildBreathSource();
            if (inhaleClips == null || inhaleClips.Length == 0) inhaleClips = BreathSynth.Make(true, 3);
            if (exhaleClips == null || exhaleClips.Length == 0) exhaleClips = BreathSynth.Make(false, 3);
        }

        void OnEnable()
        {
            crouchToggle.Enable(); crouchHold.Enable();
            if (death != null) death.Died += OnDied;
        }

        void OnDisable()
        {
            crouchToggle.Disable(); crouchHold.Disable();
            if (death != null) death.Died -= OnDied;
        }

        void OnDestroy()
        {
            crouchToggle.Dispose(); crouchHold.Dispose();
            if (fpc != null) { fpc.TargetSpeedOverride = null; fpc.JumpAllowed = null; }
        }

        void Start()
        {
            if (!hintShown) Invoke(nameof(ShowHint), 6f);
        }

        void ShowHint()
        {
            hintShown = true;
            HudMessage.Show("[C]  Crouch to move quietly", 4f);   // short: the HUD line has a fixed width
        }

        void OnDied()
        {
            if (breathSource != null) breathSource.Stop();
            enabled = false;
        }

        // ------------------------------------------------------------------ per frame

        void Update()
        {
            float dt = Time.deltaTime;
            UpdateCrouch(dt);

            Vector3 v = cc != null ? cc.velocity : Vector3.zero;
            float speed = new Vector2(v.x, v.z).magnitude;
            bool grounded = cc == null || cc.isGrounded;
            IsSprinting = input != null && input.sprint && !IsCrouching && !IsExhausted && speed > walkSpeed + 0.2f;

            // Jump cost: paid on take-off.
            if (wasGrounded && !grounded && v.y > 1f) { Stamina = Mathf.Max(0f, Stamina - jumpCost); regenAt = Time.time + regenDelay; }
            wasGrounded = grounded;

            if (IsSprinting)
            {
                Stamina -= dt / Mathf.Max(0.1f, sprintSeconds);
                regenAt = Time.time + regenDelay;
                if (Stamina <= 0f) { Stamina = 0f; IsExhausted = true; }
            }
            else if (Time.time >= regenAt)
            {
                bool resting = speed < 0.3f || IsCrouching;
                Stamina += dt / (resting ? regenSecondsResting : regenSecondsWalking);
                Stamina = Mathf.Min(1f, Stamina);
            }
            if (IsExhausted && Stamina >= recoverAt) IsExhausted = false;

            // Exertion follows effort quickly and calms down slowly (the body lags the stamina bar).
            float effort = IsExhausted ? 1f : IsSprinting ? Mathf.Lerp(0.35f, 0.9f, 1f - Stamina) : (1f - Stamina) * 0.8f;
            Exertion = Mathf.MoveTowards(Exertion, effort, dt * (effort > Exertion ? 0.6f : 0.12f));

            UpdateBreathing(dt);
        }

        /// What FirstPersonController should move at, given its (possibly scaled, e.g. dragging) walk/sprint speeds.
        public float TargetSpeed(bool sprintHeld, float walk, float sprint)
        {
            float scale = walkSpeed > 0f ? walk / walkSpeed : 1f;   // PhysicsCarrier scales both while dragging
            if (IsCrouching) return crouchSpeed * scale;
            if (IsExhausted) return walk * exhaustedSpeedFactor;
            return sprintHeld ? sprint : walk;
        }

        // ------------------------------------------------------------------ crouch

        /// Crouch on / off from code (tests, a future settings or touch UI). Same as pressing C.
        public void SetCrouched(bool on) => toggledCrouch = on;

        void UpdateCrouch(float dt)
        {
            bool want = holdCrouch || toggledCrouch;
            if (want && input != null && input.sprint && !IsExhausted) { want = false; toggledCrouch = false; }   // sprint stands you up
            if (!want && IsCrouching && !RoomToStand()) want = true;                                              // something overhead
            IsCrouching = want;

            CrouchAmount = Mathf.MoveTowards(CrouchAmount, IsCrouching ? 1f : 0f, dt / 0.28f);
            if (cc == null) return;
            float h = Mathf.Lerp(standHeight, crouchHeight, Smooth(CrouchAmount));
            if (!Mathf.Approximately(cc.height, h))
            {
                cc.height = h;
                cc.center = new Vector3(cc.center.x, h * 0.5f + footOffset, cc.center.z);
            }
        }

        bool RoomToStand()
        {
            if (cc == null) return true;
            float r = cc.radius * 0.95f;
            Vector3 bottom = transform.position + Vector3.up * (crouchHeight - r);
            Vector3 top = transform.position + Vector3.up * (standHeight - r);
            int n = Physics.OverlapCapsuleNonAlloc(bottom, top, r, overlap, ~0, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < n; i++)
                if (!overlap[i].transform.IsChildOf(transform)) return false;
            return true;
        }

        static float Smooth(float x) => x * x * (3f - 2f * x);

        // ------------------------------------------------------------------ breathing

        void UpdateBreathing(float dt)
        {
            // 0.25 Hz calm (15 / min) .. 0.9 Hz gasping (54 / min).
            float targetRate = Mathf.Lerp(0.25f, 0.9f, Exertion);
            breathRate = Mathf.MoveTowards(breathRate, targetRate, dt * 0.5f);
            BreathPhase += dt * breathRate;
            if (BreathPhase >= 1f) { BreathPhase -= 1f; inhaled = false; Breathe(false); }
            if (!inhaled && BreathPhase >= 0f && BreathPhase < 0.45f) { inhaled = true; Breathe(true); }

            // Panting the hunters can hear: once per breath while out of breath.
            if (Exertion > pantThreshold && Time.time >= nextPantNoise)
            {
                nextPantNoise = Time.time + 1f / Mathf.Max(0.2f, breathRate);
                float k = Mathf.InverseLerp(pantThreshold, 1f, Exertion);
                Noise.Make(SoundType.Footstep, transform.position + Vector3.up * 1.5f, pantIntensity * Mathf.Lerp(0.6f, 1f, k),
                           SoundSource.Player, gameObject, playAudio: false);
            }
        }

        void Breathe(bool inhale)
        {
            if (breathSource == null) return;
            // Barely there at rest, clearly audible from ~35 % exertion, gasping when exhausted.
            float loud = Mathf.Clamp01((Exertion - 0.15f) / 0.85f);
            if (loud <= 0.02f) return;
            var clips = inhale ? inhaleClips : exhaleClips;
            if (clips == null || clips.Length == 0) return;
            breathSource.pitch = Random.Range(0.94f, 1.06f) * Mathf.Lerp(1f, 1.12f, Exertion);
            breathSource.PlayOneShot(clips[Random.Range(0, clips.Length)], breathVolume * loud * (inhale ? 0.85f : 1f));
        }

        AudioSource BuildBreathSource()
        {
            var cam = GetComponentInChildren<Camera>(true);
            var go = new GameObject("Breath");
            go.transform.SetParent(cam != null ? cam.transform : transform, false);
            var s = go.AddComponent<AudioSource>();
            s.playOnAwake = false; s.spatialBlend = 0f; s.loop = false;
            return s;
        }
    }

    /// Procedural breath one-shots: band-limited noise with a breathy envelope. A stand-in until recorded clips exist.
    static class BreathSynth
    {
        const int Rate = 44100;

        public static AudioClip[] Make(bool inhale, int count)
        {
            var clips = new AudioClip[count];
            var rng = new System.Random(inhale ? 17 : 29);
            for (int c = 0; c < count; c++)
            {
                float seconds = (inhale ? 0.55f : 0.75f) * (0.9f + 0.2f * (float)rng.NextDouble());
                int n = (int)(seconds * Rate);
                var data = new float[n];
                // Inhale: brighter, airy hiss through the teeth. Exhale: darker, chest and mouth.
                float lpHz = inhale ? 3200f : 1700f, hpHz = inhale ? 700f : 260f;
                float lpA = Mathf.Exp(-2f * Mathf.PI * lpHz / Rate), hpA = Mathf.Exp(-2f * Mathf.PI * hpHz / Rate);
                float lp = 0f, lp2 = 0f, hpIn = 0f, hp = 0f, peak = 0f;
                float flutter = 7f + 5f * (float)rng.NextDouble();
                for (int i = 0; i < n; i++)
                {
                    float t = (float)i / n;
                    float white = (float)(rng.NextDouble() * 2.0 - 1.0);
                    lp = lpA * lp + (1f - lpA) * white;           // two one-pole low-passes
                    lp2 = lpA * lp2 + (1f - lpA) * lp;
                    hp = hpA * (hp + lp2 - hpIn); hpIn = lp2;      // one-pole high-pass
                    float env = inhale
                        ? Mathf.Sin(Mathf.PI * Mathf.Pow(t, 0.7f))                                    // swells, cuts off
                        : Mathf.Pow(Mathf.Sin(Mathf.PI * Mathf.Min(1f, t * 1.6f)), 0.6f) * (1f - t * 0.6f); // pushes out, fades
                    env *= 1f + 0.18f * Mathf.Sin(2f * Mathf.PI * flutter * t * seconds);            // throat flutter
                    data[i] = hp * env;
                    peak = Mathf.Max(peak, Mathf.Abs(data[i]));
                }
                float norm = peak > 0f ? 0.8f / peak : 1f;
                for (int i = 0; i < n; i++) data[i] *= norm;
                clips[c] = AudioClip.Create((inhale ? "Inhale_" : "Exhale_") + c, n, 1, Rate, false);
                clips[c].SetData(data, 0);
            }
            return clips;
        }
    }
}
