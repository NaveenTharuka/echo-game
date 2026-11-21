using System.Collections.Generic;
using Echo.Gameplay;
using UnityEngine;
using UnityEngine.UI;

namespace Echo.UI
{
    /// Objective panel at the top right. Shows ObjectiveDirector.Active as cards: what to do, where, plus an arrow and
    /// the distance to the target (and "up"/"down" when it is on another floor - the facility has four levels).
    /// When an objective disappears from the list it is done: its card turns green ("DONE"), holds, slides out to the
    /// right; only then do the next objectives slide in. Built at runtime by ObjectiveDirector, no scene setup.
    /// Presentation only: it reads the director and the player camera; the AI never sees any of this.
    public class ObjectiveHud : MonoBehaviour
    {
        const float Width = 440f, RowHeight = 74f, Gap = 10f, Margin = 34f, HeaderHeight = 30f;
        const float SlideInTime = 0.5f, DoneHoldTime = 1.2f, SlideOutTime = 0.45f, Stagger = 0.18f;
        const float OffscreenX = Width + 80f;
        const float FloorStep = 2.5f;     // height difference that counts as "another floor" for the hint

        static readonly Color Accent = new(0.95f, 0.66f, 0.24f);
        static readonly Color DoneColor = new(0.38f, 0.92f, 0.48f);
        static readonly Color CardColor = new(0.02f, 0.025f, 0.03f, 0.62f);
        static readonly Color DetailColor = new(0.78f, 0.78f, 0.74f);

        enum Phase { Entering, Active, Done, Leaving }

        class Card
        {
            public string Id;
            public Vector3 Target;
            public RectTransform Rect, ArrowRect;
            public CanvasGroup Group;
            public Image Bar, Arrow;
            public Text Title, Detail, Distance;
            public Phase Phase;
            public float T, Y;
        }

        ObjectiveDirector director;
        Transform player;
        Font font;
        RectTransform root;
        CanvasGroup headerGroup;
        Sprite white, arrowSprite;
        readonly List<Card> cards = new();
        readonly List<ObjectiveDirector.Objective> waiting = new();

        public void Init(ObjectiveDirector owner, Transform playerTransform, Font uiFont)
        {
            director = owner;
            player = playerTransform;
            font = uiFont;
            root = (RectTransform)transform;
            white = Sprite.Create(Texture2D.whiteTexture, new Rect(0, 0, 4, 4), new Vector2(0.5f, 0.5f));
            arrowSprite = BuildArrowSprite();
            BuildHeader();
        }

        void Update()
        {
            if (director == null) return;
            if (player == null)
            {
                var fpc = FindFirstObjectByType<StarterAssets.FirstPersonController>();
                if (fpc != null) player = fpc.transform;
            }
            Diff(director.Active);
            float dt = Time.unscaledDeltaTime;
            Animate(dt);
            Point();
            headerGroup.alpha = Mathf.MoveTowards(headerGroup.alpha, cards.Count > 0 || waiting.Count > 0 ? 1f : 0f, dt * 2f);
        }

        // ------------------------------------------------------------------ list changes

        void Diff(IReadOnlyList<ObjectiveDirector.Objective> wanted)
        {
            // Cards no longer wanted are done.
            foreach (var c in cards)
            {
                if (c.Phase is Phase.Done or Phase.Leaving) continue;
                int i = IndexOf(wanted, c.Id);
                if (i < 0) MarkDone(c);
                else
                {
                    var o = wanted[i];
                    c.Target = o.Target;
                    c.Title.text = o.Title;
                    c.Detail.text = o.Detail;
                }
            }
            waiting.RemoveAll(w => IndexOf(wanted, w.Id) < 0);
            foreach (var o in wanted)
                if (!HasCard(o.Id) && waiting.FindIndex(w => w.Id == o.Id) < 0) waiting.Add(o);

            // New objectives wait until every finished one has left the screen.
            if (waiting.Count == 0 || cards.Exists(c => c.Phase is Phase.Done or Phase.Leaving)) return;
            for (int i = 0; i < waiting.Count; i++) Spawn(waiting[i], -i * Stagger);
            waiting.Clear();
        }

        static int IndexOf(IReadOnlyList<ObjectiveDirector.Objective> list, string id)
        {
            for (int i = 0; i < list.Count; i++) if (list[i].Id == id) return i;
            return -1;
        }

        bool HasCard(string id) => cards.Exists(c => c.Id == id && c.Phase is Phase.Entering or Phase.Active);

        void MarkDone(Card c)
        {
            c.Phase = Phase.Done; c.T = 0f;
            c.Bar.color = DoneColor;
            c.Title.color = DoneColor;
            c.Detail.text = "DONE";
            c.Detail.color = DoneColor;
            c.Arrow.enabled = false;
            c.Distance.text = "";
        }

        // ------------------------------------------------------------------ animation

