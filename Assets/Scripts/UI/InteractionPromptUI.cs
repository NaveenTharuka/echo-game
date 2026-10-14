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

        [Header("Messages (HudMessage)")]
        [Tooltip("Optional. If empty, a copy of the prompt label is created above the crosshair.")]
        public Text messageLabel;
        public Vector2 messageOffset = new(0f, 140f);

        IInteractable shownFocus;
        string shownVerb;
        CanvasGroup messageGroup;
        float messageUntil;

        void Awake()
        {
            if (interactor == null) interactor = FindFirstObjectByType<PlayerInteractor>();
            if (label != null && label.font == null)
                label.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            if (label != null) label.text = string.Empty;
            if (group != null) group.alpha = 0f;

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
                if (label != null) label.text = $"[{key}]  {verb}";
            }
            else if (!visible) shownFocus = null;

            if (group != null)
                group.alpha = Mathf.MoveTowards(group.alpha, visible ? 1f : 0f, fadeSpeed * Time.unscaledDeltaTime);
            else if (label != null)
                label.enabled = visible;

            bool messageVisible = Time.unscaledTime < messageUntil;
            if (messageGroup != null)
                messageGroup.alpha = Mathf.MoveTowards(messageGroup.alpha, messageVisible ? 1f : 0f, fadeSpeed * Time.unscaledDeltaTime);
            else if (messageLabel != null)
                messageLabel.enabled = messageVisible;
        }

        /// What the prompt currently reads; empty when hidden. Used by the sandbox self-test.
        public string CurrentText => shownFocus != null && label != null ? label.text : string.Empty;

        /// The HUD message currently on screen; empty when none. Used by the walkthrough.
        public string CurrentMessage => Time.unscaledTime < messageUntil && messageLabel != null ? messageLabel.text : string.Empty;
    }
}
