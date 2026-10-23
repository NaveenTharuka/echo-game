using System.Collections.Generic;
using Echo.Sound;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.InputSystem;

namespace Echo.AI
{
    /// Test view of every agent's ear, drawn on the floor in the Game view (F5 toggles).
    ///
    ///   strong tiles = it would hear you WALK there      faint tiles = only if you RUN there
    ///   ring         = a sound it just heard: centre = where it thinks the sound was, size = how unsure it is
    ///
    /// Tiles use the real model (Acoustics: walls make sound go round via the NavGraph, Hearing threshold and the
    /// hunting boost), so they shrink behind walls and grow while the agent hunts. Only the agent's own floor level
    /// is drawn. Debug only: it reads the agents, it never feeds them anything.
    public class HearingVisualizer : MonoBehaviour
    {
        public bool visible = true;
        [Tooltip("Tile size (m). Smaller = finer but more raycasts.")]
        public float cellSize = 1.2f;
        [Tooltip("Seconds between recomputing one agent's area (agents take turns).")]
        public float refreshInterval = 0.5f;
        [Tooltip("Reference sounds: player walk / run step intensity (PlayerNoise).")]
        public float walkIntensity = 0.15f, runIntensity = 0.30f;
        public float markLifetime = 2f;
        [Tooltip("Transparent vertex-colour material. Empty = Sprites/Default.")]
        public Material material;

        class View
        {
            public AgentBrain Brain; public Hearing Ear; public Color Color;
            public Mesh Area, Marks; public float NextRefresh, FlashUntil;
            public readonly List<(Vector3 pos, float radius, float until)> Heard = new();
            public System.Action<PerceivedSound> Handler;
        }

        readonly Dictionary<AgentBrain, View> views = new();
        readonly List<Vector3> verts = new(); readonly List<Color> cols = new(); readonly List<int> tris = new();
        MaterialPropertyBlock block; GUIStyle style;
        static readonly int ColorId = Shader.PropertyToID("_Color");

        void Awake()
        {
            if (material == null) material = new Material(Shader.Find("Sprites/Default"));
            block = new MaterialPropertyBlock();
        }

        void OnDisable()
        {
            foreach (var v in views.Values) if (v.Ear != null) v.Ear.Heard -= v.Handler;
            views.Clear();
        }

        void Update()
        {
            var k = Keyboard.current;
            if (k != null && k.f5Key.wasPressedThisFrame) visible = !visible;
            if (!visible) return;

            int stagger = 0;
            foreach (var brain in AgentRegistry.All)
            {
                if (brain == null) continue;
                if (!views.TryGetValue(brain, out var v)) v = Track(brain, stagger);
                if (v == null) continue;
                stagger++;
                if (!brain.IsAlive) continue;

                if (Time.time >= v.NextRefresh) { v.NextRefresh = Time.time + refreshInterval; BuildArea(v); }
                BuildMarks(v);
                block.SetColor(ColorId, Time.time < v.FlashUntil ? new Color(1.6f, 1.6f, 1.6f, 1.8f) : Color.white);
                Graphics.DrawMesh(v.Area, Matrix4x4.identity, material, 0, null, 0, block);
                block.SetColor(ColorId, Color.white);
                Graphics.DrawMesh(v.Marks, Matrix4x4.identity, material, 0, null, 0, block);
            }
        }

        View Track(AgentBrain brain, int stagger)
        {
            if (!brain.TryGetComponent(out Hearing ear)) return null;
            var v = new View
            {
                Brain = brain, Ear = ear, Color = ColorFor(brain.DisplayName),
                Area = new Mesh { name = brain.name + "_HearingArea" }, Marks = new Mesh { name = brain.name + "_Heard" },
                NextRefresh = Time.time + stagger * refreshInterval / 3f
            };
            v.Area.MarkDynamic(); v.Marks.MarkDynamic();
            v.Handler = s =>
            {
                v.Heard.Add((s.EstimatedPosition, Mathf.Max(0.3f, s.PositionError), Time.time + markLifetime));
                v.FlashUntil = Time.time + 0.25f;
            };
            ear.Heard += v.Handler;
            views[brain] = v;
            return v;
        }

        static Color ColorFor(string name) => name switch
        {
            "LISTENER" => new Color(1f, 0.55f, 0.1f),
            "TRACKER" => new Color(1f, 0.15f, 0.15f),
            "AMBUSHER" => new Color(0.7f, 0.3f, 1f),
            _ => new Color(0.2f, 0.9f, 1f)
        };

