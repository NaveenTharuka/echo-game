using Echo.Interaction;
using UnityEngine;

namespace Echo.DevTools
{
    /// Sandbox stand-in interactable: counts presses, emits a Switch noise and flashes.
    /// Used to exercise PlayerInteractor / prompt / InteractionNoise before real doors exist.
    public class InteractionProbe : MonoBehaviour, IInteractable
    {
        public string verb = "Press";
        public bool interactable = true;
        public float noiseIntensity = 0.25f;
        public float noiseCooldown = 1f;
        public Color flashColor = new(1f, 0.6f, 0.1f);

        public int InteractCount { get; private set; }
        public bool LastNoiseAccepted { get; private set; }

        static readonly int BaseColor = Shader.PropertyToID("_BaseColor");
        Renderer rend;
        MaterialPropertyBlock block;
        float flashUntil;

        void Awake()
        {
            rend = GetComponent<Renderer>();
            block = new MaterialPropertyBlock();
        }

        public bool CanInteract(PlayerInteractor interactor) => interactable && isActiveAndEnabled;

        public string GetPrompt(PlayerInteractor interactor) => verb;

        public void Interact(PlayerInteractor interactor)
        {
            InteractCount++;
            LastNoiseAccepted = InteractionNoise.Emit(NoiseType.Switch, transform.position, noiseIntensity,
                                                      NoiseSource.Player, gameObject, noiseCooldown);
            flashUntil = Time.time + 0.25f;
            SetTint(flashColor);
        }

        void Update()
        {
            if (flashUntil > 0f && Time.time >= flashUntil)
            {
                flashUntil = 0f;
                if (rend != null) rend.SetPropertyBlock(null);
            }
        }

        void SetTint(Color c)
        {
            if (rend == null) return;
            block.SetColor(BaseColor, c);
            rend.SetPropertyBlock(block);
        }
    }
}
