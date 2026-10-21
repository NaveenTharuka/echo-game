using System.Collections;
using System.Collections.Generic;
using Echo.AI;
using Echo.Gameplay;
using Echo.Interaction;
using Echo.Player;
using StarterAssets;
using UnityEngine;
using UnityEngine.AI;

namespace Echo.DevTools
{
    /// Scripted Play Mode check of Phase 2 in SampleScene. Logs "[Phase2]" lines. Start in Play Mode:
    ///   new GameObject("Phase2SelfTest").AddComponent<Phase2SelfTest>().Run("patrol" | "objective")
    /// patrol    : player invulnerable and parked; every 5 s for 40 s, where each agent is and what it does.
    /// objective : player invulnerable; teleports through the chain - fusebox (who reacts to the generator?), the locked
    ///             Lab door, the pass, the door, the medicine (alarm: who comes?), the exit (win).
    public class Phase2SelfTest : MonoBehaviour
    {
        FirstPersonController fpc;
        CharacterController cc;
        PlayerInteractor interactor;
        ObjectiveDirector director;
        readonly List<string> hud = new();

        public void Run(string test)
        {
            fpc = FindFirstObjectByType<FirstPersonController>();
            cc = fpc.GetComponent<CharacterController>();
            interactor = fpc.GetComponent<PlayerInteractor>();
            director = FindFirstObjectByType<ObjectiveDirector>();
            if (fpc.TryGetComponent(out UnityEngine.InputSystem.PlayerInput pi)) pi.enabled = false;
#if UNITY_EDITOR
            if (fpc.TryGetComponent(out PlayerDeath death) && !death.Invulnerable) death.SetInvulnerable(true);
#endif
            HudMessage.Shown += (t, _) => hud.Add(t);
            StartCoroutine(test == "patrol" ? Patrol() : Objective());
        }

        static void Log(string s) => Debug.Log("[Phase2] " + s);

        IEnumerator Patrol()
        {
            var start = new Dictionary<AgentBrain, Vector3>();
            var travelled = new Dictionary<AgentBrain, float>();
            var last = new Dictionary<AgentBrain, Vector3>();
            foreach (var a in AgentRegistry.All) { start[a] = last[a] = a.transform.position; travelled[a] = 0f; }
            for (int i = 1; i <= 8; i++)
            {
                for (float t = 0f; t < 5f; t += Time.deltaTime)
                {
                    foreach (var a in AgentRegistry.All) { travelled[a] += Vector3.Distance(last[a], a.transform.position); last[a] = a.transform.position; }
                    yield return null;
                }
                var line = $"t={i * 5}s";
                foreach (var a in AgentRegistry.All)
                {
                    a.TryGetComponent(out AgentPatrol p);
                    line += $" | {a.DisplayName} {a.StateName}{(p != null && p.IsPatrolling ? " patrol" : "")} " +
                            $"{ZoneOf(a.transform.position)} moved {travelled[a]:0}m";
                }
                Log(line);
            }
            foreach (var a in AgentRegistry.All)
                Log($"{a.DisplayName}: walked {travelled[a]:0} m in 40 s, now {Vector3.Distance(start[a], a.transform.position):0} m from its post");
            Log("patrol test done");
        }

