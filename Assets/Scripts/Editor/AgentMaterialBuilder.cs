using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Echo.EditorTools
{
    /// Builds URP Lit materials for an agent from its texture files and remaps the FBX's own materials to them
    /// (the FBX keeps its embedded placeholders; the remap swaps them at import). Re-runnable.
    ///   Textures: Assets/Art/Characters/<Agent>/Textures/<Prefix>_BaseColor / _Normal / _MetallicSmoothness / _AO .png
    ///   Materials: Assets/Art/Characters/<Agent>/Materials/<name>.mat
    public static class AgentMaterialBuilder
    {
        public class MaterialDef
        {
            public string Name;                    // output material name
            public string[] FbxMaterials;          // materials inside the FBX to replace with this one
            public string TexturePrefix;           // e.g. "Tracker_Default" -> Tracker_Default_BaseColor.png ...
            public string AOTexture;               // optional, file name in the Textures folder
            public Color Color = Color.white;      // tint (multiplies the base map) or flat colour without one
            public float Smoothness = 0.5f;        // used without a MetallicSmoothness map; a scale with one
            public float Metallic = 0f;
            public float NormalStrength = 1f;
            public Color Emission = Color.black;
        }

        public static Material Build(string folder, MaterialDef def)
        {
            string tex = folder + "/Textures/", matDir = folder + "/Materials";
            Directory.CreateDirectory(matDir);
            string path = $"{matDir}/{def.Name}.mat";
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null)
            {
                mat = new Material(Shader.Find("Universal Render Pipeline/Lit"));
                AssetDatabase.CreateAsset(mat, path);
            }

            Texture2D Tex(string file, TextureImporterType type, bool srgb)
            {
                string p = tex + file;
                if (!File.Exists(p)) return null;
                var imp = (TextureImporter)AssetImporter.GetAtPath(p);
                bool dirty = imp.textureType != type || imp.sRGBTexture != srgb || imp.maxTextureSize != 2048;
                if (dirty)
                {
                    imp.textureType = type; imp.sRGBTexture = srgb; imp.maxTextureSize = 2048;
                    imp.mipmapEnabled = true; imp.anisoLevel = 4;
                    imp.SaveAndReimport();
                }
                return AssetDatabase.LoadAssetAtPath<Texture2D>(p);
            }

            Texture2D baseMap = null, normal = null, ms = null, ao = null;
            if (!string.IsNullOrEmpty(def.TexturePrefix))
            {
                baseMap = Tex(def.TexturePrefix + "_BaseColor.png", TextureImporterType.Default, true);
                normal = Tex(def.TexturePrefix + "_Normal.png", TextureImporterType.NormalMap, false);
                ms = Tex(def.TexturePrefix + "_MetallicSmoothness.png", TextureImporterType.Default, false);
            }
            if (!string.IsNullOrEmpty(def.AOTexture)) ao = Tex(def.AOTexture, TextureImporterType.Default, false);

            mat.SetTexture("_BaseMap", baseMap);
            mat.SetColor("_BaseColor", def.Color);
            mat.SetTexture("_BumpMap", normal);
            mat.SetFloat("_BumpScale", def.NormalStrength);
            Keyword(mat, "_NORMALMAP", normal != null);
            mat.SetTexture("_MetallicGlossMap", ms);
            Keyword(mat, "_METALLICSPECGLOSSMAP", ms != null);
            mat.SetFloat("_Metallic", def.Metallic);
            mat.SetFloat("_Smoothness", def.Smoothness);
            mat.SetFloat("_SmoothnessTextureChannel", 0);   // smoothness from the metallic map's alpha
            mat.SetTexture("_OcclusionMap", ao);
            Keyword(mat, "_OCCLUSIONMAP", ao != null);
            bool glow = def.Emission.maxColorComponent > 0f;
            mat.SetColor("_EmissionColor", def.Emission);
            Keyword(mat, "_EMISSION", glow);
            mat.globalIlluminationFlags = glow ? MaterialGlobalIlluminationFlags.RealtimeEmissive : MaterialGlobalIlluminationFlags.EmissiveIsBlack;
            EditorUtility.SetDirty(mat);
            return mat;
        }

        /// Replace the FBX's embedded materials by name. Returns true if the importer changed (needs reimport).
        public static bool Remap(ModelImporter importer, string fbxMaterial, Material mat)
        {
            var id = new AssetImporter.SourceAssetIdentifier(typeof(Material), fbxMaterial);
            if (importer.GetExternalObjectMap().TryGetValue(id, out var current) && current == mat) return false;
            importer.AddRemap(id, mat);
            return true;
        }

        static void Keyword(Material m, string k, bool on) { if (on) m.EnableKeyword(k); else m.DisableKeyword(k); }
    }
}
