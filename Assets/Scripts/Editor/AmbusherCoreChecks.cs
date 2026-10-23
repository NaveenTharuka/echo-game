using System;
using System.Collections.Generic;
using Echo.AI;
using UnityEngine;

namespace Echo.EditorTools
{
    /// ECHO > Ambusher > Run Core Checks. Edit-mode checks of the Ambusher's RouteModel (guide §12.2) on a tiny zone graph:
    ///        A - B - C          (corridor)
    ///            |
    ///            D              (side room)
    static class AmbusherCoreChecks
    {
        static int failed;

        [UnityEditor.MenuItem("ECHO/Ambusher/Run Core Checks")]
        static void Run()
        {
            failed = 0;

            var m = New();
            Check("prior is uniform over neighbours", Near(m.P("B", "A"), 1f / 3f) && Near(m.P("B", "C"), 1f / 3f) && Near(m.P("B", "D"), 1f / 3f));
            Check("non-adjacent zones have P = 0", m.P("A", "C") == 0f && m.P("A", "D") == 0f);

            // The player walks A -> B -> C five times.
            float t = 0f;
            for (int i = 0; i < 5; i++) { m.Observe("A", t += 2f); m.Observe("B", t += 2f); m.Observe("C", t += 2f); }
            Check("learned B -> C beats B -> D", m.P("B", "C") > 0.6f && m.P("B", "D") < 0.2f);
            Check("rows sum to 1", Near(m.P("B", "A") + m.P("B", "C") + m.P("B", "D"), 1f));
            Check("C -> A is not counted (not adjacent)", m.Count("C", "A") == 0f);

            m.Observe("A", t += 2f); m.Observe("B", t += 2f);
            var pass = m.PassProbability("B", 3);
            Check("prediction from B: C is the most likely next zone",
                  pass.TryGetValue("C", out var pc) && pc > (pass.TryGetValue("D", out var pd) ? pd : 0f) && pc > 0.6f);
            Check("route confidence passes the commit threshold (0.35)", Max(pass) >= 0.35f);

            // Forgetting: after one time constant the counts drop to e^-1.
            float before = m.Count("B", "C");
            m.Decay(t + m.ForgetTau);
            Check("forgetting: count x e^-1 after tau", Near(m.Count("B", "C"), before * Mathf.Exp(-1f), 0.05f));
            m.Decay(t + 20f * m.ForgetTau);
            Check("old habits fade back towards the uniform prior", Near(m.P("B", "C"), 1f / 3f, 0.02f));

            // A new habit takes over: A -> B -> D.
            m = New(); t = 0f;
            for (int i = 0; i < 4; i++) { m.Observe("A", t += 2f); m.Observe("B", t += 2f); m.Observe("C", t += 2f); }
            t += 300f;
            for (int i = 0; i < 4; i++) { m.Observe("A", t += 2f); m.Observe("B", t += 2f); m.Observe("D", t += 2f); }
            Check("recent route wins after a change of habit", m.P("B", "D") > m.P("B", "C"));

            m.Clear();
            Check("clear forgets everything", m.TotalObservations() == 0f && m.CurrentZone == null);

            if (failed == 0) Debug.Log("[AmbusherCoreChecks] all passed");
            else Debug.LogError($"[AmbusherCoreChecks] {failed} check(s) FAILED");
        }

        static RouteModel New() => new(new Dictionary<string, HashSet<string>>
        {
            ["A"] = new() { "B" }, ["B"] = new() { "A", "C", "D" }, ["C"] = new() { "B" }, ["D"] = new() { "B" },
        });

        static float Max(Dictionary<string, float> d) { float x = 0f; foreach (var v in d.Values) x = Math.Max(x, v); return x; }
        static bool Near(float a, float b, float eps = 1e-4f) => Mathf.Abs(a - b) <= eps;

        static void Check(string what, bool ok)
        {
            if (ok) Debug.Log("[AmbusherCoreChecks] ok   " + what);
            else { failed++; Debug.LogError("[AmbusherCoreChecks] FAIL " + what); }
        }
    }
}