        IEnumerator Objective()
        {
            yield return new WaitForSeconds(1f);
            Log($"start: stage {director.Current}, Lab door locked {director.labDoor.isLocked}");

            // 1. Lab door first: locked, says why.
            yield return UseAt(director.labDoor.DoorwayCenter, director.labDoor.transform, "Lab door (nothing yet)");
            Log($"  HUD: {LastHud()}");

            // 2. Fusebox: who hears the generator?
            var before = Snapshot();
            yield return UseAt(director.powerSwitch.transform.position, director.powerSwitch.transform, "fusebox");
            Log($"  power {director.PowerOn}, HUD: {LastHud()}, stage {director.Current}");
            yield return new WaitForSeconds(3f);
            Log("  3 s after the generator: " + Reactions(before));

            // 3. Door again: power but no pass.
            yield return UseAt(director.labDoor.DoorwayCenter, director.labDoor.transform, "Lab door (power only)");
            Log($"  locked {director.labDoor.isLocked}, HUD: {LastHud()}");

            // 4. The pass.
            yield return UseAt(director.accessPass.transform.position, director.accessPass.transform, "access pass");
            Log($"  pass {director.HasPass}, Lab door locked {director.labDoor.isLocked}, stage {director.Current}, HUD: {LastHud()}");

            // 5. Medicine: alarm.
            before = Snapshot();
            yield return UseAt(director.medicine.transform.position, director.medicine.transform, "medicine");
            Log($"  medicine {director.HasMedicine}, stage {director.Current}, HUD: {LastHud()}");
            for (int i = 1; i <= 3; i++)
            {
                yield return new WaitForSeconds(5f);
                Log($"  {i * 5} s into the alarm: " + Reactions(before));
            }

            // 6. Exit.
            Teleport(director.exit.position);
            yield return new WaitForSeconds(2.5f);
            Log($"at the exit: stage {director.Current} after {director.ElapsedSeconds:0} s");
            Log("objective test done");
        }

        /// Stand 1.2 m in front of `target` (on the NavMesh), look at it, interact.
        IEnumerator UseAt(Vector3 point, Transform target, string what)
        {
            Vector3 best = point; float bestD = float.MaxValue;
            for (int i = 0; i < 16; i++)
            {
                Vector3 c = point + Quaternion.Euler(0f, i * 22.5f, 0f) * Vector3.forward * 1.3f;
                if (NavMesh.SamplePosition(c + Vector3.down * 1.5f, out var h, 2f, NavMesh.AllAreas))   // floor up to ~3 m below (a wall box hangs at 2.4 m)
                {
                    float d = Mathf.Abs(h.position.y - (point.y - 1.2f)) + Vector3.Distance(new Vector3(h.position.x, 0, h.position.z), new Vector3(point.x, 0, point.z)) * 0.2f;
                    if (!Physics.Linecast(h.position + Vector3.up * 1.5f, point, out var block, ~0, QueryTriggerInteraction.Ignore) || block.transform.IsChildOf(target))
                        if (d < bestD) { bestD = d; best = h.position; }
                }
            }
            Teleport(best);
            yield return new WaitForSeconds(0.4f);
            var cam = interactor.viewPoint;
            Vector3 dir = point - cam.position;
            fpc.transform.rotation = Quaternion.LookRotation(new Vector3(dir.x, 0f, dir.z));
            fpc.CinemachineCameraTarget.transform.localRotation = Quaternion.Euler(-Mathf.Atan2(dir.y, new Vector2(dir.x, dir.z).magnitude) * Mathf.Rad2Deg, 0f, 0f);
            yield return null; yield return null;
            interactor.RefreshFocus();
            bool focused = interactor.Focus != null;
            bool used = interactor.TryInteract();
            Log($"{what}: stood at {best}, focus {(focused ? ((MonoBehaviour)interactor.Focus).name : "none")}, used {used}");
        }

        void Teleport(Vector3 feet)
        {
            cc.enabled = false; fpc.transform.position = feet + Vector3.up * 0.05f; cc.enabled = true;
            Physics.SyncTransforms();
        }

        Dictionary<AgentBrain, Vector3> Snapshot()
        {
            var d = new Dictionary<AgentBrain, Vector3>();
            foreach (var a in AgentRegistry.All) d[a] = a.transform.position;
            return d;
        }

        string Reactions(Dictionary<AgentBrain, Vector3> before)
        {
            var s = "";
            foreach (var a in AgentRegistry.All)
                s += $"{a.DisplayName} {a.StateName} (alert {a.AlertLevel:0.0}, moved {Vector3.Distance(before[a], a.transform.position):0} m, {ZoneOf(a.transform.position)}) ";
            return s;
        }

        string LastHud() => hud.Count > 0 ? "\"" + hud[hud.Count - 1] + "\"" : "-";

        static string ZoneOf(Vector3 p)
        {
            string z = Navigation.ZoneVolume.ZoneAt(p);
            return string.IsNullOrEmpty(z) ? "?" : z;
        }
    }
}
