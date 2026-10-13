using System;
using UnityEngine;

namespace Echo.Interaction
{
    /// Short on-screen messages ("Power restored") raised by gameplay objects and shown by the HUD.
    /// An event hub, so interaction code never references the UI.
    public static class HudMessage
    {
        public static event Action<string, float> Shown;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() => Shown = null;

        public static void Show(string text, float seconds = 2f)
        {
            if (!string.IsNullOrEmpty(text)) Shown?.Invoke(text, seconds);
        }
    }
}
