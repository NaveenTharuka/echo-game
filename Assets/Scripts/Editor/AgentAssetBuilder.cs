using System;
using System.Collections.Generic;
using System.IO;
using Echo.AI;
using Echo.Sound;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using UnityEngine.AI;

namespace Echo.EditorTools
{
    /// Turns an agent .fbx into a ready-to-use agent prefab, re-runnably (safe to run again after re-exporting the model):
    ///   1. FBX import: clips renamed (ECHO_Rig|ECHO_Walk -> Walk), locomotion clips looped.
    ///   2. Animator Controller (guide §8.5): Speed blend tree + Listening / Alert / Attack / Hit / Dead.
    ///   3. Prefab: root (NavMeshAgent, kinematic Rigidbody, capsule, ear, motor, body, brain, animator bridge,
    ///      footsteps) + model child + ContactSense child.
    /// One AgentSpec per agent; the Listener's is below. Copy it for Tracker / Mimic / Ambusher.
    public static class AgentAssetBuilder
    {
        public const string EnvironmentLayer = "Environment", AgentsLayer = "Agents";

        public class AgentSpec
        {
            public string Name;                 // "Listener"
            public string ModelPath;            // the .fbx
            public string ControllerPath;
            public string PrefabPath;
            public Type BrainType;
            public float HearingSensitivity = 1f;
            /// Run gait speed (m/s). The player sprints at 6: a hunter slower than that can always be outrun.
            public float RunSpeed = 5f;
            public int AvoidancePriority = 50;
            public float AttackDamage = 35f;
            /// Clip name suffix in the FBX -> clean name.  Locomotion: Idle, Sneak, Walk, Run.
            public string Idle = "Idle", Sneak = "Crawl", Walk = "Walk", Run = "Run";
            public string Listen = "Twitch", Alert = "Scream", Attack = "Attack", Hit = "HitReact", Death = "Death";
            public string[] Looping = { "Idle", "Walk", "Run", "Crawl", "Twitch" };
            /// Presence audio (guide §5.6). Empty = none / shared library footsteps.
            public string BreathingLoopPath;
            public float BreathingMaxDistance = 12f;
            public string StepClipsFolder, StepClipsPrefix;
            public Vector2 StepPitch = new(0.8f, 0.95f);
            /// Breathing loop audible only while the agent lies in wait (AgentWaitAudio) instead of always.
            public bool BreathingOnlyWhileWaiting;
            /// Signature one-shots (AgentClicks), e.g. the Tracker's clicks.
            public string ClickClipsFolder, ClickClipsPrefix;
            /// Model fixes: uniform scale for the Model child (e.g. a 6 m export), meshes to hide (props in the FBX).
            public float ModelScale = 1f;
            public string[] HiddenMeshes = new string[0];
            /// Seconds the one-shot states should take; clips are sped up (or slowed, max 2x) to fit (0 = natural length).
            public float AttackSeconds, HitSeconds, AlertSeconds;
            /// Optional frame range per clip (first, last), e.g. one punch out of a take that repeats it.
            public Dictionary<string, Vector2Int> ClipFrames = new();
            /// URP materials built from Assets/Art/Characters/<Name>/Textures and swapped in for the FBX materials.
            public AgentMaterialBuilder.MaterialDef[] Materials = new AgentMaterialBuilder.MaterialDef[0];
            /// Optional: material name shown when badly hurt (AgentDamageLook swaps Materials[0] -> this).
            public string DamagedMaterial;
            /// Clips in separate FBX files (Maya-style "Model@clip.fbx"): folder + file prefix. Empty = clips are in the model.
            public string AnimationFolder, AnimationPrefix;
            /// Blend-tree thresholds (m/s) for Sneak / Walk / Run. Must equal the speed each clip's planted feet move at,
            /// or the body slides over the floor.
            public Vector3 LocomotionSpeeds = new(1.2f, 2.2f, 5f);
            /// AgentAnimatorBridge.footSlideFix: speeds up locomotion above the run threshold (LocomotionSpeeds.z).
            public bool FootSlideFix;
            public string Folder => Path.GetDirectoryName(Path.GetDirectoryName(ModelPath)).Replace('\\', '/');
        }

