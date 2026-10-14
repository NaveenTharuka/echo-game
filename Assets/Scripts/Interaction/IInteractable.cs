namespace Echo.Interaction
{
    /// Anything the player can aim at and press Interact on: doors, switches, the generator.
    /// Objects that are carried or dragged are handled by PhysicsCarrier, not through this interface.
    public interface IInteractable
    {
        /// False hides the prompt and ignores the key (e.g. a door that is mid-slide).
        bool CanInteract(PlayerInteractor interactor);

        /// Verb shown after the key hint, e.g. "Open", "Close", "Locked".
        /// Return a cached/constant string: it is read every frame while focused.
        string GetPrompt(PlayerInteractor interactor);

        void Interact(PlayerInteractor interactor);
    }

    /// Optional: an interactable that acts only while the Interact key is held (dragging a barricade).
    /// The prompt then reads "[Hold E]  Drag" instead of "[E]  Drag".
    public interface IHoldToInteract
    {
        bool RequiresHold(PlayerInteractor interactor);
    }
}
