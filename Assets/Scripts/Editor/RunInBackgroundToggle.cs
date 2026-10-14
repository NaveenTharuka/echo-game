#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace Echo.EditorTools
{
    /// ECHO > Dev > Run In Background (Play Mode). Per-machine (EditorPrefs), off by default.
    /// When on, Play Mode keeps running while the Unity window is unfocused (e.g. while driving it from
    /// Claude Code / MCP). Sets only the runtime Application.runInBackground during Play Mode; the project's
    /// Player setting (ProjectSettings) and builds are unaffected, and it is restored on exit.
    [InitializeOnLoad]
    static class RunInBackgroundToggle
    {
        const string PrefKey = "ECHO.Dev.RunInBackgroundInPlayMode";
        const string MenuPath = "ECHO/Dev/Run In Background (Play Mode)";

        static RunInBackgroundToggle()
        {
            EditorApplication.playModeStateChanged -= OnPlayModeChanged;
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
            if (EditorApplication.isPlaying) Apply();   // domain reload while already playing
        }

        static bool Enabled
        {
            get => EditorPrefs.GetBool(PrefKey, false);
            set => EditorPrefs.SetBool(PrefKey, value);
        }

        [MenuItem(MenuPath)]
        static void Toggle()
        {
            Enabled = !Enabled;
            Apply();
            Debug.Log($"[RunInBackgroundToggle] Run in background during Play Mode: {(Enabled ? "ON" : "OFF")} (this machine only).");
        }

        [MenuItem(MenuPath, true)]
        static bool ToggleValidate()
        {
            Menu.SetChecked(MenuPath, Enabled);
            return true;
        }

        static void OnPlayModeChanged(PlayModeStateChange change)
        {
            if (change == PlayModeStateChange.EnteredPlayMode) Apply();
        }

        static void Apply()
        {
            if (!EditorApplication.isPlaying) return;
            // On: keep ticking when unfocused. Off: back to the project's own Player setting.
            Application.runInBackground = Enabled || PlayerSettings.runInBackground;
        }
    }
}
#endif
