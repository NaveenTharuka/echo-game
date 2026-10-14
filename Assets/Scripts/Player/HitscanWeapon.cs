using System;
using Echo.Interaction;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Echo.Player
{
    /// Minimal hitscan gun on the Fire action (guide §6.2). One raycast from the camera per shot: Gunshot 1.0 at the
    /// muzzle (Player source), BulletImpact 0.3 at the hit point (Environment source - a second, quieter sound agents
    /// may investigate), and an impulse at the hit point on any dynamic Rigidbody. Muzzle flash = a point light and a
    /// bright sphere for 0.05 s; impact = a small mark (pooled, no allocations per shot) with a brief spark.
    /// Anything hit that implements IDamageable (agents) takes `damage`, from the camera position (guide §6.2).
    /// No firing while carrying or dragging (hands full).
    ///
    /// Gun model: assign a prefab to `modelPrefab` - that is the whole swap. It is spawned under the camera at
    /// `modelOffset`; a child named "Muzzle" marks the barrel tip (else the model root). Empty -> a primitive
    /// placeholder is built at runtime (the project has no gun model yet).
    [DisallowMultipleComponent]
    [RequireComponent(typeof(PlayerInteractor))]
    public class HitscanWeapon : MonoBehaviour
    {
        public const string FireActionName = "Fire";

        public float range = 100f;
        [Tooltip("s between shots.")]
        public float fireInterval = 0.3f;
        [Tooltip("Damage per hit to an IDamageable (agents: 100 HP -> 4 hits).")]
        public float damage = 25f;
        [Tooltip("N·s impulse on a dynamic Rigidbody that is hit (free 2 kg body -> 3 m/s).")]
        public float impactImpulse = 6f;
        [Tooltip("What the shot can hit. Triggers are always ignored.")]
        public LayerMask hitMask = ~0;
        public float gunshotIntensity = 1f;
        public float impactIntensity = 0.3f;

        [Header("Visuals")]
        [Tooltip("Gun model prefab, spawned under the camera. Empty -> a primitive placeholder is built.")]
        public GameObject modelPrefab;
        [Tooltip("Model position in camera space.")]
        public Vector3 modelOffset = new(0.22f, -0.2f, 0.45f);
        public float flashSeconds = 0.05f;
        public Color flashColor = new(1f, 0.75f, 0.35f);
        public float markSeconds = 4f;
        public int markPoolSize = 16;

        public int ShotsFired { get; private set; }
        /// The spawned gun model (or placeholder) and its barrel tip.
        public Transform Model { get; private set; }
        public Transform Muzzle { get; private set; }
        /// (hit something, the hit). Raised after every shot.
        public event Action<bool, RaycastHit> Fired;

        static readonly int BaseColor = Shader.PropertyToID("_BaseColor");
        const float SparkSeconds = 0.08f;

        PlayerInteractor interactor;
        PhysicsCarrier carrier;
        InputAction fire;
        Light flashLight;
        Renderer flashMesh;
        float nextShot, flashUntil;
        Transform[] marks;
        GameObject[] sparks;
        float[] markUntil, sparkUntil;
        int nextMark;

        void Awake()
        {
            interactor = GetComponent<PlayerInteractor>();
            carrier = GetComponent<PhysicsCarrier>();
            if (interactor.inputActions != null)
                fire = interactor.inputActions.FindActionMap(PlayerInteractor.MapName, true).FindAction(FireActionName, true);
            BuildModel();
            BuildFlash();
            BuildMarkPool();
        }

        void OnEnable()
        {
            if (fire == null) return;
            fire.performed += OnFire;
            fire.actionMap.Enable();
        }

        void OnDisable()
        {
            if (fire != null) fire.performed -= OnFire;
            if (flashLight != null) SetFlash(false);
        }

        void OnFire(InputAction.CallbackContext _) => TryFire();

        /// False while carrying/dragging, between shots, or when disabled (dead).
        public bool CanFire => isActiveAndEnabled && Time.time >= nextShot && (carrier == null || carrier.Held == null);

        public bool TryFire()
        {
            if (!CanFire) return false;
            nextShot = Time.time + fireInterval;
            ShotsFired++;

            Transform view = interactor.viewPoint != null ? interactor.viewPoint : transform;
            Vector3 muzzlePos = Muzzle != null ? Muzzle.position : view.position;
            InteractionNoise.Emit(NoiseType.Gunshot, muzzlePos, gunshotIntensity, NoiseSource.Player, gameObject);
            SetFlash(true);

            // From the camera, not the muzzle: the shot goes where the crosshair is. The ray starts inside the
            // player's capsule, and a raycast never reports the collider it starts in.
            bool hitSomething = Physics.Raycast(view.position, view.forward, out RaycastHit hit, range, hitMask, QueryTriggerInteraction.Ignore);
            if (hitSomething)
            {
                Rigidbody rb = hit.rigidbody;
                if (rb != null && !rb.isKinematic)
                    rb.AddForceAtPosition(view.forward * impactImpulse, hit.point, ForceMode.Impulse);
                IDamageable target = hit.collider.GetComponentInParent<IDamageable>();   // agent colliders may be on a child
                target?.TakeDamage(damage, view.position);
                InteractionNoise.Emit(NoiseType.BulletImpact, hit.point, impactIntensity, NoiseSource.Environment);
                PlaceMark(hit);
            }
            Fired?.Invoke(hitSomething, hit);
            return true;
        }

        void Update()
        {
            if (flashUntil > 0f && Time.time >= flashUntil) SetFlash(false);
            for (int i = 0; i < marks.Length; i++)
            {
                if (sparkUntil[i] > 0f && Time.time >= sparkUntil[i]) { sparkUntil[i] = 0f; sparks[i].SetActive(false); }
                if (markUntil[i] > 0f && Time.time >= markUntil[i]) { markUntil[i] = 0f; marks[i].gameObject.SetActive(false); }
            }
        }

        // ---------------------------------------------------------------------------------- visuals
        void SetFlash(bool on)
        {
            flashLight.enabled = on;
            flashMesh.enabled = on;
            flashUntil = on ? Time.time + flashSeconds : 0f;
        }

        void PlaceMark(RaycastHit hit)
        {
            int i = nextMark;
            nextMark = (nextMark + 1) % marks.Length;
            marks[i].SetPositionAndRotation(hit.point + hit.normal * 0.005f, Quaternion.LookRotation(hit.normal));
            marks[i].gameObject.SetActive(true);
            sparks[i].SetActive(true);
            markUntil[i] = Time.time + markSeconds;
            sparkUntil[i] = Time.time + SparkSeconds;
        }

        void BuildModel()
        {
            Transform view = interactor.viewPoint != null ? interactor.viewPoint : transform;
            if (modelPrefab != null)
            {
                Model = Instantiate(modelPrefab, view, false).transform;
                Model.localPosition = modelOffset;
                foreach (Collider c in Model.GetComponentsInChildren<Collider>()) DestroyImmediate(c);   // never shoot yourself
                Transform tip = Model.Find("Muzzle");
                Muzzle = tip != null ? tip : Model;
                return;
            }
            BuildPlaceholder(view);
        }

        void BuildPlaceholder(Transform view)
        {
            Transform model = new GameObject("WeaponPlaceholder").transform;
            Model = model;
            model.SetParent(view, false);
            model.localPosition = modelOffset;
            Part(PrimitiveType.Cube, model, new Vector3(0f, 0f, 0f), new Vector3(0.05f, 0.09f, 0.2f), new Color(0.12f, 0.12f, 0.13f));
            Part(PrimitiveType.Cube, model, new Vector3(0f, -0.08f, -0.06f), new Vector3(0.04f, 0.1f, 0.05f), new Color(0.08f, 0.08f, 0.08f));
            Transform barrel = Part(PrimitiveType.Cylinder, model, new Vector3(0f, 0.025f, 0.15f), new Vector3(0.025f, 0.06f, 0.025f), new Color(0.2f, 0.2f, 0.22f));
            barrel.localRotation = Quaternion.Euler(90f, 0f, 0f);
            Muzzle = new GameObject("Muzzle").transform;
            Muzzle.SetParent(model, false);
            Muzzle.localPosition = new Vector3(0f, 0.025f, 0.22f);
        }

        void BuildFlash()
        {
            Transform at = Muzzle;
            var lightGo = new GameObject("MuzzleFlashLight");
            lightGo.transform.SetParent(at, false);
            flashLight = lightGo.AddComponent<Light>();
            flashLight.type = LightType.Point;
            flashLight.range = 4f;
            flashLight.intensity = 3f;
            flashLight.color = flashColor;
            flashLight.shadows = LightShadows.None;
            flashMesh = Part(PrimitiveType.Sphere, at, Vector3.zero, Vector3.one * 0.06f, flashColor * 2f).GetComponent<Renderer>();
            SetFlash(false);
        }

        void BuildMarkPool()
        {
            var pool = new GameObject("ImpactMarks (runtime)").transform;   // world space: marks stay where they hit
            marks = new Transform[markPoolSize];
            sparks = new GameObject[markPoolSize];
            markUntil = new float[markPoolSize];
            sparkUntil = new float[markPoolSize];
            for (int i = 0; i < markPoolSize; i++)
            {
                marks[i] = Part(PrimitiveType.Sphere, pool, Vector3.zero, new Vector3(0.035f, 0.035f, 0.01f), new Color(0.03f, 0.03f, 0.03f));
                sparks[i] = Part(PrimitiveType.Sphere, marks[i], new Vector3(0f, 0f, 2f), new Vector3(2.5f, 2.5f, 6f), flashColor * 2f).gameObject;
                marks[i].gameObject.SetActive(false);
            }
        }

        /// A collider-less, shadow-less primitive tinted with a property block (no new materials).
        static Transform Part(PrimitiveType type, Transform parent, Vector3 localPos, Vector3 scale, Color color)
        {
            var go = GameObject.CreatePrimitive(type);
            DestroyImmediate(go.GetComponent<Collider>());   // now, not end of frame: a shot this frame must not hit it
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            go.transform.localScale = scale;
            var r = go.GetComponent<Renderer>();
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            var block = new MaterialPropertyBlock();
            block.SetColor(BaseColor, color);
            r.SetPropertyBlock(block);
            return go.transform;
        }
    }
}
