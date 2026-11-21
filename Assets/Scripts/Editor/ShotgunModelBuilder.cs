using Echo.Player;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Echo.EditorTools
{
    /// ECHO > Player > Build Shotgun Model: the first-person pump shotgun, assembled from Unity's built-in cube /
    /// cylinder / sphere meshes (no new mesh assets; 26 parts, ~1.7k triangles) with three URP Lit materials -
    /// blued steel, walnut, black rubber. Saved as Resources/Echo/Weapons/Shotgun.prefab: HitscanWeapon loads it
    /// when its modelPrefab is empty, and Resources guarantees the prefab and its materials are in the player build.
    /// Root = the pivot HitscanWeapon places at modelOffset (camera space), +Z = forward. With the camera's 0.3 m near
    /// plane, only the part beyond ~0.35 m from the eye is on screen (receiver, pump, barrel); the stock is modelled
    /// for completeness but stays below the frame. Re-runnable: overwrites the prefab and updates the materials.
    public static class ShotgunModelBuilder
    {
        const string MaterialFolder = "Assets/Art/Materials/Weapons";
        const string PrefabFolder = "Assets/Resources/Echo/Weapons";
        const string PrefabPath = PrefabFolder + "/Shotgun.prefab";

        [MenuItem("ECHO/Player/Build Shotgun Model")]
        public static void Build()
        {
            EnsureFolder(MaterialFolder);
            EnsureFolder(PrefabFolder);
            Material steel = LitMaterial("Shotgun_Steel", new Color(0.075f, 0.078f, 0.085f), 0.85f, 0.55f);
            Material walnut = LitMaterial("Shotgun_Walnut", new Color(0.11f, 0.06f, 0.032f), 0f, 0.4f);
            Material rubber = LitMaterial("Shotgun_Rubber", new Color(0.025f, 0.025f, 0.027f), 0f, 0.2f);

            var root = new GameObject("Shotgun");
            Transform r = root.transform;

            // Receiver: flat-sided steel block with the ejection port on the right and a sighting rib on top.
            Box(r, "Receiver", new Vector3(0f, 0f, -0.03f), new Vector3(0.04f, 0.062f, 0.2f), steel);
            Box(r, "TopRib", new Vector3(0f, 0.033f, -0.03f), new Vector3(0.014f, 0.004f, 0.2f), steel);
            Box(r, "EjectionPort", new Vector3(0.0205f, 0.01f, 0f), new Vector3(0.002f, 0.022f, 0.06f), rubber);
            Box(r, "LoadingPort", new Vector3(0f, -0.0315f, 0.02f), new Vector3(0.024f, 0.002f, 0.07f), rubber);

            // Barrel above the magazine tube, joined by a clamp near the muzzle.
            Rod(r, "Barrel", new Vector3(0f, 0.019f, 0.24f), 0.02f, 0.02f, 0.36f, steel);
            Rod(r, "Bore", new Vector3(0f, 0.019f, 0.4205f), 0.012f, 0.012f, 0.002f, rubber);
            Rod(r, "MagazineTube", new Vector3(0f, -0.004f, 0.21f), 0.02f, 0.02f, 0.3f, steel);
            Rod(r, "TubeCap", new Vector3(0f, -0.004f, 0.367f), 0.023f, 0.023f, 0.016f, steel);
            Box(r, "BarrelClamp", new Vector3(0f, 0.007f, 0.345f), new Vector3(0.024f, 0.038f, 0.014f), steel);
            Ball(r, "FrontBead", new Vector3(0f, 0.031f, 0.405f), 0.006f, steel);

            // Pump: walnut fore-end with grip grooves and the two action bars into the receiver; ShotgunViewmodel
            // slides it back and forward after each shot.
            var pump = new GameObject("Pump").transform;
            pump.SetParent(r, false);
            pump.localPosition = new Vector3(0f, -0.003f, 0.17f);
            Rod(pump, "ForeEnd", Vector3.zero, 0.044f, 0.038f, 0.15f, walnut);
            for (int i = 0; i < 5; i++)
                Rod(pump, "Groove" + i, new Vector3(0f, 0f, -0.05f + 0.025f * i), 0.0455f, 0.0395f, 0.004f, rubber);
            Box(pump, "ActionBarL", new Vector3(-0.017f, -0.005f, -0.12f), new Vector3(0.003f, 0.006f, 0.1f), steel);
            Box(pump, "ActionBarR", new Vector3(0.017f, -0.005f, -0.12f), new Vector3(0.003f, 0.006f, 0.1f), steel);

            // Trigger group under the receiver.
            Box(r, "GuardBottom", new Vector3(0f, -0.062f, -0.075f), new Vector3(0.01f, 0.005f, 0.075f), steel);
            Box(r, "GuardFront", new Vector3(0f, -0.047f, -0.04f), new Vector3(0.01f, 0.03f, 0.005f), steel);
            Box(r, "GuardRear", new Vector3(0f, -0.047f, -0.11f), new Vector3(0.01f, 0.03f, 0.005f), steel);
            Box(r, "Trigger", new Vector3(0f, -0.044f, -0.07f), new Vector3(0.005f, 0.022f, 0.005f), steel, new Vector3(15f, 0f, 0f));
            Box(r, "SafetyButton", new Vector3(0f, -0.031f, -0.115f), new Vector3(0.012f, 0.004f, 0.006f), rubber);

            // Stock: wrist and butt sloping down to the rear, rubber pad.
            Box(r, "StockWrist", new Vector3(0f, -0.018f, -0.18f), new Vector3(0.034f, 0.05f, 0.12f), walnut, new Vector3(-10f, 0f, 0f));
            Box(r, "Stock", new Vector3(0f, -0.045f, -0.33f), new Vector3(0.038f, 0.075f, 0.2f), walnut, new Vector3(-8f, 0f, 0f));
            Box(r, "ButtPad", new Vector3(0f, -0.06f, -0.435f), new Vector3(0.04f, 0.08f, 0.02f), rubber, new Vector3(-8f, 0f, 0f));

            var muzzle = new GameObject("Muzzle").transform;   // HitscanWeapon: flash + gunshot position
            muzzle.SetParent(r, false);
            muzzle.localPosition = new Vector3(0f, 0.019f, 0.425f);

            var motion = root.AddComponent<ShotgunViewmodel>();
            motion.pump = pump;
            motion.muzzleReach = muzzle.localPosition.z;

            PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
            Object.DestroyImmediate(root);
            AssetDatabase.SaveAssets();
            Debug.Log($"[Shotgun] saved {PrefabPath}");
        }

        static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            int slash = path.LastIndexOf('/');
            string parent = path.Substring(0, slash);
            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, path.Substring(slash + 1));
        }

        static Material LitMaterial(string name, Color color, float metallic, float smoothness)
        {
            string path = $"{MaterialFolder}/{name}.mat";
            var m = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (m == null)
            {
                m = new Material(Shader.Find("Universal Render Pipeline/Lit"));
                AssetDatabase.CreateAsset(m, path);
            }
            m.SetColor("_BaseColor", color);
            m.SetFloat("_Metallic", metallic);
            m.SetFloat("_Smoothness", smoothness);
            EditorUtility.SetDirty(m);
            return m;
        }

        static Transform Part(PrimitiveType type, Transform parent, string name, Vector3 pos, Vector3 scale, Material mat, Vector3 euler)
        {
            var go = GameObject.CreatePrimitive(type);
            Object.DestroyImmediate(go.GetComponent<Collider>());   // the shot ray must never hit the gun
            go.name = name;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = pos;
            go.transform.localRotation = Quaternion.Euler(euler);
            go.transform.localScale = scale;
            var renderer = go.GetComponent<MeshRenderer>();
            renderer.sharedMaterial = mat;
            renderer.shadowCastingMode = ShadowCastingMode.Off;   // a viewmodel would shadow the player's view
            renderer.receiveShadows = false;
            return go.transform;
        }

        static Transform Box(Transform parent, string name, Vector3 pos, Vector3 size, Material mat, Vector3 euler = default)
            => Part(PrimitiveType.Cube, parent, name, pos, size, mat, euler);

        /// Cylinder along +Z: width (x) and height (y) of the cross-section, length along Z.
        static Transform Rod(Transform parent, string name, Vector3 pos, float width, float height, float length, Material mat)
            => Part(PrimitiveType.Cylinder, parent, name, pos, new Vector3(width, length * 0.5f, height), mat, new Vector3(90f, 0f, 0f));

        static Transform Ball(Transform parent, string name, Vector3 pos, float diameter, Material mat)
            => Part(PrimitiveType.Sphere, parent, name, pos, Vector3.one * diameter, mat, Vector3.zero);
    }
}
