using System.Collections;
using Unity.Profiling;
using UnityEngine;

namespace Echo.DevTools
{
    /// ECHO > Dev > Measure Physics Cost (Play Mode). Evidence for physics optimisation: counts the Rigidbodies and
    /// measures the physics time per rendered frame from the profiler markers (works without the Profiler window):
    ///   Physics.Simulate                - the PhysX step itself,
    ///   FixedUpdate.PhysicsFixedUpdate  - the whole physics fixed update (step + transform sync + callbacks).
    /// Two phases: 8 s idle (props asleep, the normal case) and 3 s with every dynamic body kept awake (worst case,
    /// e.g. a fight knocking everything over). Logs one "[PhysicsCost]" line per phase.
    public class PhysicsCostProbe : MonoBehaviour
    {
        const string Tag = "[PhysicsCost] ";

        public static void Launch()
        {
            if (TrackerScenarios.RefuseIfBusy("physics cost probe")) return;
            var run = new GameObject("_PhysicsCostProbe").AddComponent<PhysicsCostProbe>();
            TrackerScenarios.Claim(run, "physics cost probe");
            run.StartCoroutine(run.Run());
        }

        IEnumerator Run()
        {
            yield return new WaitForSeconds(2f);   // let the scene settle after load
            var bodies = FindObjectsByType<Rigidbody>(FindObjectsSortMode.None);
            int dynamic = 0, kinematic = 0;
            foreach (var b in bodies) if (b.isKinematic) kinematic++; else dynamic++;
            Debug.Log($"{Tag}Rigidbodies: {dynamic} dynamic, {kinematic} kinematic; fixed step {Time.fixedDeltaTime * 1000f:0} ms");

            yield return Measure("idle 8 s", 8f, bodies, false);
            yield return Measure("all dynamic bodies awake 3 s", 3f, bodies, true);
            Destroy(gameObject);
        }

        IEnumerator Measure(string phase, float seconds, Rigidbody[] bodies, bool keepAwake)
        {
            using var simulate = ProfilerRecorder.StartNew(ProfilerCategory.Physics, "Physics.Simulate");
            using var fixedUpdate = ProfilerRecorder.StartNew(new ProfilerCategory("PlayerLoop"), "FixedUpdate.PhysicsFixedUpdate");
            double simSum = 0, fixSum = 0, simMax = 0, fixMax = 0;
            int frames = 0, awakeSum = 0;
            float end = Time.realtimeSinceStartup + seconds;
            yield return null;   // skip the frame the recorders started in
            while (Time.realtimeSinceStartup < end)
            {
                if (keepAwake)
                    foreach (var b in bodies) if (b != null && !b.isKinematic) b.WakeUp();
                yield return null;
                double sim = simulate.LastValue * 1e-6, fix = fixedUpdate.LastValue * 1e-6;   // ns -> ms, sum over this frame
                simSum += sim; fixSum += fix;
                if (sim > simMax) simMax = sim;
                if (fix > fixMax) fixMax = fix;
                int awake = 0;
                foreach (var b in bodies) if (b != null && !b.isKinematic && !b.IsSleeping()) awake++;
                awakeSum += awake;
                frames++;
            }
            if (frames == 0) yield break;
            Debug.Log($"{Tag}{phase}: {frames} frames, awake bodies avg {awakeSum / (float)frames:0.0}; " +
                      $"Physics.Simulate avg {simSum / frames:0.000} ms (max {simMax:0.000}); " +
                      $"PhysicsFixedUpdate avg {fixSum / frames:0.000} ms (max {fixMax:0.000}) per frame" +
                      (simulate.Valid && fixedUpdate.Valid ? "" : " [a recorder was not valid]"));
        }
    }
}
