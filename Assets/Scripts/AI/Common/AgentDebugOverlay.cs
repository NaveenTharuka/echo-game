using UnityEngine;
using UnityEngine.InputSystem;

namespace Echo.AI
{
    /// On-screen state of every agent (guide §15.2). F1 toggles it; F2 / F3 / F4 = time scale 0.25x / 1x / 3x.
    public class AgentDebugOverlay : MonoBehaviour
    {
        public bool visible = true;
        GUIStyle style;

        void Update()
        {
            var k = Keyboard.current;
            if (k == null) return;
            if (k.f1Key.wasPressedThisFrame) visible = !visible;
            if (k.f2Key.wasPressedThisFrame) Time.timeScale = 0.25f;
            if (k.f3Key.wasPressedThisFrame) Time.timeScale = 1f;
            if (k.f4Key.wasPressedThisFrame) Time.timeScale = 3f;
        }

        void OnGUI()
        {
            if (!visible) return;
            style ??= new GUIStyle(GUI.skin.label) { richText = true, fontSize = 14 };
            GUILayout.BeginArea(new Rect(10, 10, 480, 400), GUI.skin.box);
            GUILayout.Label($"<b>AGENTS</b>  (F1 hide, F2/F3/F4 time {Time.timeScale:0.##}x)", style);
            foreach (var a in AgentRegistry.All)
                GUILayout.Label($"<b>{a.DisplayName}</b>  [{a.StateName}]  alert {a.AlertLevel:0.0}\n{a.DebugText}", style);
            GUILayout.EndArea();
        }
    }
}
