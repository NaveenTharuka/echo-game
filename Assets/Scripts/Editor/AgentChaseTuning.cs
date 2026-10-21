using System.Collections.Generic;
using Echo.AI;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Echo.EditorTools
{
    /// ECHO > Agents > Apply Chase Tuning: writes the Phase 1 hunting values into the four agent prefabs without a
    /// full rebuild (AgentAssetBuilder sets the same values on a rebuild), then removes scene overrides of exactly these
    /// fields in the open scene, so instances pick the prefab values up (stale overrides win otherwise).
    public static class AgentChaseTuning
    {
        static readonly string[] Fields =
        {
            "chaseSpeed", "strikeDistance", "closeStrike", "footSlideFix", "blendTopSpeed", "lungeRange", "lungeSpeed"
        };

        [MenuItem("ECHO/Agents/Apply Chase Tuning (prefabs + open scene)")]
        public static void Apply()
        {
            var specs = new[] { AgentAssetBuilder.Listener, AgentAssetBuilder.Tracker, AgentAssetBuilder.Ambusher, AgentAssetBuilder.Mimic };
            var prefabs = new List<GameObject>();
            foreach (var spec in specs)
            {
                var root = PrefabUtility.LoadPrefabContents(spec.PrefabPath);
                try
                {
                    if (root.TryGetComponent(out AgentMotor motor)) motor.chaseSpeed = spec.ChaseSpeed;
                    if (root.TryGetComponent(out ListenerBrain listener)) listener.strikeDistance = 3f;
                    if (root.TryGetComponent(out AmbusherBrain ambusher)) ambusher.closeStrike = 3f;
                    if (root.TryGetComponent(out AgentAnimatorBridge bridge))
                    {
                        // Chasing runs faster than the run clip: speed its playback up instead of sliding.
                        bridge.footSlideFix = true;
                        bridge.blendTopSpeed = spec.LocomotionSpeeds.z;
                    }
                    PrefabUtility.SaveAsPrefabAsset(root, spec.PrefabPath);
                }
                finally { PrefabUtility.UnloadPrefabContents(root); }
                prefabs.Add(AssetDatabase.LoadAssetAtPath<GameObject>(spec.PrefabPath));
                Debug.Log($"[ChaseTuning] {spec.Name}: chase {spec.ChaseSpeed} m/s, run clip top {spec.LocomotionSpeeds.z} m/s");
            }

            // Scene instances: drop overrides of the tuned fields.
            var scene = EditorSceneManager.GetActiveScene();
            int removed = 0;
            foreach (var go in scene.GetRootGameObjects())
            foreach (var t in go.GetComponentsInChildren<Transform>(true))
            {
                if (!PrefabUtility.IsAnyPrefabInstanceRoot(t.gameObject)) continue;
                if (!prefabs.Contains(PrefabUtility.GetCorrespondingObjectFromSource(t.gameObject))) continue;
                var mods = PrefabUtility.GetPropertyModifications(t.gameObject);
                if (mods == null) continue;
                var keep = new List<PropertyModification>();
                foreach (var m in mods)
                {
                    bool tuned = System.Array.IndexOf(Fields, m.propertyPath) >= 0;
                    if (tuned) removed++; else keep.Add(m);
                }
                if (keep.Count != mods.Length) PrefabUtility.SetPropertyModifications(t.gameObject, keep.ToArray());
            }
            if (removed > 0) { EditorSceneManager.MarkSceneDirty(scene); EditorSceneManager.SaveScene(scene); }
            Debug.Log($"[ChaseTuning] done; {removed} stale scene override(s) removed in {scene.name}.");
        }
    }
}
