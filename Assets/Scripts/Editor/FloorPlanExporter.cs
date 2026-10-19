using System.Globalization;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Echo.EditorTools
{
    /// ECHO > Map > Export Floor Plan Data: dumps SampleScene's top-down geometry for Tools/Map/floor_plan.py
    /// (which draws Docs/Map/ECHO_floor_plan_v3.png). SampleScene is opened additively and closed again, never saved.
    /// Structural meshes (floors, walls, tunnels, stairs, doors, catwalks) are written as top-down triangles so L-shaped
    /// corner pieces keep their shape; props only as bounds.
    public static class FloorPlanExporter
    {
        const string ScenePath = "Assets/Scenes/SampleScene.unity";
        public const string OutPath = "Temp/Map/floor_plan_data.json";
        static readonly string[] Structural =
            { "Floor", "Wall", "Door", "Tunnel", "Incline", "Stair", "Steps", "Catwalk", "CatWalk", "Perimeter", "Partition" };

        [MenuItem("ECHO/Map/Export Floor Plan Data (SampleScene)")]
        public static void Export()
        {
            var scene = SceneManager.GetSceneByPath(ScenePath);
            bool opened = !scene.isLoaded;
            if (opened) scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Additive);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(OutPath));
                File.WriteAllText(OutPath, Dump(scene));
                Debug.Log($"[FloorPlan] wrote {OutPath}");
            }
            finally
            {
                if (opened) EditorSceneManager.CloseScene(scene, true);
            }
        }

        static string Dump(Scene scene)
        {
            var sb = new StringBuilder("{\"renderers\":[");
            bool first = true;
            foreach (var root in scene.GetRootGameObjects())
            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
            {
                if (r is ParticleSystemRenderer || r is TrailRenderer || r is LineRenderer) continue;
                if (!r.enabled || !r.gameObject.activeInHierarchy) continue;
                string path = PathOf(r.transform);
                var b = r.bounds;
                sb.Append(first ? "" : ",").Append(F("{{\"p\":{0},\"x0\":{1},\"x1\":{2},\"y0\":{3},\"y1\":{4},\"z0\":{5},\"z1\":{6}",
                    Q(path), b.min.x, b.max.x, b.min.y, b.max.y, b.min.z, b.max.z));
                first = false;
                if (IsStructural(path) && r.TryGetComponent(out MeshFilter mf) && mf.sharedMesh != null) Triangles(sb, mf);
                sb.Append('}');
            }
            sb.Append("],\"lights\":[");
            first = true;
            foreach (var root in scene.GetRootGameObjects())
            foreach (var l in root.GetComponentsInChildren<Light>(true))
            {
                if (l.type == LightType.Directional) continue;
                var p = l.transform.position; var c = l.color;
                sb.Append(first ? "" : ",").Append(F("{{\"p\":{0},\"x\":{1},\"y\":{2},\"z\":{3},\"r\":{4},\"g\":{5},\"b\":{6},\"on\":{7},\"sh\":{8}}}",
                    Q(PathOf(l.transform)), p.x, p.y, p.z, c.r, c.g, c.b,
                    l.enabled && l.gameObject.activeInHierarchy ? 1 : 0, l.shadows != LightShadows.None ? 1 : 0));
                first = false;
            }
            sb.Append("],\"objects\":[");
            first = true;
            foreach (var root in scene.GetRootGameObjects())
            foreach (var mb in root.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (mb == null || !mb.gameObject.activeInHierarchy) continue;
                string type = mb.GetType().Name;
                if (type is not ("Door" or "SwitchInteractable" or "DecalProjector" or "FirstPersonController")) continue;
                var t = mb.transform; var p = t.position; var f = t.forward;
                sb.Append(first ? "" : ",").Append(F("{{\"t\":{0},\"p\":{1},\"x\":{2},\"y\":{3},\"z\":{4},\"fx\":{5},\"fz\":{6}}}",
                    Q(type), Q(PathOf(t)), p.x, p.y, p.z, f.x, f.z));
                first = false;
            }
            return sb.Append("]}").ToString();
        }

        /// Top-down triangles: [x0,z0,x1,z1,x2,z2, up(1 = faces up, 0 = side/down), yMin].
        static void Triangles(StringBuilder sb, MeshFilter mf)
        {
            var mesh = mf.sharedMesh;
            var m = mf.transform.localToWorldMatrix;
            var v = mesh.vertices; var tris = mesh.triangles;
            var w = new Vector3[v.Length];
            for (int i = 0; i < v.Length; i++) w[i] = m.MultiplyPoint3x4(v[i]);
            sb.Append(",\"t\":[");
            bool first = true;
            for (int i = 0; i + 2 < tris.Length; i += 3)
            {
                Vector3 a = w[tris[i]], b = w[tris[i + 1]], c = w[tris[i + 2]];
                var n = Vector3.Cross(b - a, c - a);
                float area2 = Mathf.Abs(n.y);
                bool up = n.sqrMagnitude > 1e-10f && n.normalized.y > 0.5f;
                if (area2 < 1e-4f && !(Mathf.Abs(n.x) + Mathf.Abs(n.z) > 1e-4f)) continue;   // degenerate
                sb.Append(first ? "" : ",").Append(F("[{0},{1},{2},{3},{4},{5},{6},{7}]",
                    a.x, a.z, b.x, b.z, c.x, c.z, up ? 1 : 0, Mathf.Min(a.y, Mathf.Min(b.y, c.y))));
                first = false;
            }
            sb.Append(']');
        }

        static bool IsStructural(string path)
        {
            foreach (var k in Structural) if (path.Contains(k)) return true;
            return false;
        }

        static string PathOf(Transform t) => t.parent == null ? t.name : PathOf(t.parent) + "/" + t.name;
        static string Q(string s) => "\"" + s.Replace("\\", "/").Replace("\"", "'") + "\"";
        static string F(string fmt, params object[] args)
        {
            for (int i = 0; i < args.Length; i++)
                if (args[i] is float f) args[i] = f.ToString("0.###", CultureInfo.InvariantCulture);
            return string.Format(CultureInfo.InvariantCulture, fmt, args);
        }
    }
}
