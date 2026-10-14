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

        public float LastLandSpeed { get; private set; }

        CharacterController cc;
        FirstPersonController fpc;
        StarterAssetsInputs input;
        float travelled, peakFallSpeed;
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
                if (!wasGrounded) Land(feet);

                float speed = new Vector2(v.x, v.z).magnitude;
                if (speed > minStepSpeed)
                {
                    travelled += speed * Time.deltaTime;
                    bool running = IsRunning(speed);
                    if (travelled >= (running ? runStride : walkStride))
                    {
                        travelled = 0f;
                        InteractionNoise.Emit(running ? NoiseType.Run : NoiseType.Footstep, feet,
                                              running ? runIntensity : walkIntensity, NoiseSource.Player, gameObject);
                    }
                }
                peakFallSpeed = 0f;
            }
            else
            {
                if (wasGrounded && v.y > minJumpSpeed)
                    InteractionNoise.Emit(NoiseType.Jump, feet, jumpIntensity, NoiseSource.Player, gameObject);
                peakFallSpeed = Mathf.Max(peakFallSpeed, -v.y);
            }
            wasGrounded = grounded;
        }

        void Land(Vector3 feet)
        {
            travelled = 0f;
            LastLandSpeed = peakFallSpeed;
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