        void BuildArea(View v)
        {
            verts.Clear(); cols.Clear(); tris.Clear();
            Vector3 origin = v.Brain.transform.position;
            Vector3 ear = origin + Vector3.up * v.Ear.earHeight;
            float sens = v.Ear.EffectiveSensitivity, th = v.Ear.threshold;
            // Loudness = (1 - d/R)^2 >= threshold  <=>  d <= R (1 - sqrt(threshold))
            float reach = runIntensity * Acoustics.MetresPerIntensity * sens * (1f - Mathf.Sqrt(th));
            int n = Mathf.CeilToInt(reach / cellSize);
            float half = cellSize * 0.45f;

            for (int ix = -n; ix <= n; ix++)
            for (int iz = -n; iz <= n; iz++)
            {
                var offset = new Vector3(ix * cellSize, 0f, iz * cellSize);
                if (offset.magnitude > reach) continue;
                if (!NavMesh.SamplePosition(origin + offset, out var hit, cellSize * 0.75f, NavMesh.AllAreas)) continue;
                if (Mathf.Abs(hit.position.y - origin.y) > 1.5f) continue;       // own floor level only

                float d = Acoustics.EffectiveDistance(hit.position, ear);
                float walk = Acoustics.Loudness(walkIntensity, sens, d);
                float run = Acoustics.Loudness(runIntensity, sens, d);
                Color c = v.Color;
                if (walk >= th) c.a = 0.22f + 0.35f * Mathf.Clamp01(walk * 2f);
                else if (run >= th) c.a = 0.09f;
                else continue;

                Vector3 p = new Vector3(origin.x + offset.x, hit.position.y + 0.06f, origin.z + offset.z);
                Quad(p, half, c);
            }
            Apply(v.Area);
        }

        void BuildMarks(View v)
        {
            verts.Clear(); cols.Clear(); tris.Clear();
            v.Heard.RemoveAll(h => h.until < Time.time);
            Vector3 from = v.Brain.transform.position + Vector3.up * 0.1f;
            foreach (var (pos, radius, until) in v.Heard)
            {
                Color c = v.Color; c.a = Mathf.Clamp01((until - Time.time) / markLifetime) * 0.9f;
                Ring(pos + Vector3.up * 0.1f, radius, 0.08f, c);
                Line(from, pos + Vector3.up * 0.1f, 0.04f, c * new Color(1f, 1f, 1f, 0.6f));
            }
            Apply(v.Marks);
        }

        void Quad(Vector3 c, float h, Color col)
        {
            int i = verts.Count;
            verts.Add(c + new Vector3(-h, 0f, -h)); verts.Add(c + new Vector3(-h, 0f, h));
            verts.Add(c + new Vector3(h, 0f, h)); verts.Add(c + new Vector3(h, 0f, -h));
            for (int k = 0; k < 4; k++) cols.Add(col);
            tris.Add(i); tris.Add(i + 1); tris.Add(i + 2); tris.Add(i); tris.Add(i + 2); tris.Add(i + 3);
        }

        void Ring(Vector3 c, float r, float w, Color col)
        {
            const int seg = 28;
            for (int s = 0; s < seg; s++)
            {
                float a0 = s * Mathf.PI * 2f / seg, a1 = (s + 1) * Mathf.PI * 2f / seg;
                Vector3 d0 = new(Mathf.Cos(a0), 0f, Mathf.Sin(a0)), d1 = new(Mathf.Cos(a1), 0f, Mathf.Sin(a1));
                Strip(c + d0 * (r - w), c + d0 * (r + w), c + d1 * (r + w), c + d1 * (r - w), col);
            }
        }

        void Line(Vector3 a, Vector3 b, float w, Color col)
        {
            Vector3 dir = b - a; dir.y = 0f;
            if (dir.sqrMagnitude < 0.01f) return;
            Vector3 side = Vector3.Cross(Vector3.up, dir.normalized) * w;
            Strip(a - side, a + side, b + side, b - side, col);
        }

        void Strip(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Color col)
        {
            int i = verts.Count;
            verts.Add(a); verts.Add(b); verts.Add(c); verts.Add(d);
            for (int k = 0; k < 4; k++) cols.Add(col);
            tris.Add(i); tris.Add(i + 1); tris.Add(i + 2); tris.Add(i); tris.Add(i + 2); tris.Add(i + 3);
        }

        void Apply(Mesh m)
        {
            m.Clear();
            m.indexFormat = verts.Count > 65000 ? UnityEngine.Rendering.IndexFormat.UInt32 : UnityEngine.Rendering.IndexFormat.UInt16;
            m.SetVertices(verts); m.SetColors(cols); m.SetTriangles(tris, 0);
            m.RecalculateBounds();
        }

        void OnGUI()
        {
            if (!visible) return;
            style ??= new GUIStyle(GUI.skin.label) { richText = true, fontSize = 13 };
            GUILayout.BeginArea(new Rect(10, Screen.height - 110, 520, 100), GUI.skin.box);
            GUILayout.Label("<b>HEARING</b> (F5 hide)   <color=#ff8c1a>Listener</color>  <color=#ff2626>Tracker</color>  " +
                            "<color=#b34dff>Ambusher</color>\nstrong tiles = hears you WALK there,  faint = only if you RUN\n" +
                            "ring = what it just heard (size = how unsure); areas grow while it hunts", style);
            GUILayout.EndArea();
        }
    }
}
