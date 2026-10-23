using Echo.Interaction;
using Echo.Sound;
using StarterAssets;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;

namespace Echo.Player
{
    /// The player's torch: off when a level starts, L switches it on and off (with a click the player hears; the agents
    /// are blind, so the light and the click mean nothing to them). Drives the existing light at
    /// PlayerCameraRoot/Main Camera/Flashlight; creates one with the SampleScene settings if a scene has none.
    /// Added to the player automatically on every scene load, so no scene needs editing.
    ///
    /// Beam look (realLook): a procedural cookie gives the shape of a real torch - hot centre, faint reflector ring,
    /// softer body, dim wide spill, slight unevenness - in a warm white. Like an eye adapting, the beam dims when it
    /// is pressed against a nearby surface instead of blowing it out (a 70-intensity spot 30 cm from a wall glares).
    /// When something hunts close by (PlayerCameraMotion.Fear) the torch stutters now and then.
    [DisallowMultipleComponent]
    public class PlayerFlashlight : MonoBehaviour
    {
        /// Set false to manage the torch by hand (e.g. a cutscene scene).
        public static bool AutoCreate = true;
        static bool hintShown;

        public Light torch;
        public bool startOn = false;
        public string toggleBinding = "<Keyboard>/l";
        [Tooltip("Click volume (audio only, never a SoundEvent for the AI).")]
        public float clickVolume = 0.12f;
        public bool showHint = true;
        public float hintDelay = 1.5f;

        [Header("Beam look")]
        public bool realLook = true;
        public Color beamColor = new(1f, 0.94f, 0.84f);
        [Tooltip("Brightness of the hotspot at a normal distance.")]
        public float beamIntensity = 150f;
        public float beamRange = 24f;
        [Tooltip("Outer cone (deg). The cookie shapes the hotspot, ring and spill inside it.")]
        public float beamAngle = 72f;
        [Tooltip("Closer than this (m) the beam dims, so a wall in your face is lit, not blown out.")]
        public float comfortDistance = 4.5f;
        [Range(0.01f, 1f)] public float minCloseFactor = 0.04f;
        [Tooltip("Stutter when a hunting agent is close (needs PlayerCameraMotion).")]
        public bool fearFlicker = true;

        InputAction toggle;
        public bool IsOn => torch != null && torch.enabled;

        static Texture2D cookie;
        PlayerCameraMotion body;
        float closeFactor = 1f, flickerUntil, nextFlickerCheck;
        readonly RaycastHit[] hits = new RaycastHit[8];

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
            // FirstPersonController: SampleScene's player has no PlayerNoise yet, every player has the controller.
            var player = FindFirstObjectByType<FirstPersonController>();
            if (player != null && !player.TryGetComponent(out PlayerFlashlight _))
                player.gameObject.AddComponent<PlayerFlashlight>();
        }

        // ------------------------------------------------------------------ lifecycle

        void Awake()
        {
            if (torch == null) torch = FindTorch();
            if (torch != null) torch.enabled = startOn;
            if (torch != null && realLook) ApplyBeamLook(torch);
            toggle = new InputAction("ToggleFlashlight", InputActionType.Button, toggleBinding);
            toggle.performed += _ => Toggle();
        }

        void OnEnable() => toggle.Enable();
        void OnDisable() => toggle.Disable();
        void OnDestroy() => toggle.Dispose();

        void Start()
        {
            TryGetComponent(out body);
            if (torch != null && realLook) AssignCookie(torch);
            if (showHint && !hintShown) Invoke(nameof(ShowHint), hintDelay);
        }

        void ShowHint()
        {
            hintShown = true;
            HudMessage.Show("[L]  Flashlight", 4f);
        }

        // ------------------------------------------------------------------ beam

        void ApplyBeamLook(Light l)
        {
            l.type = LightType.Spot;
            l.color = beamColor;
            l.range = beamRange;
            l.spotAngle = beamAngle;
            l.innerSpotAngle = beamAngle - 2f;     // the cookie does the falloff, not the cone
            AssignCookie(l);
            l.shadows = LightShadows.Soft;
            l.shadowNearPlane = 0.1f;
            l.shadowBias = 0.03f; l.shadowNormalBias = 0.3f;
            l.intensity = beamIntensity;
        }

        // The cookie atlas is not ready while the scene is still loading: assigned on Awake it can render black,
        // so it is (re)assigned on Start and whenever the torch is switched on.
        void AssignCookie(Light l)
        {
            if (cookie == null) cookie = BuildCookie(256);
            l.cookie = null;
            l.cookie = cookie;
        }

