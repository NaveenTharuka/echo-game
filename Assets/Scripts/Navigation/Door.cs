using System;
using System.Collections.Generic;
using Echo.Interaction;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.Events;

namespace Echo.Navigation
{
    /// Sliding door (guide §6.4 API, sliding implementation).
    ///
    /// The leaf is a kinematic Rigidbody moved with MovePosition in FixedUpdate: physics-correct contacts,
    /// interpolated rendering, deterministic state for the AI (no HingeJoint: neither the CharacterController
    /// nor NavMeshAgents apply forces, so a jointed door would have nothing to push it).
    /// Before closing, an OverlapBox over the closed leaf volume checks for the player/agents/props; if
    /// blocked the door waits and retries, and a close in progress reverses if something steps in.
    ///
    /// Frozen members read by NavGraph / AgentMotor: sideA, sideB, isOpen, isLocked, SetOpen, OpenDelay.
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(-100)]   // attach to the graph before agents plan in their Update
    public class Door : MonoBehaviour, IInteractable
    {
        public enum State { Closed, Opening, Open, Closing, Locked }

        [Header("Waypoint graph (guide §6.4)")]
        [Tooltip("The two NavNodes this door connects (NavGraph puts the door on that edge).")]
        public NavNode sideA;
        public NavNode sideB;

        [Header("Leaf")]
        [Tooltip("Kinematic, interpolated Rigidbody on the moving leaf (child of this object).")]
        public Rigidbody leaf;
        [Tooltip("Solid collider of the leaf; defines the doorway and the close-safety volume.")]
        public BoxCollider leafCollider;
        [Tooltip("Slide direction in this object's local space: (0,0,1) sideways, (0,1,0) a bay door rising.")]
        public Vector3 slideDirection = Vector3.forward;
        [Tooltip("0 = the leaf's own size along the slide direction (fully clears the doorway).")]
        public float slideDistance;
        [Tooltip("Seconds to fully open or close. Also the time an agent waits after opening (OpenDelay).")]
        public float openDuration = 0.6f;

        [Header("Lock")]
        [SerializeField] bool startLocked = false;
        public UnityEvent onUnlocked = new(), onLocked = new(), onOpened = new(), onClosed = new();

        [Header("Close safety")]
        [Tooltip("Layers checked before closing. Everything until the Player/Agents layers exist; only " +
                 "CharacterControllers and Rigidbodies count as blockers, static geometry is ignored.")]
        public LayerMask blockerMask = ~0;
        public float retryInterval = 0.25f;
        [Tooltip("Extra half-size added to the closed leaf box for the safety check (metres).")]
        public Vector3 safetyPadding = new(0.05f, 0.02f, 0.05f);

        [Header("Noise (guide §19)")]
        public float openIntensity = 0.5f;
        public float closeIntensity = 0.6f;
        public float rattleIntensity = 0.2f;
        public float rattleCooldown = 1f;

        [Header("Status light (optional)")]
        public Renderer statusLight;
        public Color lockedColor = new(0.9f, 0.08f, 0.05f);
        public Color unlockedColor = new(0.1f, 0.85f, 0.25f);

        // ---- frozen API ---------------------------------------------------------------------
        /// True while the door is open or opening (the AI treats it as passable / not muffling).
        public bool isOpen => state == State.Open || state == State.Opening;
        public bool isLocked
        {
            get => state == State.Locked;
            set { if (value) Lock(); else Unlock(); }
        }
        public float OpenDelay => openDuration;

        public void SetOpen(bool open, GameObject who, bool byAgent)
        {
            if (isLocked) return;
            if (open)
            {
                closeRequested = false;
                IsCloseBlocked = false;
                if (state == State.Closed || state == State.Closing) BeginOpening(who, byAgent);
            }
            else if (isOpen)
            {
                closeRequested = true;
                closeBy = who;
                closeByAgent = byAgent;
                nextCloseCheck = 0f;
            }
        }
        // -------------------------------------------------------------------------------------

        public State CurrentState => state;
        /// 0 = closed, 1 = fully open.
        public float OpenAmount => amount;
        /// A close was requested but something is standing in the doorway.
        public bool IsCloseBlocked { get; private set; }
        public float SlideDistance => distance;
        /// Centre of the doorway (the closed leaf's collider centre), in world space.
        public Vector3 DoorwayCenter => transform.TransformPoint(closedLocalCenter);
        /// Where the leaf's Rigidbody sits when fully closed, in world space.
        public Vector3 ClosedLeafPosition => transform.TransformPoint(closedLocalPos);

        /// Raised whenever isOpen or isLocked changes (debug views; NavGraph is notified directly first).
        public static event Action<Door> Changed;

        const string PromptOpen = "Open", PromptClose = "Close", PromptLocked = "Locked", PromptBlocked = "Blocked";
        static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        static readonly Collider[] overlap = new Collider[16];

        State state;
        float amount, distance, nextCloseCheck;
        bool closeRequested, closeByAgent, pendingLock;
        GameObject closeBy;
        Vector3 closedLocalPos, closedLocalCenter, localDir, closedSize;
        Quaternion leafRotation, closedLocalRotation;
        MaterialPropertyBlock block;

        void Awake()
        {
            if (leaf == null) leaf = GetComponentInChildren<Rigidbody>();
            if (leafCollider == null && leaf != null) leafCollider = leaf.GetComponent<BoxCollider>();
            if (leaf == null || leafCollider == null)
            {
                Debug.LogError($"{name}: Door needs a child leaf with a Rigidbody and BoxCollider.", this);
                enabled = false;
                return;
            }

            leaf.isKinematic = true;
            leaf.useGravity = false;
            leaf.interpolation = RigidbodyInterpolation.Interpolate;
            leaf.collisionDetectionMode = CollisionDetectionMode.Discrete;

            localDir = slideDirection.sqrMagnitude > 0f ? slideDirection.normalized : Vector3.forward;
            closedLocalPos = transform.InverseTransformPoint(leaf.position);
            leafRotation = leaf.rotation;
            closedLocalCenter = transform.InverseTransformPoint(leafCollider.transform.TransformPoint(leafCollider.center));
            closedLocalRotation = Quaternion.Inverse(transform.rotation) * leafCollider.transform.rotation;
            closedSize = Vector3.Scale(leafCollider.size, leafCollider.transform.lossyScale);
            distance = slideDistance > 0f ? slideDistance : LeafExtentAlong(transform.TransformDirection(localDir));

            state = startLocked ? State.Locked : State.Closed;
            block = new MaterialPropertyBlock();
            UpdateStatusLight();
        }

        float LeafExtentAlong(Vector3 worldDir)
        {
            Vector3 size = Vector3.Scale(leafCollider.size, leafCollider.transform.lossyScale);
            Vector3 d = Quaternion.Inverse(leafCollider.transform.rotation) * worldDir;
            return Mathf.Abs(d.x) * size.x + Mathf.Abs(d.y) * size.y + Mathf.Abs(d.z) * size.z;
        }

        // ---- IInteractable ------------------------------------------------------------------
        public bool CanInteract(PlayerInteractor interactor) => state != State.Opening && state != State.Closing;

        public string GetPrompt(PlayerInteractor interactor) =>
            state == State.Locked ? PromptLocked
            : IsCloseBlocked ? PromptBlocked
            : isOpen ? PromptClose
            : PromptOpen;

        public void Interact(PlayerInteractor interactor)
        {
            GameObject who = interactor != null ? interactor.gameObject : null;
            if (state == State.Locked)
            {
                InteractionNoise.Emit(NoiseType.Door, DoorwayCenter, rattleIntensity, NoiseSource.Player, gameObject, rattleCooldown);
                return;
            }
            if (closeRequested) { closeRequested = false; IsCloseBlocked = false; return; }   // cancel a blocked close
            SetOpen(!isOpen, who, false);
        }

        // ---- lock (UnityEvent targets) --------------------------------------------------------
        public void Unlock()
        {
            pendingLock = false;
            if (state != State.Locked) return;
            state = State.Closed;
            UpdateStatusLight();
            onUnlocked?.Invoke();
            NotifyChanged();
        }

        /// Locks now if closed, otherwise as soon as it finishes closing.
        public void Lock()
        {
            if (state == State.Locked) return;
            if (state != State.Closed) { pendingLock = true; return; }
            state = State.Locked;
            UpdateStatusLight();
            onLocked?.Invoke();
            NotifyChanged();
        }

        // ---- motion ---------------------------------------------------------------------------
        void BeginOpening(GameObject who, bool byAgent)
        {
            bool wasOpen = isOpen;
            if (state == State.Closed)   // reversing a close makes no new sound
                InteractionNoise.Emit(NoiseType.Door, DoorwayCenter, openIntensity,
                                      byAgent ? NoiseSource.AgentBody : NoiseSource.Player, who);
            state = State.Opening;
            if (!wasOpen) NotifyChanged();
        }

        void BeginClosing()
        {
            closeRequested = false;
            IsCloseBlocked = false;
            state = State.Closing;
            InteractionNoise.Emit(NoiseType.Door, DoorwayCenter, closeIntensity,
                                  closeByAgent ? NoiseSource.AgentBody : NoiseSource.Player, closeBy);
            closeBy = null;
            NotifyChanged();
        }

        void FixedUpdate()
        {
            if (closeRequested && state == State.Open && Time.time >= nextCloseCheck)
            {
                if (IsDoorwayBlocked()) { IsCloseBlocked = true; nextCloseCheck = Time.time + retryInterval; }
                else BeginClosing();
            }

            if (state == State.Closing && IsDoorwayBlocked())
            {
                // Someone stepped in mid-close: back off and retry once they leave.
                state = State.Opening;
                closeRequested = true;
                IsCloseBlocked = true;
                nextCloseCheck = Time.time + retryInterval;
                NotifyChanged();
            }

            if (state != State.Opening && state != State.Closing) return;

            float target = state == State.Opening ? 1f : 0f;
            amount = Mathf.MoveTowards(amount, target, Time.fixedDeltaTime / Mathf.Max(0.01f, openDuration));
            float eased = amount * amount * (3f - 2f * amount);   // smoothstep: soft start and stop
            leaf.MovePosition(transform.TransformPoint(closedLocalPos + localDir * (distance * eased)));
            leaf.MoveRotation(leafRotation);

            if (amount != target) return;
            if (state == State.Opening) { state = State.Open; onOpened?.Invoke(); }
            else
            {
                state = State.Closed;
                onClosed?.Invoke();
                if (pendingLock) Lock();
            }
        }

        /// OverlapBox over the closed leaf volume. Only movers block: CharacterControllers (player)
        /// and Rigidbodies (agents' kinematic bodies, props). Walls/frame and our own colliders are ignored.
        public bool IsDoorwayBlocked()
        {
            Transform t = leafCollider.transform;
            Vector3 half = Vector3.Scale(leafCollider.size, t.lossyScale) * 0.5f + safetyPadding;
            // The leaf only translates, so its current rotation is also its closed rotation.
            int n = Physics.OverlapBoxNonAlloc(DoorwayCenter, half, overlap, t.rotation, blockerMask, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < n; i++)
            {
                Collider c = overlap[i];
                if (c.transform.IsChildOf(transform)) continue;
                if (c is CharacterController || c.attachedRigidbody != null || c.GetComponentInParent<CharacterController>() != null)
                    return true;
            }
            return false;
        }

        /// Open/close and lock/unlock both change the graph (A* cost, sound distances, route caches keyed on Version).
        void NotifyChanged()
        {
            var graph = NavGraph.Instance;
            if (graph != null) graph.OnDoorChanged();
            Changed?.Invoke(this);
        }

        // ---- graph attachment -------------------------------------------------------------------
        // NavGraph.Build puts a door only on its sideA-sideB edge, but Auto-Link also joins node pairs whose straight
        // walk passes through the doorway without touching those nodes (22 such edges in Sandbox_Tracker's one
        // doorway). Every enabled door therefore attaches itself to EVERY edge whose walk crosses its closed-leaf box,
        // at startup and after each rebuild, so all of them respect open / closed / locked (A* cost, sound penalty,
        // AgentMotor opening). "Walk" = the straight segment, else the NavMesh path between the nodes: Auto-Link
        // accepts a pair whose path bends through a doorway (<= 1.25 x straight) although the straight line runs
        // through the wall beside it. A rebuild is detected by NavGraph replacing its adjacency lists.

        static readonly List<Door> enabledDoors = new();
        static NavGraph attachedGraph;
        static IReadOnlyList<Edge> attachedProbe;
        static bool attachDirty;
        static int attachCheckedFrame = -1;
        static bool warnedAdjacency;
        static readonly Vector3[] corners = new Vector3[32];
        static NavMeshPath walk;

        /// Graph edges (both directions) this door gates after the last attach.
        public int AttachedEdges { get; private set; }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            enabledDoors.Clear(); attachedGraph = null; attachedProbe = null;
            attachDirty = false; attachCheckedFrame = -1; warnedAdjacency = false;
        }

        void OnEnable() { if (!enabledDoors.Contains(this)) enabledDoors.Add(this); attachDirty = true; }
        void OnDisable() { enabledDoors.Remove(this); attachDirty = true; }

        void Update()
        {
            if (attachCheckedFrame == Time.frameCount) return;   // once per frame for all doors
            attachCheckedFrame = Time.frameCount;
            EnsureAttached();
        }

        /// Re-attaches all enabled doors if the graph was (re)built or a door appeared/disappeared since the last attach.
        public static void EnsureAttached()
        {
            var graph = NavGraph.Instance;
            if (graph == null || graph.Count == 0) return;
            if (!attachDirty && graph == attachedGraph && ReferenceEquals(graph.Neighbours(0), attachedProbe)) return;
            attachDirty = false; attachedGraph = graph; attachedProbe = graph.Neighbours(0);
            AttachAll(graph);
        }

        static void AttachAll(NavGraph graph)
        {
            foreach (var d in enabledDoors) d.AttachedEdges = 0;
            bool changed = false;
            for (int i = 0; i < graph.Count; i++)
            {
                if (!(graph.Neighbours(i) is List<Edge> edges))
                {
                    if (!warnedAdjacency) Debug.LogError("[Door] NavGraph adjacency is not a List<Edge>: doors gate only their sideA-sideB edge.");
                    warnedAdjacency = true;
                    return;
                }
                Vector3 a = graph.Position(i);
                for (int k = 0; k < edges.Count; k++)
                {
                    var e = edges[k];
                    Door gate = FindGate(a, graph.Position(e.To));
                    if (gate == null)
                    {
                        // A gate left by a door that is now disabled would keep its stale state: drop it.
                        if (e.Door != null && !e.Door.isActiveAndEnabled) { edges[k] = new Edge(e.To, e.Length, null); changed = true; }
                        continue;
                    }
                    gate.AttachedEdges++;
                    if (e.Door == gate) continue;                   // Build already put it there (sideA-sideB)
                    edges[k] = new Edge(e.To, e.Length, gate);
                    changed = true;
                }
            }
            if (changed) graph.OnDoorChanged();                     // sound distances (closed-door penalty) + Version
        }

        /// The enabled door whose doorway the walk a -> b passes through, or null. The NavMesh path is only computed when
        /// the straight segment misses every door (once per edge per rebuild, not per frame).
        static Door FindGate(Vector3 a, Vector3 b)
        {
            foreach (var d in enabledDoors)
                if (d.isActiveAndEnabled && d.leafCollider != null && d.Crosses(a, b)) return d;
            if (walk == null) walk = new NavMeshPath();
            if (!NavMesh.SamplePosition(a, out var ha, 1.5f, NavMesh.AllAreas) ||
                !NavMesh.SamplePosition(b, out var hb, 1.5f, NavMesh.AllAreas) ||
                !NavMesh.CalculatePath(ha.position, hb.position, NavMesh.AllAreas, walk)) return null;
            int n = walk.GetCornersNonAlloc(corners);
            for (int c = 1; c < n; c++)
                foreach (var d in enabledDoors)
                    if (d.isActiveAndEnabled && d.leafCollider != null && d.Crosses(corners[c - 1], corners[c])) return d;
            return null;
        }

        /// True if the walk a -> b (raised to body height) passes through the closed leaf's box, padded so a walk that
        /// grazes the jamb still counts (beside the doorway the wall blocks the walk anyway).
        bool Crosses(Vector3 a, Vector3 b)
        {
            const float BodyHeight = 1f, Pad = 0.25f;
            Quaternion inv = Quaternion.Inverse(transform.rotation * closedLocalRotation);
            Vector3 c = DoorwayCenter, up = Vector3.up * BodyHeight;
            Vector3 p = inv * (a + up - c), q = inv * (b + up - c), dir = q - p;
            Vector3 half = closedSize * 0.5f + Vector3.one * Pad;
            float t0 = 0f, t1 = 1f;
            for (int ax = 0; ax < 3; ax++)                          // slab test, segment vs box
            {
                if (Mathf.Abs(dir[ax]) < 1e-6f) { if (Mathf.Abs(p[ax]) > half[ax]) return false; continue; }
                float u = (-half[ax] - p[ax]) / dir[ax], v = (half[ax] - p[ax]) / dir[ax];
                if (u > v) (u, v) = (v, u);
                t0 = Mathf.Max(t0, u); t1 = Mathf.Min(t1, v);
                if (t0 > t1) return false;
            }
            return true;
        }

        void UpdateStatusLight()
        {
            if (statusLight == null) return;
            block ??= new MaterialPropertyBlock();
            block.SetColor(BaseColorId, state == State.Locked ? lockedColor : unlockedColor);
            statusLight.SetPropertyBlock(block);
        }

#if UNITY_EDITOR
        void Reset()
        {
            leaf = GetComponentInChildren<Rigidbody>();
            if (leaf != null) leafCollider = leaf.GetComponent<BoxCollider>();
        }

        void OnDrawGizmosSelected()
        {
            if (leafCollider == null) return;
            Transform t = leafCollider.transform;
            Vector3 centre = Application.isPlaying ? DoorwayCenter : t.TransformPoint(leafCollider.center);
            Vector3 size = Vector3.Scale(leafCollider.size, t.lossyScale) + safetyPadding * 2f;
            Gizmos.color = IsCloseBlocked ? Color.red : new Color(1f, 0.6f, 0f);
            Gizmos.matrix = Matrix4x4.TRS(centre, t.rotation, Vector3.one);
            Gizmos.DrawWireCube(Vector3.zero, size);
            Gizmos.matrix = Matrix4x4.identity;
            Vector3 dir = transform.TransformDirection(slideDirection.normalized);
            float d = Application.isPlaying ? distance : (slideDistance > 0f ? slideDistance : 1f);
            Gizmos.DrawLine(centre, centre + dir * d);
        }
#endif
    }
}