        public static readonly AgentSpec Listener = new()
        {
            Name = "Listener",
            ModelPath = "Assets/Art/Characters/Listener/Model/ECHO_Creature.fbx",
            ControllerPath = "Assets/Art/Characters/Listener/Animations/Listener.controller",
            PrefabPath = "Assets/Prefabs/Agents/Listener.prefab",
            BrainType = typeof(ListenerBrain),
            HearingSensitivity = 1.15f, RunSpeed = 6f,
            AvoidancePriority = 40,
            AttackDamage = 35f,
            BreathingLoopPath = "Assets/Audio/Agents/Listener/Listener_Breathing_Loop.wav",
            BreathingMaxDistance = 12f,
            StepClipsFolder = "Assets/Audio/Agents/Listener",
            StepClipsPrefix = "Listener_Step_",
            // v2 model (echo-creature/Listener/echo_creature.blend, Tools/Blender/bake_listener_v2.py): modelled ~8 m tall
            // -> 0.28 = 2.3 m (hunched ~2 m). Clips are the Blender action names minus "Listener_".
            ModelScale = 0.28f,
            // Sneak / Walk / Run come from Tools/Blender/listener_locomotion.py: strides computed so the planted feet
            // move at exactly 1.2 / 2.2 / 6 m/s. Crawl (all fours) stays in the FBX but is not blended with upright gaits.
            Idle = "Idle_Breathe", Sneak = "Sneak_Creep", Walk = "Walk_Stalk", Run = "Run_Sprint",
            LocomotionSpeeds = new Vector3(1.2f, 2.2f, 6f),
            Listen = "Search_Listen", Alert = "Scream", Attack = "Attack_Swipe", Hit = "Hit_React", Death = "Death",
            Looping = new[] { "Idle_Breathe", "Idle_Spasm", "Crawl", "Sneak_Creep", "Walk_Stalk", "Run_Sprint", "Search_Listen" },
            AttackSeconds = 1.4f, HitSeconds = 0.9f, AlertSeconds = 1.6f,   // clips are 3.0 / 2.1 / 4.4 s
            // Procedural skin, talons and mouth baked into one 2048 atlas (one material for every mesh).
            Materials = new[]
            {
                new AgentMaterialBuilder.MaterialDef { Name = "Listener_Skin", FbxMaterials = new[] { "Listener_Skin" },
                                                       TexturePrefix = "Listener", NormalStrength = 1.2f, Smoothness = 1f },
            },
        };

        public static readonly AgentSpec Tracker = new()
        {
            Name = "Tracker",
            ModelPath = "Assets/Art/Characters/Tracker/Model/Demon_animated.fbx",
            ControllerPath = "Assets/Art/Characters/Tracker/Animations/Tracker.controller",
            PrefabPath = "Assets/Prefabs/Agents/Tracker.prefab",
            BrainType = typeof(TrackerBrain),
            HearingSensitivity = 1.0f, RunSpeed = 5.5f,
            AvoidancePriority = 45,
            AttackDamage = 35f,
            // Demon_animated.fbx: exported ~5.95 m tall -> 2.4 m. The Ball mesh is a throw prop: hidden.
            ModelScale = 0.4f,
            HiddenMeshes = new[] { "Ball-mesh" },
            Idle = "Idle1", Sneak = "Walk2", Walk = "Walk1", Run = "Run1",
            // Sneak = Walk2's measured foot speed (1.4). Walk stays at the walk gait (2.2): lower, it would blend in Run1,
            // whose 6 strides per loop are out of step with Walk1's 4. Run 5 tested best with footSlideFix (4.4 / 5.5 slide more).
            LocomotionSpeeds = new Vector3(1.4f, 2.2f, 5f),
            Listen = "Telepathic-loop", Alert = "Telepathic", Attack = "Punch1", Hit = "Get-damage", Death = "Death",
            Looping = new[] { "Idle1", "Idle2", "Idle3", "Walk1", "Walk2", "Run1", "Telepathic-loop", "Throw-loop" },
            // Punch1 repeats one 0.8 s punch 6 times: keep the first (frames 0-20, peak at frame 6).
            // 1.6 s puts the peak at ~0.48 s, on AgentBody's 0.5 s claw hit.
            ClipFrames = new() { ["Punch1"] = new Vector2Int(0, 20) },
            AttackSeconds = 1.6f, HitSeconds = 0.8f, AlertSeconds = 1.2f,
            FootSlideFix = true,   // run slide 15 -> 10 %
            ClickClipsFolder = "Assets/Audio/Agents/Tracker",
            ClickClipsPrefix = "Tracker_Click_",
            // Original textures of the "Demon Horror Creature with Weapon" asset (Models/1/Demon_tex), resized to 2048.
            Materials = new[]
            {
                new AgentMaterialBuilder.MaterialDef { Name = "Tracker_Skin", FbxMaterials = new[] { "Demon", "Ball" },
                                                       TexturePrefix = "Tracker_Default", AOTexture = "Tracker_AO.png", Smoothness = 1f },
                new AgentMaterialBuilder.MaterialDef { Name = "Tracker_Skin_Damaged", FbxMaterials = new string[0],
                                                       TexturePrefix = "Tracker_Damaged", AOTexture = "Tracker_AO.png", Smoothness = 1f },
            },
            DamagedMaterial = "Tracker_Skin_Damaged",
        };

