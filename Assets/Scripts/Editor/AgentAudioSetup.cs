using System.Linq;
using Echo.AI;
using Echo.Sound;
using UnityEditor;
using UnityEngine;
using UnityEngine.AI;

namespace Echo.EditorTools
{
    /// ECHO > Audio > Apply Agent Sounds. Edits the agent prefabs in place (like Apply Size, so scene overrides such as
    /// patrol zones survive): each agent's own steps, an AgentVoice with near + far scream sources, hurt / attack /
    /// death clips, and a Feet source for agents that had none (the Tracker used the player's step sounds).
    /// AgentAssetBuilder calls Apply() too, so a full rebuild keeps the voices.
    public static class AgentAudioSetup
    {
        const string Root = "Assets/Audio/Agents/";

        struct Spec
        {
            public string agent, steps; public Vector2 stepPitch; public bool lockOnScream;
            public float screamRange;
        }

        static readonly Spec[] Specs =
        {
            new() { agent = "Listener", steps = "Listener_Step_", stepPitch = new(0.8f, 0.95f), screamRange = 50f },
            new() { agent = "Ambusher", steps = "Ambusher_Step_", stepPitch = new(0.92f, 1.04f), screamRange = 45f },
            new() { agent = "Tracker", steps = "Tracker_Step_", stepPitch = new(0.9f, 1.02f), screamRange = 45f, lockOnScream = true },
            new() { agent = "Mimic", steps = "Mimic_Step_", stepPitch = new(0.95f, 1.1f), screamRange = 40f },
        };

        [MenuItem("ECHO/Audio/Apply Agent Sounds")]
        static void ApplyAll()
        {
            foreach (var s in Specs)
            {
                string path = $"Assets/Prefabs/Agents/{s.agent}.prefab";
                if (AssetDatabase.LoadAssetAtPath<GameObject>(path) == null) { Debug.LogWarning($"[AgentAudio] no {path}"); continue; }
                var root = PrefabUtility.LoadPrefabContents(path);
                try
                {
                    Debug.Log($"[AgentAudio] {s.agent}: {Apply(root, s.agent)}");
                    PrefabUtility.SaveAsPrefabAsset(root, path);
                }
                finally { PrefabUtility.UnloadPrefabContents(root); }
            }
            AssetDatabase.SaveAssets();
        }

        /// Wires the agent's audio on a prefab root. Returns a short summary.
        public static string Apply(GameObject root, string agent)
        {
            var s = Specs.FirstOrDefault(x => x.agent == agent);
            if (s.agent == null) return "no audio spec";
            string folder = Root + agent;
            float height = root.TryGetComponent(out NavMeshAgent nav) ? nav.height : 2f;

            // Steps: the agent's own recordings, on its own (occluded) Feet source.
            string stepInfo = "steps unchanged";
            var stepClips = SoundLibraryBuilder.Clips(folder, s.steps);
            if (stepClips.Length > 0 && root.TryGetComponent(out AgentFootsteps feet))
            {
                if (feet.source == null) feet.source = Child(root.transform, "Feet", 0.1f, 1f, 16f);
                feet.clips = stepClips;
                feet.pitch = s.stepPitch;
                stepInfo = $"{stepClips.Length} steps";
            }

            // Voice.
            var scream = new[] { Clip(folder, $"{agent}_Scream_01"), Clip(folder, $"{agent}_Scream_02") }.Where(c => c != null).ToArray();
            var far = new[] { Clip(folder, $"{agent}_Scream_01_Far"), Clip(folder, $"{agent}_Scream_02_Far") }.Where(c => c != null).ToArray();
            var hurt = SoundLibraryBuilder.Clips(folder, $"{agent}_Hurt_");
            var attack = SoundLibraryBuilder.Clips(folder, $"{agent}_Attack_");
            var death = SoundLibraryBuilder.Clips(folder, $"{agent}_Death_");
            if (scream.Length + hurt.Length + attack.Length + death.Length == 0)
                return stepInfo + ", no voice clips (shared growls stay)";

            if (!root.TryGetComponent(out AgentVoice voice)) voice = root.AddComponent<AgentVoice>();
            voice.source = Find(root.transform, "Vocal");
            if (voice.source == null) voice.source = Child(root.transform, "Vocal", height * 0.85f, 3f, s.screamRange);
            voice.farSource = Find(root.transform, "VocalFar");
            if (voice.farSource == null) voice.farSource = Child(root.transform, "VocalFar", height * 0.85f, 8f, s.screamRange * 2f);
            voice.scream = scream; voice.screamFar = far;
            voice.hurt = hurt; voice.attack = attack; voice.death = death;
            voice.screamOnLockOn = s.lockOnScream;
            return $"{stepInfo}, voice: {scream.Length} screams (+{far.Length} far), {hurt.Length} hurt, " +
                   $"{attack.Length} attack, {death.Length} death";
        }

        static AudioClip Clip(string folder, string name) =>
            AssetDatabase.LoadAssetAtPath<AudioClip>($"{folder}/{name}.wav");

        static AudioSource Find(Transform root, string name)
        {
            var t = root.Find(name);
            return t != null && t.TryGetComponent(out AudioSource a) ? a : null;
        }

        static AudioSource Child(Transform parent, string name, float height, float min, float max)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = new Vector3(0f, height, 0f);
            var a = go.AddComponent<AudioSource>();
            a.spatialBlend = 1f; a.dopplerLevel = 0f; a.playOnAwake = false;
            a.rolloffMode = AudioRolloffMode.Linear;   // silent beyond max distance: a fair, readable cue
            a.minDistance = min; a.maxDistance = max;
            go.AddComponent<AudioLowPassFilter>();
            go.AddComponent<AudioOcclusion>();
            return a;
        }
    }
}
