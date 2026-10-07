using UnityEditor;
using UnityEngine;
using System.IO;

/// <summary>Restores the URP/Lit shader and texture bindings for the imported Abandoned Building pack.</summary>
[InitializeOnLoad]
public static class RepairAbandonedBuildingMaterials
{
    private const string MaterialsFolder = "Assets/Abandoned_Building/Materials";

    static RepairAbandonedBuildingMaterials()
    {
        // Run as soon as Unity finishes compiling this repair tool, so no manual editor steps are needed.
        EditorApplication.delayCall += Repair;
    }

    [MenuItem("Tools/ECHO/Repair Abandoned Building Materials")]
    public static void Repair()
    {
        var shader = Shader.Find("Universal Render Pipeline/Lit");
        if (shader == null)
        {
            Debug.LogError("URP/Lit was not found. Enable the Universal Render Pipeline before running this repair.");
            return;
        }

        var guids = AssetDatabase.FindAssets("t:Material", new[] { MaterialsFolder });
        var repaired = 0;

        foreach (var guid in guids)
        {
            var path = AssetDatabase.GUIDToAssetPath(guid);
            var material = AssetDatabase.LoadAssetAtPath<Material>(path);
            // Only touch materials whose source shader GUID was absent from the imported package.
            if (material == null || !File.ReadAllText(path).Contains("m_Shader: {fileID: 4800000, guid: abc"))
                continue;

            // The source shader stored its maps in generic slots: 0 = normal, 1 = albedo, 2 = mask.
            var normal = material.GetTexture("Material_Texture2D_0") ?? material.GetTexture("_BumpMap");
            var albedo = material.GetTexture("Material_Texture2D_1") ?? material.GetTexture("_BaseMap") ?? material.GetTexture("_MainTex");
            var mask = material.GetTexture("Material_Texture2D_2") ?? material.GetTexture("_MetallicGlossMap");

            material.shader = shader;
            material.SetTexture("_BaseMap", albedo);
            material.SetTexture("_MainTex", albedo);
            material.SetTexture("_BumpMap", normal);
            material.SetTexture("_MetallicGlossMap", mask);
            material.SetFloat("_Smoothness", 0.25f);
            material.EnableKeyword("_NORMALMAP");
            if (mask != null)
                material.EnableKeyword("_METALLICSPECGLOSSMAP");

            EditorUtility.SetDirty(material);
            repaired++;
        }

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        Debug.Log($"Repaired {repaired} Abandoned Building materials.");
    }
}

