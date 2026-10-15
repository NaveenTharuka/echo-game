using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Echo.Sound;
using UnityEditor;
using UnityEngine;

namespace Echo.EditorTools
{
    /// ECHO > Audio > Build Sound Library. (Re)fills Assets/Audio/EchoSoundLibrary.asset from the folders:
    ///   Assets/Audio/SFX/<SoundType>/*.wav   -> the entry for that SoundType (folder name = enum name)
    ///   Assets/Audio/Agents/Common/AgentVoice_*.wav -> AgentVoice
    /// Drop more clips into a folder and run it again. Volumes / distances per type are in `Mix` below.
    public static class SoundLibraryBuilder
    {
        public const string LibraryPath = "Assets/Audio/EchoSoundLibrary.asset";

        // volume, minDistance, maxDistance (m). Loud events carry far; footsteps stay local.
        static readonly Dictionary<SoundType, (float vol, float min, float max)> Mix = new()
        {
            [SoundType.Footstep] = (0.45f, 1f, 12f),
            [SoundType.Run] = (0.6f, 1f, 18f),
            [SoundType.Jump] = (0.5f, 1f, 12f),
            [SoundType.Land] = (0.7f, 1f, 20f),
            [SoundType.Gunshot] = (1f, 3f, 60f),
            [SoundType.Reload] = (0.6f, 1f, 10f),
            [SoundType.BulletImpact] = (0.7f, 1.5f, 30f),
            [SoundType.Door] = (0.8f, 2f, 30f),
            [SoundType.MetalImpact] = (0.8f, 1.5f, 35f),
            [SoundType.GlassBreak] = (0.9f, 2f, 40f),
            [SoundType.ObjectDrop] = (0.7f, 1.5f, 25f),
            [SoundType.Machinery] = (0.4f, 2f, 20f),
            [SoundType.Generator] = (0.9f, 3f, 40f),
            [SoundType.Switch] = (0.6f, 1f, 15f),
            [SoundType.AgentVoice] = (0.9f, 2f, 25f),
        };

        [MenuItem("ECHO/Audio/Build Sound Library")]
        static void BuildMenu()
        {
            var lib = Build();
            Selection.activeObject = lib;
        }

        public static SoundLibrary Build()
        {
            var lib = AssetDatabase.LoadAssetAtPath<SoundLibrary>(LibraryPath);
            if (lib == null)
            {
                lib = ScriptableObject.CreateInstance<SoundLibrary>();
                AssetDatabase.CreateAsset(lib, LibraryPath);
            }

            // Keep any mixer groups assigned by hand.
            var groups = (lib.entries ?? new SoundLibrary.Entry[0]).Where(e => e != null && e.group != null)
                                                                   .ToDictionary(e => e.type, e => e.group);
            var entries = new List<SoundLibrary.Entry>();
            var missing = new List<string>();
            foreach (SoundType t in Enum.GetValues(typeof(SoundType)))
            {
                var clips = t == SoundType.AgentVoice
                    ? Clips("Assets/Audio/Agents/Common", "AgentVoice_")
                    : Clips("Assets/Audio/SFX/" + t, "");
                if (clips.Length == 0) { missing.Add(t.ToString()); continue; }
                var (vol, min, max) = Mix.TryGetValue(t, out var m) ? m : (0.8f, 1f, 25f);
                entries.Add(new SoundLibrary.Entry
                {
                    type = t, clips = clips, volume = vol, minDistance = min, maxDistance = max,
                    pitch = t == SoundType.Gunshot ? new Vector2(0.97f, 1.03f) : new Vector2(0.92f, 1.08f),
                    group = groups.TryGetValue(t, out var g) ? g : null,
                });
            }
            lib.entries = entries.ToArray();
            EditorUtility.SetDirty(lib);
            AssetDatabase.SaveAssets();
            Debug.Log($"[SoundLibraryBuilder] {entries.Count} sound types, {entries.Sum(e => e.clips.Length)} clips. " +
                      (missing.Count > 0 ? "No clips yet for: " + string.Join(", ", missing) : "All types covered."));
            return lib;
        }

        public static AudioClip[] Clips(string folder, string prefix)
        {
            if (!AssetDatabase.IsValidFolder(folder)) return new AudioClip[0];
            return AssetDatabase.FindAssets("t:AudioClip", new[] { folder })
                .Select(AssetDatabase.GUIDToAssetPath)
                .Where(p => Path.GetFileName(p).StartsWith(prefix))
                .OrderBy(p => p)
                .Select(AssetDatabase.LoadAssetAtPath<AudioClip>)
                .ToArray();
        }
    }
}
