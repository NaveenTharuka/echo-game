using System.Collections.Generic;
using Echo.AI;
using Echo.Player;
using Echo.Sound;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace Echo.UI
{
    /// Player noise meter, bottom centre, in the style of stealth-horror HUDs: a mirrored waveform that jumps with every
    /// sound the player makes (steps, landings, doors, gunshots) and decays back to a flat line, a one-word level
    /// (SILENT / QUIET / NOISY / LOUD) and a red "HEARD" flash when a creature actually perceived that sound.
    ///
    /// Reads ground truth only (SoundBus.Emitted with Source == Player, and each agent's Hearing.Heard) - UI may, the AI
    /// never does. Builds its own overlay canvas; one is created automatically in any scene that has a PlayerNoise, so no
    /// scene needs editing. Thrown decoys are Environment sounds, so they do not move the meter (they are not the player).
    public class PlayerNoiseMeter : MonoBehaviour
    {
        /// Set false (e.g. from a menu scene) to stop the automatic meter.
        public static bool AutoCreate = true;

        [Header("Response")]
        [Tooltip("Seconds to rise to a new sound's level.")]
        public float attackTime = 0.05f;
        [Tooltip("Display level lost per second (1 = full scale).")]
        public float decayPerSecond = 0.75f;

        [Header("Words (sound intensity: walk 0.15, run 0.3, door 0.5, gunshot 1)")]
        public float quietBelow = 0.2f;
        public float loudFrom = 0.35f;

        [Header("Look")]
        public int bars = 31;
        public Vector2 size = new(320f, 52f);
        public float bottomMargin = 54f;
        [Range(0f, 1f)] public float idleAlpha = 0.3f;
        public float idleAfter = 1.5f;
        public Color quietColor = new(0.86f, 0.9f, 0.94f);
        public Color noisyColor = new(1f, 0.66f, 0.22f);
        public Color loudColor = new(0.92f, 0.16f, 0.12f);

        [Header("Heard by a creature")]
        public bool showHeard = true;
        public float heardSeconds = 1.4f;

        float display, target, lastNoiseTime = -99f, heardUntil = -99f, nextScan;
        readonly Dictionary<int, float> recentPlayerSounds = new();          // event id -> time
        readonly List<int> expired = new();
        readonly HashSet<Hearing> ears = new();
        RectTransform[] barRects;
        Image[] barImages;
        Text levelLabel, heardLabel;
        CanvasGroup group;
        string shownWord;

        // ------------------------------------------------------------------ auto-create

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Boot()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            SceneManager.sceneLoaded += OnSceneLoaded;
            EnsureMeter();
        }

        static void OnSceneLoaded(Scene scene, LoadSceneMode mode) => EnsureMeter();

        static void EnsureMeter()
        {
            if (!AutoCreate || FindFirstObjectByType<PlayerNoiseMeter>() != null) return;
            if (FindFirstObjectByType<PlayerNoise>() == null) return;     // menus, tests: no player, no meter
            new GameObject("PlayerNoiseMeter").AddComponent<PlayerNoiseMeter>();
        }

        // ------------------------------------------------------------------ lifecycle

        void Awake() => BuildUi();

        void OnEnable() => SoundBus.Emitted += OnSound;

        void OnDisable()
        {
            SoundBus.Emitted -= OnSound;
            foreach (var ear in ears) if (ear != null) ear.Heard -= OnHeard;
            ears.Clear();
        }

        void OnSound(SoundEvent e)
        {
            if (e.Source != SoundSource.Player) return;
            // sqrt: a walking step (0.15) still reads clearly; a gunshot (1.0) fills the meter.
            target = Mathf.Max(target, Mathf.Sqrt(e.Intensity));
            lastNoiseTime = Time.time;
            recentPlayerSounds[e.Id] = Time.time;
        }

        void OnHeard(PerceivedSound s)
        {
            if (recentPlayerSounds.ContainsKey(s.EventId)) heardUntil = Time.time + heardSeconds;
        }

        void Update()
        {
            if (Time.time >= nextScan) { nextScan = Time.time + 1f; ScanEars(); ForgetOldSounds(); }

            float dt = Time.deltaTime;
            if (target > display) display = Mathf.MoveTowards(display, target, dt / Mathf.Max(0.001f, attackTime));
            else display = Mathf.Max(0f, display - decayPerSecond * dt);
            if (display >= target) target = 0f;

            DrawBars();
            DrawWords();

            bool active = Time.time - lastNoiseTime < idleAfter || display > 0.02f || Time.time < heardUntil;
            group.alpha = Mathf.MoveTowards(group.alpha, active ? 1f : idleAlpha, dt * (active ? 6f : 1.2f));
        }

        // ------------------------------------------------------------------ drawing

        void DrawBars()
        {
            float t = Time.time;
            float spread = 0.22f + 0.5f * display;                     // louder = wider as well as taller
            Color tint = LevelColor(display);
            bool heard = showHeard && Time.time < heardUntil;
            if (heard) tint = Color.Lerp(tint, loudColor, 0.5f + 0.5f * Mathf.Sin(t * 14f));
            int n = barRects.Length;
            for (int i = 0; i < n; i++)
            {
                float x = n == 1 ? 0f : (i - (n - 1) * 0.5f) / ((n - 1) * 0.5f);
                float shape = Mathf.Exp(-x * x / (2f * spread * spread));
                float jitter = 0.7f + 0.3f * Mathf.PerlinNoise(t * 9f, i * 0.43f);
                float h = Mathf.Max(2f, size.y * display * shape * jitter);
                barRects[i].sizeDelta = new Vector2(barRects[i].sizeDelta.x, h);
                var c = tint; c.a = Mathf.Lerp(0.35f, 1f, shape);
                barImages[i].color = c;
            }
        }

        void DrawWords()
        {
            float intensity = display * display;                       // back from the sqrt display scale
            string word = display < 0.06f ? "SILENT" : intensity < quietBelow ? "QUIET" : intensity < loudFrom ? "NOISY" : "LOUD";
            if (word != shownWord) { shownWord = word; levelLabel.text = word; }
            levelLabel.color = Color.Lerp(LevelColor(display), Color.white, 0.25f);
            bool heard = showHeard && Time.time < heardUntil;
            heardLabel.enabled = heard;
            if (heard)
            {
                var c = loudColor; c.a = 0.6f + 0.4f * Mathf.Sin(Time.time * 14f);
                heardLabel.color = c;
            }
        }

        Color LevelColor(float level)
        {
            float quiet = Mathf.Sqrt(quietBelow), loud = Mathf.Sqrt(loudFrom);
            if (level <= quiet) return quietColor;
            if (level <= loud) return Color.Lerp(quietColor, noisyColor, Mathf.InverseLerp(quiet, loud, level));
            return Color.Lerp(noisyColor, loudColor, Mathf.InverseLerp(loud, 1f, level));
        }

        // ------------------------------------------------------------------ helpers

        /// Subscribe to every agent's ear (agents can appear after the meter).
        void ScanEars()
        {
            foreach (var brain in AgentRegistry.All)
            {
                if (brain == null || !brain.TryGetComponent(out Hearing ear) || ears.Contains(ear)) continue;
                ear.Heard += OnHeard;
                ears.Add(ear);
            }
        }

        void ForgetOldSounds()
        {
            expired.Clear();
            foreach (var kv in recentPlayerSounds) if (Time.time - kv.Value > 5f) expired.Add(kv.Key);
            foreach (int id in expired) recentPlayerSounds.Remove(id);
        }

        void BuildUi()
        {
            var canvas = gameObject.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 40;
            var scaler = gameObject.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;

            var root = new GameObject("Meter", typeof(RectTransform), typeof(CanvasGroup));
            root.transform.SetParent(transform, false);
            var rt = (RectTransform)root.transform;
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0f);
            rt.anchoredPosition = new Vector2(0f, bottomMargin);
            rt.sizeDelta = new Vector2(size.x, size.y + 30f);
            group = root.GetComponent<CanvasGroup>();
            group.alpha = idleAlpha; group.interactable = false; group.blocksRaycasts = false;

            barRects = new RectTransform[Mathf.Max(1, bars)];
            barImages = new Image[barRects.Length];
            float step = size.x / barRects.Length;
            for (int i = 0; i < barRects.Length; i++)
            {
                var bar = new GameObject("Bar" + i, typeof(RectTransform), typeof(Image));
                bar.transform.SetParent(root.transform, false);
                var br = (RectTransform)bar.transform;
                br.anchorMin = br.anchorMax = br.pivot = new Vector2(0.5f, 0.5f);
                br.anchoredPosition = new Vector2((i - (barRects.Length - 1) * 0.5f) * step, 15f);
                br.sizeDelta = new Vector2(Mathf.Max(2f, step * 0.5f), 2f);
                var img = bar.GetComponent<Image>();
                img.raycastTarget = false;
                barRects[i] = br; barImages[i] = img;
            }

            var font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            levelLabel = Label(root.transform, "Level", font, new Vector2(0f, -size.y * 0.5f + 4f), TextAnchor.MiddleCenter);
            heardLabel = Label(root.transform, "Heard", font, new Vector2(size.x * 0.5f + 14f, 15f), TextAnchor.MiddleLeft);
            ((RectTransform)heardLabel.transform).pivot = new Vector2(0f, 0.5f);   // grows to the right of the bars
            heardLabel.text = "HEARD";
            heardLabel.enabled = false;
        }

        static Text Label(Transform parent, string name, Font font, Vector2 pos, TextAnchor align)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Text), typeof(Outline));
            go.transform.SetParent(parent, false);
            var r = (RectTransform)go.transform;
            r.anchorMin = r.anchorMax = r.pivot = new Vector2(0.5f, 0.5f);
            r.anchoredPosition = pos;
            r.sizeDelta = new Vector2(160f, 24f);
            var t = go.GetComponent<Text>();
            t.font = font; t.fontSize = 15; t.fontStyle = FontStyle.Bold; t.alignment = align;
            t.raycastTarget = false;
            var o = go.GetComponent<Outline>();
            o.effectColor = new Color(0f, 0f, 0f, 0.7f); o.effectDistance = new Vector2(1f, -1f);
            return t;
        }
    }
}