        void Animate(float dt)
        {
            float y = -(Margin + HeaderHeight);
            for (int i = 0; i < cards.Count; i++)
            {
                var c = cards[i];
                c.T += dt;
                float x = 0f, alpha = 1f;
                switch (c.Phase)
                {
                    case Phase.Entering:
                    {
                        float k = EaseOut(Mathf.Clamp01(c.T / SlideInTime));
                        x = (1f - k) * OffscreenX; alpha = k;
                        if (c.T >= SlideInTime) { c.Phase = Phase.Active; c.T = 0f; }
                        break;
                    }
                    case Phase.Active:
                        // A soft pulse of the accent bar for the first seconds draws the eye to a new objective.
                        c.Bar.color = Color.Lerp(Accent, Color.white, c.T < 2.4f ? 0.35f * (0.5f + 0.5f * Mathf.Sin(c.T * 8f)) : 0f);
                        break;
                    case Phase.Done:
                        c.Bar.color = Color.Lerp(DoneColor, Color.white, 0.4f * (0.5f + 0.5f * Mathf.Sin(c.T * 10f)));
                        if (c.T >= DoneHoldTime) { c.Phase = Phase.Leaving; c.T = 0f; }
                        break;
                    case Phase.Leaving:
                    {
                        float k = EaseIn(Mathf.Clamp01(c.T / SlideOutTime));
                        x = k * OffscreenX; alpha = 1f - k;
                        break;
                    }
                }
                // Cards stack downwards; when one leaves, the ones below glide up into its place.
                c.Y = float.IsNaN(c.Y) ? y : Mathf.Lerp(c.Y, y, 1f - Mathf.Exp(-12f * dt));
                c.Rect.anchoredPosition = new Vector2(-Margin + x, c.Y);
                c.Group.alpha = alpha;
                if (c.Phase != Phase.Leaving) y -= RowHeight + Gap;
            }
            for (int i = cards.Count - 1; i >= 0; i--)
                if (cards[i].Phase == Phase.Leaving && cards[i].T >= SlideOutTime)
                {
                    Destroy(cards[i].Rect.gameObject);
                    cards.RemoveAt(i);
                }
        }

        static float EaseOut(float t) => 1f - (1f - t) * (1f - t) * (1f - t);
        static float EaseIn(float t) => t * t * t;

        /// Arrow towards the target relative to where the player looks, straight-line distance, floor hint.
        void Point()
        {
            var cam = Camera.main;
            if (player == null) return;
            Vector3 eye = cam != null ? cam.transform.position : player.position;
            Vector3 fwd = cam != null ? cam.transform.forward : player.forward;
            fwd.y = 0f;
            foreach (var c in cards)
            {
                if (c.Phase is Phase.Done or Phase.Leaving) continue;
                Vector3 to = c.Target - player.position;
                float dy = to.y;
                to.y = 0f;
                float d = Vector3.Distance(c.Target, eye);
                if (to.sqrMagnitude < 0.01f || fwd.sqrMagnitude < 0.0001f) c.ArrowRect.localEulerAngles = Vector3.zero;
                else c.ArrowRect.localEulerAngles = new Vector3(0f, 0f, -Vector3.SignedAngle(fwd, to, Vector3.up));
                string floor = dy > FloorStep ? "\n<size=13>▲ UP</size>" : dy < -FloorStep ? "\n<size=13>▼ DOWN</size>" : "";
                c.Distance.text = d < 3f ? "HERE" : $"{d:0} m{floor}";
                c.Arrow.enabled = d >= 3f;
            }
        }

        // ------------------------------------------------------------------ building

        void Spawn(ObjectiveDirector.Objective o, float startT)
        {
            var go = new GameObject("Objective_" + o.Id, typeof(RectTransform), typeof(CanvasGroup), typeof(Image));
            go.transform.SetParent(root, false);
            var rt = (RectTransform)go.transform;
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(1f, 1f);
            rt.sizeDelta = new Vector2(Width, RowHeight);
            var bg = go.GetComponent<Image>(); bg.sprite = white; bg.color = CardColor; bg.raycastTarget = false;

            var c = new Card { Id = o.Id, Target = o.Target, Rect = rt, Group = go.GetComponent<CanvasGroup>(), T = startT, Y = float.NaN };
            c.Group.alpha = 0f; c.Group.blocksRaycasts = false; c.Group.interactable = false;

            c.Bar = NewImage(rt, "Bar", white, Accent, new Vector2(0f, 0f), new Vector2(0f, 1f), new Vector2(0f, 0.5f),
                             Vector2.zero, new Vector2(5f, 0f));
            c.Title = NewText(rt, "Title", o.Title, 21, FontStyle.Bold, Color.white, TextAnchor.UpperLeft,
                              new Vector2(18f, -10f), new Vector2(Width - 120f, 28f));
            c.Detail = NewText(rt, "Detail", o.Detail, 15, FontStyle.Normal, DetailColor, TextAnchor.UpperLeft,
                               new Vector2(18f, -40f), new Vector2(Width - 120f, 30f));
            c.Arrow = NewImage(rt, "Arrow", arrowSprite, Accent, new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(0.5f, 0.5f),
                               new Vector2(-50f, -22f), new Vector2(26f, 26f));
            c.ArrowRect = c.Arrow.rectTransform;
            c.Distance = NewText(rt, "Distance", "", 15, FontStyle.Bold, Color.white, TextAnchor.UpperCenter,
                                 new Vector2(Width - 100f, -38f), new Vector2(100f, 36f));
            c.Distance.supportRichText = true;
            c.Phase = Phase.Entering;
            rt.anchoredPosition = new Vector2(-Margin + OffscreenX, 0f);
            cards.Add(c);
        }

