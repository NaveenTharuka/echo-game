using System.Collections.Generic;
using Echo.Navigation;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Echo.AI
{
    /// Draws the agent's current path in the Game view (demo / report screenshots). F5 toggles it for all agents.
    ///   grey   = raw A* node path (rings on the nodes),
    ///   green  = smoothed curve the motor follows,
    ///   yellow = pure-pursuit look-ahead point.
    [RequireComponent(typeof(AgentMotor))]
    public class AgentPathVisualizer : MonoBehaviour
    {
        public static bool Visible = true;
        public bool showNodes = true, showCurve = true, showLookAhead = true;
        public Color nodeColour = new(0.75f, 0.75f, 0.75f, 0.8f);
        public Color curveColour = new(0.2f, 1f, 0.35f, 1f);
        public Color lookAheadColour = new(1f, 0.85f, 0.1f, 1f);
        public float width = 0.07f, height = 0.08f;

        static int toggledFrame = -1;
        AgentMotor motor;
        LineRenderer nodeLine, curveLine, lookRing;
        readonly List<LineRenderer> rings = new();
        Vector3[] buf = new Vector3[64];
        Material mat;

        void Awake()
        {
            motor = GetComponent<AgentMotor>();
            var shader = Shader.Find("Sprites/Default");
            if (shader == null) shader = Shader.Find("Universal Render Pipeline/Unlit");
            mat = new Material(shader);
            nodeLine = NewLine("PathNodes", nodeColour, width * 0.7f, false);
            curveLine = NewLine("PathCurve", curveColour, width, false);
            lookRing = NewLine("LookAhead", lookAheadColour, width * 0.7f, true);
        }

        void OnDestroy() { if (mat != null) Destroy(mat); }

        void Update()
        {
            var k = Keyboard.current;
            if (k != null && k.f5Key.wasPressedThisFrame && toggledFrame != Time.frameCount)
            { toggledFrame = Time.frameCount; Visible = !Visible; }
        }

        void LateUpdate()
        {
            bool on = Visible && motor.IsMoving;
            Vector3 up = Vector3.up * height;

            // Raw node path: agent -> remaining nodes -> goal.
            int n = 0, rn = 0;
            if (on && showNodes)
            {
                Put(ref n, transform.position + up);
                var g = NavGraph.Instance;
                if (g != null)
                    for (int i = motor.NextNodeIndex; i < motor.NodePath.Count; i++)
                    {
                        Vector3 p = g.Position(motor.NodePath[i]) + up;
                        Put(ref n, p); Ring(rn++, p, 0.3f);
                    }
                Put(ref n, motor.Goal + up); Ring(rn++, motor.Goal + up, 0.45f);
            }
            Apply(nodeLine, n);
            for (int i = rn; i < rings.Count; i++) rings[i].enabled = false;

            // Smoothed curve from the agent onwards.
            n = 0;
            var curve = motor.PathPoints;
            if (on && showCurve && curve.Count > 1)
            {
                Put(ref n, transform.position + up);
                for (int i = motor.CurveIndex + 1; i < curve.Count; i++) Put(ref n, curve[i] + up);
            }
            Apply(curveLine, n);

            bool look = on && showLookAhead && curve.Count > 1;
            lookRing.enabled = look;
            if (look) Circle(lookRing, motor.LookAheadPoint + up, 0.25f);
        }

        void Put(ref int n, Vector3 p)
        {
            if (n >= buf.Length) System.Array.Resize(ref buf, buf.Length * 2);
            buf[n++] = p;
        }

        void Apply(LineRenderer lr, int n)
        {
            lr.enabled = n > 1;
            if (n < 2) return;
            lr.positionCount = n;
            for (int i = 0; i < n; i++) lr.SetPosition(i, buf[i]);
        }

        void Ring(int i, Vector3 centre, float radius)
        {
            while (rings.Count <= i) rings.Add(NewLine("Node_" + rings.Count, nodeColour, width * 0.6f, true));
            rings[i].enabled = true;
            Circle(rings[i], centre, radius);
        }

        static readonly Vector3[] circle = new Vector3[12];
        static void Circle(LineRenderer lr, Vector3 c, float r)
        {
            for (int i = 0; i < circle.Length; i++)
            {
                float a = i * Mathf.PI * 2f / circle.Length;
                circle[i] = c + new Vector3(Mathf.Cos(a) * r, 0f, Mathf.Sin(a) * r);
            }
            lr.positionCount = circle.Length;
            lr.SetPositions(circle);
        }

        LineRenderer NewLine(string name, Color c, float w, bool loop)
        {
            var go = new GameObject(name);
            go.transform.SetParent(transform, false);
            var lr = go.AddComponent<LineRenderer>();
            lr.useWorldSpace = true; lr.loop = loop;
            lr.sharedMaterial = mat;
            lr.startColor = lr.endColor = c;
            lr.startWidth = lr.endWidth = w;
            lr.numCornerVertices = 2;
            lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            lr.receiveShadows = false;
            lr.enabled = false;
            return lr;
        }
    }
}
