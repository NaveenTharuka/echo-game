using Echo.Interaction;
using StarterAssets;
using UnityEngine;

namespace Echo.Player
{
    /// The noises the player's body makes (guide §6.1): footsteps, running steps, jump push-off and landings,
    /// all through InteractionNoise with source Player. Reads the CharacterController only - no Starter Assets
    /// code is changed, and it works the same whoever moves the controller.
    ///
    /// Steps are distance-based (one per stride travelled on the ground), so standing still or pushing against
    /// a wall is silent and the step rate follows the real speed: walk 4 m/s / 1.6 m = 2.5 steps/s,
    /// sprint 6 m/s / 2.2 m = 2.7 steps/s but twice as loud. Landing loudness scales with the fall speed.
    [RequireComponent(typeof(CharacterController))]
    [DisallowMultipleComponent]
    public class PlayerNoise : MonoBehaviour
    {
        [Header("Steps (guide §19)")]
        public float walkStride = 1.6f;
        public float runStride = 2.2f;
        public float walkIntensity = 0.15f;
        public float runIntensity = 0.30f;
        [Tooltip("Crouched steps (PlayerExertion): short and nearly silent.")]
        public float crouchStride = 1.0f;
        public float crouchIntensity = 0.04f;
        [Tooltip("m/s. Slower than this is standing still (no steps).")]
        public float minStepSpeed = 0.3f;

        [Header("Jump / land")]
        public float jumpIntensity = 0.2f;
        [Tooltip("m/s. Upward speed when leaving the ground that counts as a jump (walking off a ledge is silent).")]
        public float minJumpSpeed = 1f;
        [Tooltip("m/s. Slower landings (small step-downs) make no landing noise.")]
        public float minLandSpeed = 2f;
        [Tooltip("Landing intensity = base + fallSpeed * perMetrePerSecond, clamped to min..max (guide: 0.1-0.6).")]
        public float landBase = 0.08f;
        public float landPerSpeed = 0.05f;
        public float minLandIntensity = 0.1f;
        public float maxLandIntensity = 0.6f;
        [Tooltip("m. Drops smaller than this are not landings (walking down stairs drops ~0.2 m per step).")]
        public float minLandDrop = 0.45f;

        public float LastLandSpeed { get; private set; }
        /// 0..1 through the current stride (0 = foot just planted). The camera bob follows it, so it dips with each step sound.
        public float StepProgress { get; private set; }
        /// A foot was planted (intensity). Landed: touched down after a fall (fall speed, m/s).
        public event System.Action<float> Stepped;
        public event System.Action<float> Landed;

        CharacterController cc;
        FirstPersonController fpc;
        StarterAssetsInputs input;
        PlayerExertion exertion;
        float travelled, peakFallSpeed, airPeakY;
        bool wasGrounded = true;

        void Awake()
        {
            cc = GetComponent<CharacterController>();
            fpc = GetComponent<FirstPersonController>();
            input = GetComponent<StarterAssetsInputs>();
        }


        void Update()
        {
            Vector3 v = cc.velocity;
            Vector3 feet = transform.position;
            bool grounded = cc.isGrounded;

            if (grounded)
            {
                if (!wasGrounded) Land(feet, airPeakY - feet.y);

                float speed = new Vector2(v.x, v.z).magnitude;
                if (speed > minStepSpeed)
                {
                    travelled += speed * Time.deltaTime;
                    bool running = IsRunning(speed);
                    if (exertion == null) TryGetComponent(out exertion);   // auto-attached after Awake
                    bool crouched = exertion != null && exertion.IsCrouching;
                    float stride = running ? runStride : crouched ? crouchStride : walkStride;
                    if (travelled >= stride)
                    {
                        travelled = 0f;
                        float intensity = running ? runIntensity : crouched ? crouchIntensity : walkIntensity;
                        InteractionNoise.Emit(running ? NoiseType.Run : NoiseType.Footstep, feet,
                                              intensity, NoiseSource.Player, gameObject);
                        Stepped?.Invoke(intensity);
                    }
                    StepProgress = travelled / stride;
                }
                peakFallSpeed = 0f;
            }
            else
            {
                if (wasGrounded)
                {
                    airPeakY = feet.y;
                    if (v.y > minJumpSpeed)
                        InteractionNoise.Emit(NoiseType.Jump, feet, jumpIntensity, NoiseSource.Player, gameObject);
                }
                airPeakY = Mathf.Max(airPeakY, feet.y);
                peakFallSpeed = Mathf.Max(peakFallSpeed, -v.y);
            }
            wasGrounded = grounded;
        }

        void Land(Vector3 feet, float drop)
        {
            if (drop < minLandDrop) return;   // a stair, not a landing: the stride (and step sounds) carry on
            travelled = 0f;
            LastLandSpeed = peakFallSpeed;
            Landed?.Invoke(peakFallSpeed);
            if (peakFallSpeed < minLandSpeed) return;
            float intensity = Mathf.Clamp(landBase + peakFallSpeed * landPerSpeed, minLandIntensity, maxLandIntensity);
            InteractionNoise.Emit(NoiseType.Land, feet, intensity, NoiseSource.Player, gameObject);
        }

        // Sprint key held AND actually faster than walking (dragging a barricade slows sprint below walk speed).
        bool IsRunning(float speed)
        {
            float walk = fpc != null ? fpc.MoveSpeed : 4f;
            return input != null && input.sprint && speed > walk + 0.3f;
        }
    }
}