        void BuildHeader()
        {
            var go = new GameObject("Header", typeof(RectTransform), typeof(CanvasGroup));
            go.transform.SetParent(root, false);
            var rt = (RectTransform)go.transform;
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(1f, 1f);
            rt.anchoredPosition = new Vector2(-Margin, -Margin);
            rt.sizeDelta = new Vector2(Width, HeaderHeight);
            headerGroup = go.GetComponent<CanvasGroup>();
            headerGroup.alpha = 0f; headerGroup.blocksRaycasts = false;
            NewText(rt, "Label", "O B J E C T I V E", 14, FontStyle.Bold, Accent, TextAnchor.UpperLeft,
                    new Vector2(2f, 0f), new Vector2(Width, 20f));
            NewImage(rt, "Line", white, new Color(Accent.r, Accent.g, Accent.b, 0.45f), new Vector2(0f, 1f), new Vector2(1f, 1f),
                     new Vector2(0.5f, 1f), new Vector2(0f, -20f), new Vector2(0f, 1.5f));
        }

        static Image NewImage(Transform parent, string name, Sprite sprite, Color color, Vector2 anchorMin, Vector2 anchorMax,
                              Vector2 pivot, Vector2 pos, Vector2 size)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image));
            go.transform.SetParent(parent, false);
            var rt = (RectTransform)go.transform;
            rt.anchorMin = anchorMin; rt.anchorMax = anchorMax; rt.pivot = pivot;
            rt.anchoredPosition = pos; rt.sizeDelta = size;
            var img = go.GetComponent<Image>();
            img.sprite = sprite; img.color = color; img.raycastTarget = false;
            return img;
        }

        Text NewText(Transform parent, string name, string text, int size, FontStyle style, Color color, TextAnchor align,
                     Vector2 pos, Vector2 box)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Text), typeof(Shadow));
            go.transform.SetParent(parent, false);
            var rt = (RectTransform)go.transform;
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0f, 1f);
            rt.anchoredPosition = pos; rt.sizeDelta = box;
            var t = go.GetComponent<Text>();
            t.font = font; t.fontSize = size; t.fontStyle = style; t.color = color; t.alignment = align;
            t.text = text; t.raycastTarget = false;
            t.horizontalOverflow = HorizontalWrapMode.Wrap; t.verticalOverflow = VerticalWrapMode.Overflow;
            var sh = go.GetComponent<Shadow>();
            sh.effectColor = new Color(0f, 0f, 0f, 0.85f); sh.effectDistance = new Vector2(1.5f, -1.5f);
            return t;
        }

        /// A navigation arrowhead pointing up (notched triangle), 4x4 supersampled so the edges stay smooth.
        static Sprite BuildArrowSprite()
        {
            const int n = 64;
            var tex = new Texture2D(n, n, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
            Vector2 apex = new(32f, 60f), left = new(7f, 6f), right = new(57f, 6f), notch = new(32f, 22f);
            var px = new Color32[n * n];
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    int hits = 0;
                    for (int sy = 0; sy < 4; sy++)
                        for (int sx = 0; sx < 4; sx++)
                        {
                            var p = new Vector2(x + (sx + 0.5f) / 4f, y + (sy + 0.5f) / 4f);
                            if (InTri(p, apex, left, notch) || InTri(p, apex, notch, right)) hits++;
                        }
                    px[y * n + x] = new Color32(255, 255, 255, (byte)(hits * 255 / 16));
                }
            tex.SetPixels32(px);
            tex.Apply(false, true);
            return Sprite.Create(tex, new Rect(0, 0, n, n), new Vector2(0.5f, 0.5f), 100f);
        }

        static bool InTri(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
        {
            float d1 = Cross(p, a, b), d2 = Cross(p, b, c), d3 = Cross(p, c, a);
            bool neg = d1 < 0 || d2 < 0 || d3 < 0, pos = d1 > 0 || d2 > 0 || d3 > 0;
            return !(neg && pos);
        }

        static float Cross(Vector2 p, Vector2 a, Vector2 b) => (p.x - b.x) * (a.y - b.y) - (a.x - b.x) * (p.y - b.y);
    }
}
