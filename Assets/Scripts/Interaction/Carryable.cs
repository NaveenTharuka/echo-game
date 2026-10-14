using System;
using UnityEngine;

namespace Echo.Interaction
{
    /// A loose physics prop the player can pick up and throw, or, above the carrier's lift limit, drag.
    /// What the player can do with it (lift / too heavy / later drag) is decided by PhysicsCarrier from the
    /// body's mass; this component owns what happens to the body itself: impact noise, the collision
    /// detection mode per phase, and the kill-plane respawn.
    ///
    /// Phases (cost vs. tunnelling - only fast bodies pay for continuous detection):
    ///   Resting  - Discrete, no interpolation. The cheap default; most props are asleep.
    ///   Held     - ContinuousSpeculative + Interpolate. Velocity-driven, can be swung at walls.
    ///   Flight   - ContinuousDynamic + Interpolate. Thrown/dropped, until the first hit.
    ///   Settling - Discrete + Interpolate after the first hit, until the body sleeps (smooth bounces).
    [RequireComponent(typeof(Rigidbody))]
    [DisallowMultipleComponent]
    public class Carryable : MonoBehaviour, IInteractable, IHoldToInteract
    {
        /// Bodies that fall below this height are put back where they last came to rest.
        public const float KillPlaneY = -50f;

        public enum Phase { Resting, Held, Flight, Settling }

        [Header("Impact noise (guide §6.3, §19)")]
        [Tooltip("MetalImpact for metal props, ObjectDrop for everything else.")]
        public NoiseType impactNoise = NoiseType.ObjectDrop;
        [Tooltip("Impact speed along the contact normal (m/s) mapped onto min..max intensity. Slower is silent.")]
        public float minImpactSpeed = 1.5f;
        public float maxImpactSpeed = 10f;
        public float minIntensity = 0.15f;
        public float maxIntensity = 0.7f;
        [Tooltip("Minimum seconds between impact noises, so a bouncing prop is one sound, not a burst.")]
        public float impactCooldown = 0.15f;

        [Header("Drag release")]
        [Tooltip("Linear + angular damping right after a drag ends, so a heavy prop stops instead of sliding on.")]
        public float releaseDamping = 10f;
        [Tooltip("s. How long the release damping lasts before the body's own damping returns.")]
        public float releaseDampingTime = 0.5f;

        public Rigidbody Body { get; private set; }
        public Phase CurrentPhase { get; private set; }
        /// Where the body last came to rest (or was picked up). The kill plane respawns it here.
        public Vector3 LastValidPosition { get; private set; }
        /// Radius around the centre of mass that contains every collider (set when picked up).
        public float Radius { get; private set; }
        public Collider[] Colliders { get; private set; }

        /// Fired after each accepted impact noise: (this, intensity). Used by the self-test.
        public event Action<Carryable, float> Impacted;
        public event Action<Carryable> Respawned;

        const float RespawnSilence = 0.5f;

        Quaternion lastValidRotation;
        bool wasSleeping;
        float silentUntil, dampedUntil, baseLinearDamping, baseAngularDamping;
        PlayerInteractor cachedInteractor;
        PhysicsCarrier cachedCarrier;

        void Awake()
        {
            Body = GetComponent<Rigidbody>();
            Colliders = GetComponentsInChildren<Collider>();
            // Transform, not Body: with autoSyncTransforms off the body may not have the spawn pose yet.
            LastValidPosition = transform.position;
            lastValidRotation = transform.rotation;
            baseLinearDamping = Body.linearDamping;
            baseAngularDamping = Body.angularDamping;
            SetPhase(Phase.Resting);
        }

        // ------------------------------------------------------------------------- IInteractable
        public bool CanInteract(PlayerInteractor interactor)
        {
            PhysicsCarrier c = CarrierOf(interactor);
            return c != null && isActiveAndEnabled && (c.Held == null || c.Held == this);
        }

        public string GetPrompt(PlayerInteractor interactor)
        {
            PhysicsCarrier c = CarrierOf(interactor);
            return c != null ? c.PromptFor(this) : null;
        }

        public void Interact(PlayerInteractor interactor)
        {
            PhysicsCarrier c = CarrierOf(interactor);
            if (c != null) c.Use(this);
        }

        public bool RequiresHold(PlayerInteractor interactor)
        {
            PhysicsCarrier c = CarrierOf(interactor);
            return c != null && c.NeedsHold(this);
        }

