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

        InputAction toggle;
        public bool IsOn => torch != null && torch.enabled;

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
            toggle = new InputAction("ToggleFlashlight", InputActionType.Button, toggleBinding);
            toggle.performed += _ => Toggle();
        }

        void OnEnable() => toggle.Enable();
        void OnDisable() => toggle.Disable();
        void OnDestroy() => toggle.Dispose();

        void Start()
        {
            if (showHint && !hintShown) Invoke(nameof(ShowHint), hintDelay);
        }

        void ShowHint()
        {
            hintShown = true;
            HudMessage.Show("[L]  Flashlight", 4f);
        }

        public void Toggle()
        {
            if (torch == null) return;
            torch.enabled = !torch.enabled;
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
