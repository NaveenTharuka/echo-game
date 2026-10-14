using UnityEngine;
using UnityEngine.Events;

namespace Echo.Interaction
{
    /// A switch, lever or generator: press Interact, make a noise, fire UnityEvents
    /// (e.g. Door.Unlock). Single-use (hides its prompt afterwards) or a toggle.
    /// Generator setup: noise = Generator, noiseIntensity 0.8, verb "Start generator" (guide §6.5).
    public class SwitchInteractable : MonoBehaviour, IInteractable
    {
        public string verb = "Use";
        [Tooltip("Single-use switches can't be used again; toggles alternate onActivated / onDeactivated.")]
        public bool singleUse = true;
        public NoiseType noise = NoiseType.Switch;
        public float noiseIntensity = 0.25f;
        [Tooltip("Shown on the HUD when activated, e.g. \"Power restored\". Empty = none.")]
        public string activatedMessage = "";
        public float messageSeconds = 2f;

        public UnityEvent onActivated = new();
        [Tooltip("Toggles only.")]
        public UnityEvent onDeactivated = new();

        [Header("Indicator (optional)")]
        public Renderer indicator;
        public Color offColor = new(0.9f, 0.08f, 0.05f);
        public Color onColor = new(0.1f, 0.85f, 0.25f);

        public bool Activated { get; private set; }

        static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        MaterialPropertyBlock block;

        void Awake() => UpdateIndicator();

        public bool CanInteract(PlayerInteractor interactor) => isActiveAndEnabled && !(singleUse && Activated);

        public string GetPrompt(PlayerInteractor interactor) => verb;

        public void Interact(PlayerInteractor interactor)
        {
            if (!CanInteract(interactor)) return;
            Activated = !Activated;
            InteractionNoise.Emit(noise, transform.position, noiseIntensity, NoiseSource.Player, gameObject);
            UpdateIndicator();
            (Activated ? onActivated : onDeactivated)?.Invoke();
            if (Activated) HudMessage.Show(activatedMessage, messageSeconds);
        }

        /// Back to the off state without firing events (sandbox self-test / level reset).
        public void ResetSwitch()
        {
            Activated = false;
            UpdateIndicator();
        }

        void UpdateIndicator()
        {
            if (indicator == null) return;
            block ??= new MaterialPropertyBlock();
            block.SetColor(BaseColorId, Activated ? onColor : offColor);
            indicator.SetPropertyBlock(block);
        }
    }
}