        // Prompt is read every frame while focused: look the carrier up only when the interactor changes.
        PhysicsCarrier CarrierOf(PlayerInteractor interactor)
        {
            if (interactor != cachedInteractor)
            {
                cachedInteractor = interactor;
                cachedCarrier = interactor != null ? interactor.GetComponent<PhysicsCarrier>() : null;
            }
            return cachedCarrier;
        }

        // ------------------------------------------------------------------------- carrier hooks
        /// <param name="drag">Dragged along the floor: gravity stays on. Lifted: gravity off.</param>
        internal void OnGrabbed(bool drag)
        {
            RecordValidPose();   // it was resting somewhere valid before being moved
            Radius = ComputeRadius();
            RestoreDamping();
            Body.useGravity = drag;
            SetPhase(Phase.Held);
        }

        /// <param name="dragged">A dragged prop brakes hard (high damping) instead of flying on.</param>
        internal void OnReleased(bool dragged)
        {
            Body.useGravity = true;
            wasSleeping = false;
            if (!dragged)
            {
                SetPhase(Phase.Flight);
                return;
            }
            // It never left the floor and moves at walking speed: no continuous detection needed.
            SetPhase(Phase.Settling);
            Body.linearDamping = releaseDamping;
            Body.angularDamping = releaseDamping;
            dampedUntil = Time.time + releaseDampingTime;
        }

        void RestoreDamping()
        {
            if (dampedUntil <= 0f) return;
            dampedUntil = 0f;
            Body.linearDamping = baseLinearDamping;
            Body.angularDamping = baseAngularDamping;
        }

        // ------------------------------------------------------------------------------- physics
        void FixedUpdate()
        {
            if (dampedUntil > 0f && Time.time > dampedUntil) RestoreDamping();
            if (CurrentPhase == Phase.Held) return;

            if (Body.IsSleeping())
            {
                if (!wasSleeping)
                {
                    wasSleeping = true;
                    if (CurrentPhase != Phase.Resting) SetPhase(Phase.Resting);
                    if (Body.position.y > KillPlaneY) RecordValidPose();
                }
                return;
            }
            wasSleeping = false;

            if (Body.position.y < KillPlaneY) Respawn();
        }

        void OnCollisionEnter(Collision collision)
        {
            // First hit ends the flight: the fast part is over, so drop back to cheap discrete detection.
            if (CurrentPhase == Phase.Flight) SetPhase(Phase.Settling);
            if (Time.time < silentUntil) return;

            ContactPoint contact = collision.GetContact(0);
            float speed = Mathf.Abs(Vector3.Dot(collision.relativeVelocity, contact.normal));
            if (speed < minImpactSpeed) return;

            float t = Mathf.InverseLerp(minImpactSpeed, maxImpactSpeed, speed);
            float intensity = Mathf.Lerp(minIntensity, maxIntensity, t);
            // Environment, not Player: agents hear where the object landed, not who threw it (the decoy).
            if (InteractionNoise.Emit(impactNoise, contact.point, intensity, NoiseSource.Environment, gameObject, impactCooldown))
                Impacted?.Invoke(this, intensity);
        }

        /// Puts the body back where it last came to rest, stopped and silent for a moment.
        public void Respawn()
        {
            Body.linearVelocity = Vector3.zero;
            Body.angularVelocity = Vector3.zero;
            Body.position = LastValidPosition;
            Body.rotation = lastValidRotation;
            transform.SetPositionAndRotation(LastValidPosition, lastValidRotation);
            SetPhase(Phase.Resting);
            silentUntil = Time.time + RespawnSilence;
            Respawned?.Invoke(this);
        }

        void RecordValidPose()
        {
            LastValidPosition = Body.position;
            lastValidRotation = Body.rotation;
        }

        void SetPhase(Phase phase)
        {
            CurrentPhase = phase;
            Body.collisionDetectionMode = phase switch
            {
                Phase.Held => CollisionDetectionMode.ContinuousSpeculative,
                Phase.Flight => CollisionDetectionMode.ContinuousDynamic,
                _ => CollisionDetectionMode.Discrete,
            };
            Body.interpolation = phase == Phase.Resting ? RigidbodyInterpolation.None : RigidbodyInterpolation.Interpolate;
        }

        float ComputeRadius()
        {
            Vector3 centre = Body.worldCenterOfMass;
            float r = 0.05f;
            foreach (Collider c in Colliders)
            {
                if (c == null || c.isTrigger || !c.enabled) continue;
                Bounds b = c.bounds;
                r = Mathf.Max(r, Vector3.Distance(centre, b.center) + b.extents.magnitude);
            }
            return r;
        }
    }
}