        public static readonly AgentSpec Ambusher = new()
        {
            Name = "Ambusher",
            ModelPath = "Assets/Art/Characters/Ambusher/Model/Ambusher_Zombie.fbx",
            AnimationFolder = "Assets/Art/Characters/Ambusher/Animations",
            AnimationPrefix = "Ambusher@",
            ControllerPath = "Assets/Art/Characters/Ambusher/Animations/Ambusher.controller",
            PrefabPath = "Assets/Prefabs/Agents/Ambusher.prefab",
            BrainType = typeof(AmbusherBrain),
            HearingSensitivity = 1.4f, RunSpeed = 5.5f,     // best ears; short explosive lunges (guide §12.1). Run slide: 5.0 0 %, 5.5 2 %, 6.0 10 %, 6.5 17 %
            AvoidancePriority = 55,
            AttackDamage = 50f,
            ModelScale = 0.8f,             // 3.0 m brute -> 2.4 m
            Idle = "idol_breathing", Sneak = "walking", Walk = "walking", Run = "running",
            Listen = "searching", Alert = "scream", Attack = "attack", Hit = "hit", Death = "deth",
            Looping = new[] { "idol", "idol2", "idol_breathing", "walking", "running", "searching", "muscules" },
            AlertSeconds = 1.2f, HitSeconds = 0.8f,
            // Real clip speeds at scale 0.8: walking 1.74, running 3.0 m/s (sneak + walk share the walking clip).
            LocomotionSpeeds = new Vector3(1.74f, 1.75f, 3f),
            FootSlideFix = true,   // playback up to 1.8x keeps the feet planted up to ~5.4 m/s
            // Almost silent (guide §5.6): heavy breathing only within 4 m, and only while it lies in wait (AmbusherBrain).
            BreathingLoopPath = "Assets/Audio/Agents/Ambusher/Ambusher_Breathing_Loop.wav",
            BreathingMaxDistance = 4f,
            StepClipsFolder = "Assets/Audio/Agents/Listener", StepClipsPrefix = "Listener_Step_",
            StepPitch = new Vector2(0.55f, 0.68f),   // same metal-grate steps, pitched down: a heavier body
            BreathingOnlyWhileWaiting = true,
            // "Zombie Monster" package textures (Models/2), resized 4096 -> 2048 (small parts 1024).
            Materials = new[]
            {
                Part("Body", "Mat_Body"), Part("Legs", "Mat_Body1"), Part("Armor", "Mat_Armor"),
                Part("Mask", "Mat_Mask"), Part("Belt_Bolts", "Mat_Belt_Bolts"), Part("Weapon", "Mat_Weapon"),
            },
        };

