using System;
using System.Collections;
using Echo.Interaction;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
#if UNITY_EDITOR
using UnityEditor.SceneManagement;
using UnityEngine.InputSystem;
#endif

namespace Echo.Player
{
    /// One-hit death. Any agent that reaches the player calls Kill() (on the player, or PlayerDeath.Current).
    /// Kill: control stops at once (input, movement, interaction, carrying, weapon, body noises), anything held or
    /// dragged is dropped, the screen fades to black over fadeSeconds, then the active scene is reloaded.
    /// No health, no health UI: the threat is binary, which suits a blind hunter that only has to find you.
    /// Editor only: F9 toggles invulnerability (testing the level without dying); not compiled into builds.
    [DisallowMultipleComponent]
    public class PlayerDeath : MonoBehaviour
    {
        public float fadeSeconds = 1f;
        [Tooltip("Disabled on death. Empty -> movement, input, interactor, carrier, weapon and body noises on this object.")]
        public Behaviour[] disableOnDeath;

        /// The live player, for agents that don't hold a reference.
        public static PlayerDeath Current { get; private set; }
        public bool IsDead { get; private set; }
        public event Action Died;

        CanvasGroup fade;

        void Awake()
        {
            if (disableOnDeath == null || disableOnDeath.Length == 0)
            {
                var list = new System.Collections.Generic.List<Behaviour>();
                foreach (Behaviour b in GetComponents<Behaviour>())
                    if (b != this) list.Add(b);   // everything that acts: FPC, PlayerInput, ... (the capsule is not a Behaviour)
                disableOnDeath = list.ToArray();
            }
        }

        void OnEnable() => Current = this;
        void OnDisable() { if (Current == this) Current = null; }

        /// Kills the player. Returns false if already dead (or, in the editor, invulnerable).
        public bool Kill(GameObject killer = null)
        {
            if (IsDead) return false;
#if UNITY_EDITOR
            if (Invulnerable)
            {
                Debug.Log($"[PlayerDeath] Kill by {(killer != null ? killer.name : "?")} ignored: invulnerable (F9).", this);
                return false;
            }
#endif
            IsDead = true;
            var carrier = GetComponent<PhysicsCarrier>();
            if (carrier != null) carrier.Drop();   // before disabling: it restores the player's speed and the prop's state
            foreach (Behaviour b in disableOnDeath)
                if (b != null) b.enabled = false;
            Died?.Invoke();
            StartCoroutine(FadeAndReload());
            return true;
        }

        IEnumerator FadeAndReload()
        {
            if (fade == null) fade = BuildFade();
            for (float t = 0f; t < fadeSeconds; t += Time.unscaledDeltaTime)
            {
                fade.alpha = t / fadeSeconds;
                yield return null;
            }
            fade.alpha = 1f;

            Scene scene = SceneManager.GetActiveScene();
#if UNITY_EDITOR
            // Works for scenes that are not in Build Settings (the sandbox) as well.
            EditorSceneManager.LoadSceneInPlayMode(scene.path, new LoadSceneParameters(LoadSceneMode.Single));
#else
            SceneManager.LoadScene(scene.buildIndex);
#endif
        }

        static CanvasGroup BuildFade()
        {
            var go = new GameObject("DeathFade (runtime)", typeof(Canvas), typeof(CanvasGroup));
            var canvas = go.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 1000;   // above the HUD
            var image = new GameObject("Black", typeof(RectTransform), typeof(Image));
            image.transform.SetParent(go.transform, false);
            var rt = (RectTransform)image.transform;
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = rt.offsetMax = Vector2.zero;
            image.GetComponent<Image>().color = Color.black;
            var group = go.GetComponent<CanvasGroup>();
            group.alpha = 0f;
            group.blocksRaycasts = false;
            return group;
        }

#if UNITY_EDITOR
        public bool Invulnerable { get; private set; }

        public void SetInvulnerable(bool on)
        {
            Invulnerable = on;
            HudMessage.Show(on ? "Invulnerable (F9)" : "Vulnerable", 1.5f);
            Debug.Log($"[PlayerDeath] Invulnerable: {on}", this);
        }

        void Update()
        {
            if (Keyboard.current != null && Keyboard.current.f9Key.wasPressedThisFrame) SetInvulnerable(!Invulnerable);
        }
#endif
    }
}