        void LateUpdate()
        {
            if (!realLook || torch == null || !torch.enabled) return;
            float dt = Time.deltaTime;

            // Eye adaptation: brightness of a spot falls with distance squared, so scale it back up to "comfortable".
            Transform t = torch.transform;
            float d = beamRange;
            int n = Physics.SphereCastNonAlloc(t.position, 0.08f, t.forward, hits, comfortDistance, ~0, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < n; i++)
                if (!hits[i].transform.IsChildOf(transform) && hits[i].distance > 0f) d = Mathf.Min(d, hits[i].distance);
            float want = d >= comfortDistance ? 1f : Mathf.Max(minCloseFactor, (d * d) / (comfortDistance * comfortDistance));
            closeFactor = Mathf.Lerp(closeFactor, want, 1f - Mathf.Exp(-(want < closeFactor ? 14f : 5f) * dt));

            float flicker = 1f;
            if (fearFlicker && body != null && body.Fear > 0.45f)
            {
                if (Time.time >= nextFlickerCheck)
                {
                    nextFlickerCheck = Time.time + Random.Range(0.8f, 2.5f);
                    if (Random.value < body.Fear * 0.5f) flickerUntil = Time.time + Random.Range(0.12f, 0.45f);
                }
                if (Time.time < flickerUntil)
                    flicker = Mathf.PerlinNoise(Time.time * 38f, 0.5f) < 0.45f ? 0.08f : 0.85f;
            }
            torch.intensity = beamIntensity * closeFactor * flicker;
        }

        /// Spot cookie, radius 0 = beam axis .. 1 = cone edge. Tuned against reference photos of LED / reflector torches.
        static Texture2D BuildCookie(int size)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false, true)
            {
                name = "TorchCookie (runtime)", wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear
            };
            var px = new Color32[size * size];
            float half = (size - 1) * 0.5f;
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float dx = (x - half) / half, dy = (y - half) / half;
                float r = Mathf.Sqrt(dx * dx + dy * dy);
                float a = Mathf.Atan2(dy, dx);
                float v = 0f;
                if (r < 1f)
                {
                    float hot = Mathf.Exp(-(r / 0.14f) * (r / 0.14f));                       // hotspot
                    float body = 0.55f * (1f - Smooth(0.12f, 0.6f, r));                        // main beam
                    float ring = 0.22f * Mathf.Exp(-((r - 0.3f) / 0.035f) * ((r - 0.3f) / 0.035f)); // reflector ring
                    float gap = 1f - 0.18f * Mathf.Exp(-((r - 0.4f) / 0.05f) * ((r - 0.4f) / 0.05f)); // darker band
                    float spill = 0.16f * (1f - Smooth(0.5f, 1f, r));                          // wide dim spill
                    float uneven = 1f + 0.06f * (Mathf.PerlinNoise(dx * 3f + 7f, dy * 3f + 3f) - 0.5f)
                                      + 0.03f * Mathf.Sin(a * 5f + r * 9f);                   // lens / reflector flaws
                    v = Mathf.Clamp01((hot + body + ring + spill) * gap * uneven);
                }
                byte b = (byte)Mathf.RoundToInt(v * 255f);
                px[y * size + x] = new Color32(b, b, b, b);
            }
            tex.SetPixels32(px);
            tex.Apply(false, true);
            return tex;
        }

        static float Smooth(float a, float b, float x) { float t = Mathf.Clamp01((x - a) / (b - a)); return t * t * (3f - 2f * t); }

        public void Toggle()
        {
            if (torch == null) return;
            torch.enabled = !torch.enabled;
            if (torch.enabled && realLook) AssignCookie(torch);
            AudioService.Play(SoundType.Switch, torch.transform.position, clickVolume);
        }

        Light FindTorch()
        {
            var cam = GetComponentInChildren<Camera>(true);
            Transform parent = cam != null ? cam.transform : transform;
            var existing = parent.Find("Flashlight");
            if (existing != null && existing.TryGetComponent(out Light found)) return found;

            var go = new GameObject("Flashlight");                 // same settings as SampleScene's torch
            go.transform.SetParent(parent, false);
            var l = go.AddComponent<Light>();
            l.type = LightType.Spot; l.color = new Color(0.91f, 0.93f, 0.95f);
            l.intensity = 70f; l.range = 25f; l.spotAngle = 55f; l.innerSpotAngle = 28f; l.shadows = LightShadows.Soft;
            return l;
        }
    }
}