        public static readonly AgentSpec Mimic = new()
        {
            Name = "Mimic",
            ModelPath = "Assets/Art/Characters/Mimic/Model/Mimic.fbx",
            ControllerPath = "Assets/Art/Characters/Mimic/Animations/Mimic.controller",
            PrefabPath = "Assets/Prefabs/Agents/Mimic.prefab",
            BrainType = typeof(MimicBrain),
            HearingSensitivity = 0.9f, RunSpeed = 5.5f,     // guide §11.2; it flees, it does not chase
            AvoidancePriority = 60,
            AttackDamage = 35f,
            // Old ECHO_Creature body (Models/4, baked in ECHO_stage8_textured.blend), 2.17 m, metres -> scale 1.
            // Every clip is generated by Tools/Blender/mimic_animations.py: leg IK with heel/ball roll, planted feet move
            // at exactly 1.2 / 2.2 / 5.5 m/s, spring-lagged arms, loops are exact periods.
            Idle = "Idle", Sneak = "Sneak", Walk = "Walk", Run = "Run",
            LocomotionSpeeds = new Vector3(1.2f, 2.2f, 5.5f),
            // Alert = "Call": the mimicry vocalisation (MimicBrain fires the fake sound 0.75 s in, jaw fully open).
            // The startle clip "Alert" stays in the FBX, unused for now.
            Listen = "Listen", Alert = "Call", Attack = "Attack", Hit = "Hit", Death = "Death",
            Looping = new[] { "Idle", "Sneak", "Walk", "Run", "Listen" },
            AttackSeconds = 1.6f,   // claw strike is at 0.44 s of 1.4 s -> lands on AgentBody's 0.5 s hit
            StepClipsFolder = "Assets/Audio/Agents/Listener", StepClipsPrefix = "Listener_Step_",
            StepPitch = new Vector2(1.05f, 1.2f),   // light, bony feet: the Listener's steps pitched up
            Materials = new[]
            {
                new AgentMaterialBuilder.MaterialDef { Name = "Mimic_Skin", FbxMaterials = new[] { "Mimic_Skin", "Mimic_Mouth" },
                                                       TexturePrefix = "Mimic", NormalStrength = 1.6f, Smoothness = 1f },
                new AgentMaterialBuilder.MaterialDef { Name = "Mimic_Teeth", FbxMaterials = new[] { "Mimic_Teeth" },
                                                       Color = new Color(0.72f, 0.65f, 0.5f), Smoothness = 0.55f },
                new AgentMaterialBuilder.MaterialDef { Name = "Mimic_Eye", FbxMaterials = new[] { "Mimic_Eye" },
                                                       Color = new Color(0.02f, 0.01f, 0.01f), Smoothness = 0.95f,
                                                       Emission = new Color(0.30f, 0.22f, 0.04f) },
            },
        };

        static AgentMaterialBuilder.MaterialDef Part(string part, string fbxMaterial) => new()
        {
            Name = "Ambusher_" + part, FbxMaterials = new[] { fbxMaterial },
            TexturePrefix = "Ambusher_" + part, AOTexture = $"Ambusher_{part}_AO.png", Smoothness = 1f,
        };

        [MenuItem("ECHO/Agents/Listener/1. Build Model, Animator and Prefab")]
        static void BuildListener() => Build(Listener);

        [MenuItem("ECHO/Agents/Tracker/1. Build Model, Animator and Prefab")]
        static void BuildTracker() => Build(Tracker);

        [MenuItem("ECHO/Agents/Ambusher/1. Build Model, Animator and Prefab")]
        static void BuildAmbusher() => Build(Ambusher);

        [MenuItem("ECHO/Agents/Mimic/1. Build Model, Animator and Prefab")]
        static void BuildMimic() => Build(Mimic);

        public static GameObject Build(AgentSpec spec)
        {
            EnsureLayers();
            var clips = ConfigureModel(spec);
            var controller = BuildController(spec, clips);
            var prefab = BuildPrefab(spec, controller);
            Debug.Log($"[AgentAssetBuilder] {spec.Name}: {clips.Count} clips, controller {spec.ControllerPath}, prefab {spec.PrefabPath}");
            return prefab;
        }

        // ---------------------------------------------------------------- layers

