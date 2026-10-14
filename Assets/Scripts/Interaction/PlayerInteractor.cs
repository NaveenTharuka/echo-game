using System;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Echo.Interaction
{
    /// Finds the IInteractable under the crosshair and triggers it on the Interact action.
    /// One SphereCast per frame, no allocations; the GetComponentInParent lookup only runs
    /// when the collider under the crosshair changes.
    [DisallowMultipleComponent]
    public class PlayerInteractor : MonoBehaviour
    {
        public const string MapName = "Interaction";
        public const string InteractActionName = "Interact";

        [Tooltip("Origin and direction of the interaction cast. Defaults to Camera.main.")]
        public Transform viewPoint;
        public float range = 2.5f;
        [Tooltip("A thin sphere instead of a ray, so small objects are easy to target.")]
        public float castRadius = 0.1f;
        [Tooltip("Layers that hold interactables.")]
        public LayerMask interactableMask;
        [Tooltip("Layers that hide an interactable behind them (walls, floors, props).")]
        public LayerMask occluderMask = ~0;
        [Tooltip("EchoInteraction.inputactions")]
        public InputActionAsset inputActions;

        /// Current target, or null. Always re-validated each frame.
        public IInteractable Focus { get; private set; }
        public RaycastHit FocusHit { get; private set; }
        /// Key label of the Interact binding, e.g. "E".
        public string InteractKeyHint { get; private set; } = "E";

        /// While set (e.g. a prop being carried), this is the focus and no cast is made.
        public IInteractable FocusOverride { get; set; }

        public event Action<IInteractable> FocusChanged;
        public event Action<IInteractable> Interacted;

        InputAction interact;
        Collider lastCollider;
        IInteractable lastCandidate;

        void Awake()
        {
            if (viewPoint == null && Camera.main != null) viewPoint = Camera.main.transform;
            if (inputActions != null)
            {
                interact = inputActions.FindActionMap(MapName, true).FindAction(InteractActionName, true);
                string hint = interact.GetBindingDisplayString(0);
                if (!string.IsNullOrEmpty(hint)) InteractKeyHint = hint;
            }
            else Debug.LogWarning($"{nameof(PlayerInteractor)}: no input actions assigned; only TryInteract() will work.", this);
        }

        void OnEnable()
        {
            if (interact == null) return;
            interact.performed += OnInteractPerformed;
            interact.actionMap.Enable();
        }

        void OnDisable()
        {
            if (interact != null) interact.performed -= OnInteractPerformed;
            ClearFocus();
        }

        void Update() => RefreshFocus();

        void OnInteractPerformed(InputAction.CallbackContext _) => TryInteract();

        /// Interacts with the current focus. Returns false if there is nothing usable.
        public bool TryInteract()
        {
            if (!IsAlive(Focus) || !Focus.CanInteract(this)) return false;
            Focus.Interact(this);
            Interacted?.Invoke(Focus);
            return true;
        }

        /// Re-runs the focus cast immediately (normally done in Update).
        public void RefreshFocus()
        {
            if (viewPoint == null) { ClearFocus(); return; }
            if (IsAlive(FocusOverride))
            {
                lastCollider = null;
                lastCandidate = null;
                SetFocus(FocusOverride.CanInteract(this) ? FocusOverride : null);
                return;
            }

            // Cast against interactables AND occluders, and accept only an interactable hit,
            // so you cannot use a door through a wall. Triggers (zones) are ignored.
            Vector3 origin = viewPoint.position, dir = viewPoint.forward;
            int mask = interactableMask.value | occluderMask.value;
            bool found = Physics.SphereCast(origin, castRadius, dir, out RaycastHit hit, range, mask, QueryTriggerInteraction.Ignore);

            // Use volumes: trigger colliders ON the interactable layers mark a space you can use, such as
            // an open doorway whose leaf has slid into the wall. Accepted only if nothing solid is closer.
            if (Physics.SphereCast(origin, castRadius, dir, out RaycastHit use, range, interactableMask.value, QueryTriggerInteraction.Collide)
                && use.collider.isTrigger && (!found || use.distance < hit.distance))
            {
                hit = use;
                found = true;
            }

            if (!found || (interactableMask.value & (1 << hit.collider.gameObject.layer)) == 0)
            {
                ClearFocus();
                return;
            }

            if (hit.collider != lastCollider)
            {
                lastCollider = hit.collider;
                lastCandidate = hit.collider.GetComponentInParent<IInteractable>();
            }

            FocusHit = hit;
            SetFocus(IsAlive(lastCandidate) && lastCandidate.CanInteract(this) ? lastCandidate : null);
        }

        void ClearFocus()
        {
            lastCollider = null;
            lastCandidate = null;
            SetFocus(null);
        }

        void SetFocus(IInteractable target)
        {
            if (ReferenceEquals(target, Focus)) return;
            Focus = target;
            FocusChanged?.Invoke(target);
        }

        /// Interface references bypass Unity's destroyed-object null check, so check it explicitly.
        static bool IsAlive(IInteractable i) => i is UnityEngine.Object o ? o != null : i != null;

        void OnDrawGizmosSelected()
        {
            if (viewPoint == null) return;
            Gizmos.color = Focus != null ? Color.green : Color.yellow;
            Gizmos.DrawLine(viewPoint.position, viewPoint.position + viewPoint.forward * range);
            Gizmos.DrawWireSphere(viewPoint.position + viewPoint.forward * range, castRadius);
        }
    }
}
