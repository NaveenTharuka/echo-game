using System.Collections;
using System.Text;
using Echo.AI;
using Echo.Interaction;
using UnityEngine;

namespace Echo.DevTools
{
    /// ECHO > Dev > Agent Hearing Smoke Test (Play Mode, any agent sandbox): checks the player-side noise path
    /// InteractionNoise -> Noise.Make -> SoundBus -> Hearing still reaches every agent. For each live agent: a gunshot
    /// through InteractionNoise 8 m from it on the NavMesh. Pass = its Hearing took it (urgent at once, before the brain
    /// drains it). The reaction 4 s later (state, alert, moved/turned) is reported: it is each brain's own design.
    public class AgentHearingSmokeTest : MonoBehaviour
    {
        public static void Launch()
        {
            if (TrackerScenarios.RefuseIfBusy("agent hearing smoke test")) return;
            var run = new GameObject("_AgentHearingSmokeTest").AddComponent<AgentHearingSmokeTest>();
            TrackerScenarios.Claim(run, "agent hearing smoke test");
            run.StartCoroutine(run.Run());
        }

        IEnumerator Run()
        {
            while (InteractionNoise.IsMuted) yield return null;
            var agents = new AgentBrain[AgentRegistry.All.Count];
            for (int i = 0; i < agents.Length; i++) agents[i] = AgentRegistry.All[i];
            var sb = new StringBuilder();
            int ok = 0;
            foreach (var a in agents)
            {
                if (a == null) continue;
                var t = a.transform;
                Vector3 p0 = t.position; Quaternion r0 = t.rotation; string s0 = a.StateName; float alert0 = a.AlertLevel;
                Vector3 at = t.position + t.right * 8f;
                if (UnityEngine.AI.NavMesh.SamplePosition(at, out var hit, 4f, UnityEngine.AI.NavMesh.AllAreas)) at = hit.position;
                var src = new GameObject("SmokeShot");
                bool sent = InteractionNoise.Emit(NoiseType.Gunshot, at, 1f, NoiseSource.Player, src);
                bool heard = a.TryGetComponent(out Echo.Sound.Hearing ear) && ear.HasUrgent;   // synchronous delivery
                Destroy(src);
                yield return new WaitForSeconds(4f);
                bool pass = sent && heard;
                if (pass) ok++;
                sb.AppendLine($"{(pass ? "PASS" : "FAIL")}  {a.DisplayName} ({a.GetType().Name}): shot at {at} sent={sent} heard={heard}; reaction: " +
                              $"state {s0} -> {a.StateName}, alert {alert0:0.00} -> {a.AlertLevel:0.00}, " +
                              $"moved {Vector3.Distance(t.position, p0):0.0} m, turned {Quaternion.Angle(t.rotation, r0):0} deg");
                a.ResetAgent();
                yield return new WaitForSeconds(1f);
            }
            string msg = $"[AgentHearingSmokeTest] {ok}/{agents.Length} agents heard the shot\n{sb}";
            if (ok == agents.Length && agents.Length > 0) Debug.Log(msg); else Debug.LogError(msg);
            Destroy(gameObject);
        }
    }
}
