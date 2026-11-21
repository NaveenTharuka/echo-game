using Echo.Interaction;
using UnityEngine;
using UnityEngine.UI;

namespace Echo.UI
{
    /// Shows "[E] Open" under the crosshair while the player looks at an interactable, and short
    /// HudMessage notices ("Power restored") above it.
    /// Rebuilds the prompt string only when the focus or its verb changes (no per-frame allocations).
    public class InteractionPromptUI : MonoBehaviour
    {
        public PlayerInteractor interactor;
        public Text label;
        [Tooltip("Fades the prompt in and out. Optional.")]
        public CanvasGroup group;
        public float fadeSpeed = 10f;

        [Header("Readability in the dark")]
        [Tooltip("Prompt text size (px at the reference resolution).")]
        public int promptFontSize = 30;
        [Tooltip("Dark panel behind the prompt; its alpha follows the prompt's fade.")]
        public Color backingColor = new(0f, 0f, 0f, 0.55f);
        public Vector2 backingPadding = new(22f, 8f);

        [Header("Messages (HudMessage)")]
        [Tooltip("Optional. If empty, a copy of the prompt label is created above the crosshair.")]
        public Text messageLabel;
        public Vector2 messageOffset = new(0f, 140f);

        IInteractable shownFocus;
        string shownVerb;
        CanvasGroup messageGroup;
        float messageUntil;
        Image backing;
        float backingAlpha = -1f;

        void Awake()
        {
            if (interactor == null) interactor = FindFirstObjectByType<PlayerInteractor>();
            if (label != null && label.font == null)
                label.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            if (label != null) label.text = string.Empty;
            if (group != null) group.alpha = 0f;
            if (label != null)
            {
                label.fontSize = Mathf.Max(label.fontSize, promptFontSize);
                backing = BuildBacking(label);
            }

            if (messageLabel == null && label != null)
            {
                // Same style as the prompt, placed above the crosshair.
                GameObject copy = Instantiate(label.gameObject, label.transform.parent);
                copy.name = "MessageLabel";
                ((RectTransform)copy.transform).anchoredPosition = ((RectTransform)label.transform).anchoredPosition + messageOffset;
                messageLabel = copy.GetComponent<Text>();
            }
            if (messageLabel != null)
            {
                messageLabel.text = string.Empty;
                messageGroup = messageLabel.GetComponent<CanvasGroup>();
                if (messageGroup != null) messageGroup.alpha = 0f;
            }
        }

        void OnEnable() => HudMessage.Shown += ShowMessage;
        void OnDisable() => HudMessage.Shown -= ShowMessage;

        void ShowMessage(string text, float seconds)
        {
            if (messageLabel == null) return;
            messageLabel.text = text;
            messageUntil = Time.unscaledTime + seconds;
        }

        void Update()
        {
            IInteractable focus = interactor != null && interactor.isActiveAndEnabled ? interactor.Focus : null;
            string verb = focus?.GetPrompt(interactor);
            bool visible = !string.IsNullOrEmpty(verb);

            if (visible && (!ReferenceEquals(focus, shownFocus) || !ReferenceEquals(verb, shownVerb)))
            {
                shownFocus = focus;
                shownVerb = verb;
                string key = focus is IHoldToInteract h && h.RequiresHold(interactor)
                    ? "Hold " + interactor.InteractKeyHint
                    : interactor.InteractKeyHint;
                if (label != null)
                {
                    label.text = $"[{key}]  {verb}";
                    FitBacking();
                }
            }
            else if (!visible) shownFocus = null;

            if (group != null)
                group.alpha = Mathf.MoveTowards(group.alpha, visible ? 1f : 0f, fadeSpeed * Time.unscaledDeltaTime);
            else if (label != null)
                label.enabled = visible;

            if (backing != null)
            {
                float a = group != null ? group.alpha : (visible ? 1f : 0f);
                if (!Mathf.Approximately(a, backingAlpha))
                {
                    backingAlpha = a;
                    Color c = backingColor;
                    c.a *= a;
                    backing.color = c;
                    backing.enabled = a > 0.001f;
                }
            }

            bool messageVisible = Time.unscaledTime < messageUntil;
            if (messageGroup != null)
                messageGroup.alpha = Mathf.MoveTowards(messageGroup.alpha, messageVisible ? 1f : 0f, fadeSpeed * Time.unscaledDeltaTime);
            else if (messageLabel != null)
                messageLabel.enabled = messageVisible;
        }

        /// A dark panel drawn just behind the prompt label (sibling before it), same anchor and position.
        static Image BuildBacking(Text text)
        {
            var go = new GameObject("PromptBacking", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            var rt = (RectTransform)go.transform;
            var lrt = (RectTransform)text.transform;
            rt.SetParent(lrt.parent, false);
            rt.SetSiblingIndex(lrt.GetSiblingIndex());
            rt.anchorMin = lrt.anchorMin; rt.anchorMax = lrt.anchorMax; rt.pivot = lrt.pivot;
            rt.anchoredPosition = lrt.anchoredPosition;
            var img = go.GetComponent<Image>();
            img.raycastTarget = false;
            img.enabled = false;
            return img;
        }

        /// Sizes the panel to the text (only when the text changes).
        void FitBacking()
        {
            if (backing == null) return;
            var rt = (RectTransform)backing.transform;
            rt.sizeDelta = new Vector2(label.preferredWidth + backingPadding.x * 2f, label.preferredHeight + backingPadding.y * 2f);
        }

        /// What the prompt currently reads; empty when hidden. Used by the sandbox self-test.
        public string CurrentText => shownFocus != null && label != null ? label.text : string.Empty;

        /// The HUD message currently on screen; empty when none. Used by the walkthrough.
        public string CurrentMessage => Time.unscaledTime < messageUntil && messageLabel != null ? messageLabel.text : string.Empty;
    }
}
