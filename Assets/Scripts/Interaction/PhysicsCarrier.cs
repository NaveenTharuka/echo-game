using System;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Echo.Interaction
{
    /// Lets the player pick up, carry, drop and throw Carryable props. Lives on the player next to
    /// PlayerInteractor (which supplies the aim and the Interact key).
    ///
    /// What happens to a prop is chosen by mass (ModeFor): up to liftMassLimit it is lifted; heavier props
    /// show "Too heavy". Each mode has its own prompt and FixedUpdate step, so a new mode (drag) is one more
    /// enum value + step, not a rewrite.
    ///
    /// Hold: velocity-follow. Every physics step the body's velocity is set to carry it to the hold point
    /// (player velocity + offset * followGain, clamped), so it stays a real dynamic body: it collides with the
    /// world and is pushed away by walls instead of passing through them. Gravity is off while held, and the
    /// body ignores the player's capsule (no pushing yourself, no standing on what you carry). If it is held
    /// back more than dropDistance from the hold point for snagTime without a break (snagged behind a wall),
    /// it is dropped; a fast turn moves the hold point that far only briefly, so it does not drop the prop.
    [DisallowMultipleComponent]
    [RequireComponent(typeof(PlayerInteractor))]
    public class PhysicsCarrier : MonoBehaviour
    {
        public const string ThrowActionName = "Throw";

        public enum Mode { None, Lift, TooHeavy }

        [Header("Lift")]
        [Tooltip("kg. Heavier props can't be picked up.")]
        public float liftMassLimit = 5f;
        [Tooltip("Gap (m) between the camera and the near side of the held prop.")]
        public float holdGap = 0.9f;
        [Tooltip("1/s. Velocity towards the hold point = offset * gain (on top of the player's velocity).")]
        public float followGain = 15f;
        [Tooltip("m/s. Caps the follow velocity so a held prop can't be swung through thin geometry.")]
        public float maxFollowSpeed = 10f;
        [Tooltip("1/s. How quickly the prop turns to keep its grab orientation relative to the player.")]
        public float rotationGain = 10f;
        [Tooltip("m. Held further than this from the hold point (snagged) -> dropped.")]
        public float dropDistance = 1.5f;
        [Tooltip("s. Dropped only after staying beyond dropDistance this long without a break (fast turns don't drop it).")]
        public float snagTime = 0.25f;
        [Tooltip("s. Time a just-grabbed prop gets to reach the hold point before the drop distance applies.")]
        public float reachTime = 0.6f;

        [Header("Throw")]
        public float minThrowSpeed = 4f;
        public float maxThrowSpeed = 12f;
        [Tooltip("s. Holding Throw this long gives the full speed.")]
        public float chargeTime = 0.8f;
        [Tooltip("m. The prop is drawn back this far at full charge (visible wind-up).")]
        public float chargePullBack = 0.25f;

        public Carryable Held { get; private set; }
        public Mode HeldMode { get; private set; }
        public bool IsCharging { get; private set; }
        public float Charge01 => IsCharging ? Mathf.Clamp01((Time.time - chargeStart) / chargeTime) : 0f;

        public event Action<Carryable> PickedUp;
        /// (prop, launch speed relative to the player; 0 = dropped)
        public event Action<Carryable, float> Released;

        const string PickUpPrompt = "Pick up";
        const string TooHeavyPrompt = "Too heavy";
        string dropPrompt = "Drop";

        PlayerInteractor interactor;
        CharacterController body;
        InputAction throwAction;
        Quaternion grabRotation;   // prop rotation relative to the player's yaw
        float grabTime, chargeStart, tooFarSince;
        bool reached;
        Carryable clearing;        // released prop still ignoring the player until it is clear of the capsule

        void Awake()
        {
            interactor = GetComponent<PlayerInteractor>();
            body = GetComponent<CharacterController>();
            if (interactor.inputActions != null)
            {
                throwAction = interactor.inputActions.FindActionMap(PlayerInteractor.MapName, true).FindAction(ThrowActionName, true);
                string hint = throwAction.GetBindingDisplayString(0);
                dropPrompt = $"Drop    [{(string.IsNullOrEmpty(hint) ? "RMB" : hint)}]  Throw";
            }
        }

        void OnEnable()
        {
            if (throwAction == null) return;
            throwAction.started += OnThrowStarted;
            throwAction.canceled += OnThrowReleased;
            throwAction.actionMap.Enable();
        }

        void OnDisable()
        {
            if (throwAction != null)
            {
                throwAction.started -= OnThrowStarted;
                throwAction.canceled -= OnThrowReleased;
            }
            Drop();
            FinishClearing();
        }

        void OnThrowStarted(InputAction.CallbackContext _)
        {
            if (Held == null || HeldMode != Mode.Lift) return;
            IsCharging = true;
            chargeStart = Time.time;
        }

        void OnThrowReleased(InputAction.CallbackContext _)
        {
            if (IsCharging) Throw(Charge01);
        }

        // ------------------------------------------------------------------------ mode by mass
        public Mode ModeFor(Carryable c)
        {
            if (c == null || c.Body == null || c.Body.isKinematic) return Mode.None;
            return c.Body.mass <= liftMassLimit ? Mode.Lift : Mode.TooHeavy;
        }

        /// Prompt verb for a focused prop (cached strings: read every frame).
        public string PromptFor(Carryable c)
        {
            if (c == Held) return dropPrompt;
            return ModeFor(c) switch
            {
                Mode.Lift => PickUpPrompt,
                Mode.TooHeavy => TooHeavyPrompt,
                _ => null,
            };
        }

        /// Interact on a prop: drop it if held, otherwise pick it up if its mode allows.
        public void Use(Carryable c)
        {
            if (c == Held) { Drop(); return; }
            Mode mode = ModeFor(c);
            if (mode == Mode.Lift) Grab(c, mode);
            // TooHeavy: the prompt already says so; nothing happens.
        }

        // ------------------------------------------------------------------------- grab/release
        void Grab(Carryable c, Mode mode)
        {
            Drop();
            if (c == clearing) clearing = null;   // still ignoring the player: keep it that way
            else FinishClearing();

            Held = c;
            HeldMode = mode;
            grabRotation = Quaternion.Inverse(Yaw) * c.Body.rotation;
            grabTime = tooFarSince = Time.time;
            reached = false;
            c.OnGrabbed();
            IgnorePlayer(c, true);
            interactor.FocusOverride = c;   // hands full: E means "drop", the prompt stays on the held prop
            PickedUp?.Invoke(c);
        }

        /// Lets go of the held prop (keeps its current velocity). Safe to call when holding nothing.
        public void Drop() => Release(0f);

        /// Throws the held prop along the view at minThrowSpeed..maxThrowSpeed (by charge) plus the player's velocity.
        public void Throw(float charge01)
        {
            if (Held == null || HeldMode != Mode.Lift) { IsCharging = false; return; }
            Release(Mathf.Lerp(minThrowSpeed, maxThrowSpeed, Mathf.Clamp01(charge01)));
        }

        void Release(float launchSpeed)
        {
            IsCharging = false;
            Carryable c = Held;
            if (ReferenceEquals(c, null)) return;
            Held = null;
            HeldMode = Mode.None;
            interactor.FocusOverride = null;
            if (c == null) return;   // destroyed while held

            c.OnReleased();
            if (launchSpeed > 0f)
            {
                c.Body.linearVelocity = PlayerVelocity;
                c.Body.angularVelocity *= 0.25f;
                c.Body.AddForce(View.forward * launchSpeed, ForceMode.VelocityChange);
            }
            // Keep ignoring the player until the prop is clear of the capsule, or the solver would shove it out.
            FinishClearing();
            clearing = c;
            Released?.Invoke(c, launchSpeed);
        }

        // ------------------------------------------------------------------------------- physics
        void FixedUpdate()
        {
            if (Held == null) { UpdateClearing(); return; }
            if (!Held.isActiveAndEnabled || Held.Body.isKinematic) { Drop(); return; }

            switch (HeldMode)
            {
                case Mode.Lift: LiftStep(); break;
            }
        }

        void LiftStep()
        {
            Rigidbody rb = Held.Body;
            Vector3 offset = HoldPoint - rb.worldCenterOfMass;
            float distance = offset.magnitude;

            if (!reached) reached = distance < dropDistance * 0.5f;
            // Snagged = too far for snagTime without a break. A fast mouse flick is too far for a few steps only.
            if ((reached || Time.time - grabTime > reachTime) && distance > dropDistance)
            {
                if (Time.time - tooFarSince > snagTime) { Drop(); return; }
            }
            else tooFarSince = Time.time;

            rb.linearVelocity = Vector3.ClampMagnitude(PlayerVelocity + offset * followGain, maxFollowSpeed);

            // Turn towards the grab orientation (relative to the player's yaw) - shortest arc, as a velocity.
            Quaternion delta = Yaw * grabRotation * Quaternion.Inverse(rb.rotation);
            delta.ToAngleAxis(out float angle, out Vector3 axis);
            if (angle > 180f) angle -= 360f;
            rb.angularVelocity = Mathf.Abs(angle) > 0.1f && !float.IsInfinity(axis.x)
                ? axis * (angle * Mathf.Deg2Rad * rotationGain)
                : Vector3.zero;
        }

        /// World point the held prop's centre of mass is pulled to.
        public Vector3 HoldPoint
        {
            get
            {
                float reach = holdGap + (Held != null ? Held.Radius : 0f) - Charge01 * chargePullBack;
                return View.position + View.forward * reach;
            }
        }

        Transform View => interactor.viewPoint != null ? interactor.viewPoint : transform;
        Quaternion Yaw => Quaternion.Euler(0f, View.eulerAngles.y, 0f);
        Vector3 PlayerVelocity => body != null ? body.velocity : Vector3.zero;

        // ------------------------------------------------------------------- player collision
        void IgnorePlayer(Carryable c, bool ignore)
        {
            if (body == null) return;
            foreach (Collider col in c.Colliders)
                if (col != null) Physics.IgnoreCollision(col, body, ignore);
        }

        void UpdateClearing()
        {
            if (clearing == null) return;
            Bounds player = body != null ? body.bounds : default;
            foreach (Collider col in clearing.Colliders)
                if (col != null && col.enabled && col.bounds.Intersects(player)) return;
            FinishClearing();
        }

        void FinishClearing()
        {
            if (clearing != null) IgnorePlayer(clearing, false);
            clearing = null;
        }

        void OnDrawGizmosSelected()
        {
            if (Held == null) return;
            Gizmos.color = Color.cyan;
            Gizmos.DrawWireSphere(HoldPoint, 0.05f);
            Gizmos.DrawLine(HoldPoint, Held.Body.worldCenterOfMass);
        }
    }
}
