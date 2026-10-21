using Echo.AI;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AI;

namespace Echo.EditorTools
{
    /// ECHO > Agents > <Name> > 3. Apply Size: makes an existing agent prefab match its AgentSpec's ModelScale / BodyScale /
    /// LocomotionSpeeds without a full rebuild. A rebuild replaces the prefab's components, which drops the scene's
    /// overrides on them (e.g. SampleScene's patrol territory); this only changes the size-related values in place:
    /// model scale, NavMeshAgent, bullet capsule, touch sphere, claw reach, stride, voice height, and the
    /// Locomotion blend-tree thresholds (so the bigger stride still matches the ground speed).
    public static class AgentSizeTool
    {
        [MenuItem("ECHO/Agents/Listener/3. Apply Size (in place, no rebuild)")]
        static void Listener() => Apply(AgentAssetBuilder.Listener);

        [MenuItem("ECHO/Agents/Mimic/3. Apply Size (in place, no rebuild)")]
        static void Mimic() => Apply(AgentAssetBuilder.Mimic);

        public static void Apply(AgentAssetBuilder.AgentSpec spec)
        {
            float k = spec.BodyScale;
            var root = PrefabUtility.LoadPrefabContents(spec.PrefabPath);
            try
            {
                var model = root.transform.Find("Model");
                if (model != null) model.localScale = Vector3.one * spec.ModelScale;
                if (root.TryGetComponent(out NavMeshAgent nav)) { nav.radius = 0.4f * k; nav.height = 2f * k; }
                if (root.TryGetComponent(out CapsuleCollider capsule))
                { capsule.center = new Vector3(0f, k, 0f); capsule.height = 2f * k; capsule.radius = 0.4f * k; }
                var touch = root.transform.Find("ContactSense");
                if (touch != null)
                {
                    touch.localPosition = new Vector3(0f, k, 0f);
                    if (touch.TryGetComponent(out SphereCollider s)) s.radius = 1.3f * k;
                    if (touch.TryGetComponent(out ContactSense c)) c.radius = 1.3f * k;
                }
                if (root.TryGetComponent(out AgentBody body)) { body.reach = 1.1f * k; body.hitRadius = 0.9f * k; }
                if (root.TryGetComponent(out AgentFootsteps steps)) steps.stride = 1.4f * k;
                if (root.TryGetComponent(out AgentAnimatorBridge bridge)) bridge.blendTopSpeed = spec.LocomotionSpeeds.z;
                var voice = root.transform.Find("Voice");
                if (voice != null) voice.localPosition = new Vector3(0f, 1.6f * k, 0f);
                PrefabUtility.SaveAsPrefabAsset(root, spec.PrefabPath);
            }
            finally { PrefabUtility.UnloadPrefabContents(root); }

            // Blend-tree thresholds: Sneak / Walk / Run clips -> LocomotionSpeeds.
            var ac = AssetDatabase.LoadAssetAtPath<AnimatorController>(spec.ControllerPath);
            int changed = 0;
            if (ac != null)
                foreach (var st in ac.layers[0].stateMachine.states)
                {
                    if (st.state.name != "Locomotion" || st.state.motion is not BlendTree tree) continue;
                    var children = tree.children;
                    for (int i = 0; i < children.Length; i++)
                    {
                        string clip = children[i].motion != null ? children[i].motion.name : "";
                        float t = clip == spec.Sneak ? spec.LocomotionSpeeds.x : clip == spec.Walk ? spec.LocomotionSpeeds.y
                                : clip == spec.Run ? spec.LocomotionSpeeds.z : children[i].threshold;
                        if (!Mathf.Approximately(t, children[i].threshold)) { children[i].threshold = t; changed++; }
                    }
                    tree.children = children;
                    EditorUtility.SetDirty(tree);
                }
            EditorUtility.SetDirty(ac);
            AssetDatabase.SaveAssets();
            Debug.Log($"[AgentSize] {spec.Name}: model x{spec.ModelScale}, body x{k}, locomotion thresholds " +
                      $"{spec.LocomotionSpeeds} ({changed} changed).");
        }
    }
}