        /// Guide §3.1 layers. Slot 8 is left alone: in SampleScene floors and the player already share it.
        public static void EnsureLayers()
        {
            var tagManager = new SerializedObject(AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/TagManager.asset")[0]);
            var layers = tagManager.FindProperty("layers");
            bool changed = false;
            changed |= SetLayer(layers, 9, EnvironmentLayer);
            changed |= SetLayer(layers, 10, AgentsLayer);
            if (changed) tagManager.ApplyModifiedPropertiesWithoutUndo();
        }

        static bool SetLayer(SerializedProperty layers, int index, string name)
        {
            for (int i = 0; i < layers.arraySize; i++)
                if (layers.GetArrayElementAtIndex(i).stringValue == name) return false;   // already exists somewhere
            var slot = layers.GetArrayElementAtIndex(index);
            if (!string.IsNullOrEmpty(slot.stringValue))
                throw new InvalidOperationException($"Layer slot {index} is '{slot.stringValue}', wanted '{name}'. Fix Tags & Layers by hand.");
            slot.stringValue = name;
            return true;
        }

        // ---------------------------------------------------------------- 1. model import

        static Dictionary<string, AnimationClip> ConfigureModel(AgentSpec spec)
        {
            var importer = (ModelImporter)AssetImporter.GetAtPath(spec.ModelPath)
                           ?? throw new FileNotFoundException("Model not found", spec.ModelPath);

            importer.animationType = ModelImporterAnimationType.Generic;   // creature rig, not a Mixamo humanoid
            importer.importAnimation = true;
            // Default reduction (0.5 deg / 0.5 %) lets planted feet creep; keep the keys close to the source.
            importer.animationCompression = ModelImporterAnimationCompression.KeyframeReduction;
            importer.animationRotationError = 0.1f;
            importer.animationPositionError = 0.1f;
            var source = importer.defaultClipAnimations;
            var clips = new ModelImporterClipAnimation[source.Length];
            for (int i = 0; i < source.Length; i++)
            {
                var c = source[i];
                c.name = CleanName(c.takeName);
                if (spec.ClipFrames.TryGetValue(c.name, out var range)) { c.firstFrame = range.x; c.lastFrame = range.y; }
                c.loopTime = Array.IndexOf(spec.Looping, c.name) >= 0;
                c.loopPose = false;
                c.lockRootRotation = c.lockRootHeightY = c.lockRootPositionXZ = false;
                clips[i] = c;
            }
            importer.clipAnimations = clips;
            foreach (var def in spec.Materials)
            {
                var mat = AgentMaterialBuilder.Build(spec.Folder, def);
                foreach (var fbxMat in def.FbxMaterials) AgentMaterialBuilder.Remap(importer, fbxMat, mat);
            }
            importer.SaveAndReimport();

            var result = new Dictionary<string, AnimationClip>();
            foreach (var a in AssetDatabase.LoadAllAssetsAtPath(spec.ModelPath))
                if (a is AnimationClip clip && !clip.name.StartsWith("__preview")) result[clip.name] = clip;
            if (!string.IsNullOrEmpty(spec.AnimationFolder)) ConfigureAnimationFiles(spec, result);
            return result;
        }

        /// One clip per file: "Ambusher@walking.fbx" -> clip "walking". Generic, no materials/meshes needed from these files.
        static void ConfigureAnimationFiles(AgentSpec spec, Dictionary<string, AnimationClip> result)
        {
            foreach (var guid in AssetDatabase.FindAssets("t:Model", new[] { spec.AnimationFolder }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                string file = Path.GetFileNameWithoutExtension(path);
                if (!file.StartsWith(spec.AnimationPrefix)) continue;
                string clipName = file.Substring(spec.AnimationPrefix.Length);
                var imp = (ModelImporter)AssetImporter.GetAtPath(path);
                imp.animationType = ModelImporterAnimationType.Generic;
                imp.importAnimation = true;
                imp.materialImportMode = ModelImporterMaterialImportMode.None;
                var src = imp.defaultClipAnimations;
                if (src.Length == 0) continue;
                var c = src[0];
                c.name = clipName;
                c.loopTime = Array.IndexOf(spec.Looping, clipName) >= 0;
                c.lockRootRotation = c.lockRootHeightY = c.lockRootPositionXZ = false;
                imp.clipAnimations = new[] { c };
                imp.SaveAndReimport();
                foreach (var a in AssetDatabase.LoadAllAssetsAtPath(path))
                    if (a is AnimationClip clip && clip.name == clipName) result[clipName] = clip;
            }
        }

        /// "ECHO_Rig|ECHO_Walk" -> "Walk"
        static string CleanName(string take)
        {
            string n = take.Contains("|") ? take.Substring(take.LastIndexOf('|') + 1) : take;
            if (n.StartsWith("ECHO_")) n = n.Substring(5);
            return n;
        }

        // ---------------------------------------------------------------- 2. animator controller

        static AnimatorController BuildController(AgentSpec spec, Dictionary<string, AnimationClip> clips)
        {
            AnimationClip Clip(string name) => clips.TryGetValue(name, out var c) ? c
                : throw new InvalidOperationException($"{spec.ModelPath} has no clip '{name}'. Clips: {string.Join(", ", clips.Keys)}");

            Directory.CreateDirectory(Path.GetDirectoryName(spec.ControllerPath));
            AssetDatabase.DeleteAsset(spec.ControllerPath);   // rebuild from scratch: no stale states
            var ac = AnimatorController.CreateAnimatorControllerAtPath(spec.ControllerPath);
            ac.AddParameter("Speed", AnimatorControllerParameterType.Float);
            ac.AddParameter("Listening", AnimatorControllerParameterType.Bool);
            ac.AddParameter("Alert", AnimatorControllerParameterType.Trigger);
            ac.AddParameter("Attack", AnimatorControllerParameterType.Trigger);
            ac.AddParameter("Hit", AnimatorControllerParameterType.Trigger);
            ac.AddParameter("Dead", AnimatorControllerParameterType.Bool);

            var sm = ac.layers[0].stateMachine;

            // Locomotion: 1D blend on NavMeshAgent speed (guide §8.5: 0 idle, 1.2 sneak, 2.2 walk, 5 run).
            var loco = ac.CreateBlendTreeInController("Locomotion", out var tree, 0);
            tree.blendType = BlendTreeType.Simple1D;
            tree.blendParameter = "Speed";
            tree.useAutomaticThresholds = false;
            tree.AddChild(Clip(spec.Idle), 0f);
            tree.AddChild(Clip(spec.Sneak), spec.LocomotionSpeeds.x);
            tree.AddChild(Clip(spec.Walk), spec.LocomotionSpeeds.y);
            tree.AddChild(Clip(spec.Run), spec.LocomotionSpeeds.z);
            sm.defaultState = loco;
            loco.writeDefaultValues = true;

            var listen = State(sm, "Listen", Clip(spec.Listen), new Vector3(300, 160));
            var alert = State(sm, "Alert", Clip(spec.Alert), new Vector3(300, -60), spec.AlertSeconds);
            var attack = State(sm, "Attack", Clip(spec.Attack), new Vector3(560, 0), spec.AttackSeconds);
            var hit = State(sm, "Hit", Clip(spec.Hit), new Vector3(560, 80), spec.HitSeconds);
            var death = State(sm, "Death", Clip(spec.Death), new Vector3(560, 200));

            // Listening pose while standing still.
            var toListen = loco.AddTransition(listen);
            Configure(toListen, 0.25f);
            toListen.AddCondition(AnimatorConditionMode.If, 0, "Listening");
            toListen.AddCondition(AnimatorConditionMode.Less, 0.3f, "Speed");
            var stopListen = listen.AddTransition(loco); Configure(stopListen, 0.25f);
            stopListen.AddCondition(AnimatorConditionMode.IfNot, 0, "Listening");
            var walkOff = listen.AddTransition(loco); Configure(walkOff, 0.2f);
            walkOff.AddCondition(AnimatorConditionMode.Greater, 0.3f, "Speed");

            // One-shots return to locomotion near their end.
            ReturnToLoco(alert, loco, 0.85f);
            ReturnToLoco(attack, loco, 0.85f);
            ReturnToLoco(hit, loco, 0.8f);

            // Any State: alert / attack / hit (never when dead), death.
            AnyState(sm, alert, "Alert", 0.15f);
            AnyState(sm, attack, "Attack", 0.1f);
            AnyState(sm, hit, "Hit", 0.08f);
            var die = sm.AddAnyStateTransition(death);
            Configure(die, 0.15f); die.canTransitionToSelf = false;
            die.AddCondition(AnimatorConditionMode.If, 0, "Dead");

            EditorUtility.SetDirty(ac);
            AssetDatabase.SaveAssets();
            return ac;
        }

        static AnimatorState State(AnimatorStateMachine sm, string name, AnimationClip clip, Vector3 pos, float seconds = 0f)
        {
            var s = sm.AddState(name, pos);
            s.motion = clip;
            if (seconds > 0f) s.speed = Mathf.Max(0.5f, clip.length / seconds);   // fit clips to gameplay timing (slow down at most 2x)
            return s;
        }

        static void Configure(AnimatorStateTransition t, float duration)
        {
            t.hasExitTime = false; t.duration = duration; t.hasFixedDuration = true;
        }

        static void ReturnToLoco(AnimatorState from, AnimatorState loco, float exitTime)
        {
            var t = from.AddTransition(loco);
            t.hasExitTime = true; t.exitTime = exitTime; t.duration = 0.2f; t.hasFixedDuration = true;
        }

        static void AnyState(AnimatorStateMachine sm, AnimatorState to, string trigger, float duration)
        {
            var t = sm.AddAnyStateTransition(to);
            Configure(t, duration); t.canTransitionToSelf = false;
            t.AddCondition(AnimatorConditionMode.If, 0, trigger);
            t.AddCondition(AnimatorConditionMode.IfNot, 0, "Dead");
        }

        // ---------------------------------------------------------------- 3. prefab

        static GameObject BuildPrefab(AgentSpec spec, AnimatorController controller)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(spec.PrefabPath));
            int agentsLayer = LayerMask.NameToLayer(AgentsLayer);

            var root = new GameObject(spec.Name);
            try
            {
                root.layer = agentsLayer;

                var nav = root.AddComponent<NavMeshAgent>();   // guide §8.2 settings
                nav.radius = 0.4f; nav.height = 2f; nav.baseOffset = 0f;
                nav.speed = 2.2f; nav.angularSpeed = 360f; nav.acceleration = 12f;
                nav.stoppingDistance = 0.5f; nav.autoBraking = true;
                nav.obstacleAvoidanceType = ObstacleAvoidanceType.MedQualityObstacleAvoidance;
                nav.avoidancePriority = spec.AvoidancePriority;

                var rb = root.AddComponent<Rigidbody>();      // kinematic: needed for trigger contacts, moved by the agent
                rb.isKinematic = true; rb.useGravity = false;

                var capsule = root.AddComponent<CapsuleCollider>();   // what bullets hit
                capsule.center = new Vector3(0f, 1f, 0f); capsule.height = 2f; capsule.radius = 0.4f;

                root.AddComponent<Hearing>().sensitivity = spec.HearingSensitivity;
                root.AddComponent<AgentMotor>().runSpeed = spec.RunSpeed;
                var body = root.AddComponent<AgentBody>();
                body.attackDamage = spec.AttackDamage;
                // Everything: a mask like ~Agents is saved with only *named* layers, which would drop the player's
                // unnamed layer 8. The body skips its own colliders anyway.
                body.playerMask = ~0;
                root.AddComponent(spec.BrainType);
                var steps = root.AddComponent<AgentFootsteps>();

                // Model
                var modelAsset = AssetDatabase.LoadAssetAtPath<GameObject>(spec.ModelPath);
                var model = (GameObject)PrefabUtility.InstantiatePrefab(modelAsset);
                model.name = "Model";
                model.transform.SetParent(root.transform, false);
                model.transform.localScale = Vector3.one * spec.ModelScale;
                foreach (var hidden in spec.HiddenMeshes)
                {
                    var h = model.transform.Find(hidden);
                    if (h != null && h.TryGetComponent(out Renderer r)) r.enabled = false;
                }
                SetLayerRecursive(model, agentsLayer);
                if (!model.TryGetComponent(out Animator animator)) animator = model.AddComponent<Animator>();   // never ?? on Unity objects
                animator.runtimeAnimatorController = controller;
                animator.applyRootMotion = false;
                animator.cullingMode = AnimatorCullingMode.CullUpdateTransforms;

                var bridge = root.AddComponent<AgentAnimatorBridge>();
                bridge.animator = animator;
                bridge.footSlideFix = spec.FootSlideFix;
                bridge.blendTopSpeed = spec.LocomotionSpeeds.z;   // fastest clip in the blend tree

                // Touch sense
                var touch = new GameObject("ContactSense");
                touch.transform.SetParent(root.transform, false);
                touch.transform.localPosition = new Vector3(0f, 1f, 0f);
                touch.layer = LayerMask.NameToLayer("Ignore Raycast");   // bullets and sound linecasts pass through
                var sphere = touch.AddComponent<SphereCollider>();
                sphere.isTrigger = true; sphere.radius = 1.3f;
                touch.AddComponent<ContactSense>();

                // Presence audio: slow breathing (loop) and heavy steps, both muffled behind walls for the player.
                var breathing = string.IsNullOrEmpty(spec.BreathingLoopPath) ? null
                              : AssetDatabase.LoadAssetAtPath<AudioClip>(spec.BreathingLoopPath);
                if (breathing != null)
                {
                    var voice = AudioChild(root.transform, "Voice", 1.6f, 1.5f, spec.BreathingMaxDistance);
                    voice.clip = breathing; voice.loop = true; voice.playOnAwake = true; voice.volume = 0.7f;
                    if (spec.BreathingOnlyWhileWaiting) root.AddComponent<AgentWaitAudio>().source = voice;
                }
                if (!string.IsNullOrEmpty(spec.StepClipsFolder))
                {
                    steps.clips = SoundLibraryBuilder.Clips(spec.StepClipsFolder, spec.StepClipsPrefix);
                    if (steps.clips.Length > 0) steps.source = AudioChild(root.transform, "Feet", 0.1f, 1f, 18f);
                    steps.pitch = spec.StepPitch;
                }

                if (!string.IsNullOrEmpty(spec.DamagedMaterial) && spec.Materials.Length > 0)
                {
                    var look = root.AddComponent<AgentDamageLook>();
                    look.healthy = AssetDatabase.LoadAssetAtPath<Material>($"{spec.Folder}/Materials/{spec.Materials[0].Name}.mat");
                    look.damaged = AssetDatabase.LoadAssetAtPath<Material>($"{spec.Folder}/Materials/{spec.DamagedMaterial}.mat");
                }

                if (!string.IsNullOrEmpty(spec.ClickClipsFolder))
                {
                    var clicks = root.AddComponent<AgentClicks>();
                    clicks.clips = SoundLibraryBuilder.Clips(spec.ClickClipsFolder, spec.ClickClipsPrefix);
                    clicks.source = AudioChild(root.transform, "Clicks", 1.8f, 1f, 14f);
                }

                return PrefabUtility.SaveAsPrefabAsset(root, spec.PrefabPath);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        static AudioSource AudioChild(Transform parent, string name, float height, float min, float max)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = new Vector3(0f, height, 0f);
            var s = go.AddComponent<AudioSource>();
            s.spatialBlend = 1f; s.dopplerLevel = 0f; s.playOnAwake = false;
            s.rolloffMode = AudioRolloffMode.Linear;   // silent beyond max distance: a fair, readable cue
            s.minDistance = min; s.maxDistance = max;
            go.AddComponent<AudioLowPassFilter>();
            go.AddComponent<AudioOcclusion>();
            return s;
        }

        static void SetLayerRecursive(GameObject go, int layer)
        {
            go.layer = layer;
            foreach (Transform t in go.transform) SetLayerRecursive(t.gameObject, layer);
        }
    }
}
