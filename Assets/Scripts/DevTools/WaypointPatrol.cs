using System.Collections.Generic;
using Echo.AI;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Echo.DevTools
{
    /// Dev tool for the Agent Controller sandbox: walks an AgentMotor round its waypoints, no brain needed.
    /// Keys: 1 / 2 / 3 = Sneak / Walk / Run, F8 = smoothing (path + rotation) on / off to compare live.
    [RequireComponent(typeof(AgentMotor))]
    public class WaypointPatrol : MonoBehaviour
    {
        public List<Transform> waypoints = new();
        public AgentMotor.Gait gait = AgentMotor.Gait.Walk;
        public float pause = 0.5f;
        public bool showHelp = true;

        AgentMotor motor; AgentAnimatorBridge bridge;
        int next; float resumeAt; GUIStyle style;

        void Awake() { motor = GetComponent<AgentMotor>(); TryGetComponent(out bridge); }
        void Start() => resumeAt = Time.time + 0.5f;   // the NavGraph builds in Start

        void Update()
        {
            var k = Keyboard.current;
            if (k != null)
            {
                if (k.digit1Key.wasPressedThisFrame) SetGait(AgentMotor.Gait.Sneak);
                if (k.digit2Key.wasPressedThisFrame) SetGait(AgentMotor.Gait.Walk);
                if (k.digit3Key.wasPressedThisFrame) SetGait(AgentMotor.Gait.Run);
                if (k.f8Key.wasPressedThisFrame)
                {
                    motor.smoothPath = motor.smoothRotation = !motor.smoothPath;
                    if (motor.IsMoving) Go();   // re-plan so the change shows at once
                }
            }
            if (waypoints.Count == 0) return;

            if (motor.ArrivedFlag || motor.FailedFlag)
            {
                if (motor.FailedFlag) Debug.LogWarning($"[WaypointPatrol] Could not reach {waypoints[next].name}, skipping.", this);
                motor.ConsumeFlags();
                next = (next + 1) % waypoints.Count;
                resumeAt = Time.time + pause;
            }
            if (!motor.IsMoving && Time.time >= resumeAt) Go();
        }

        void Go() { if (waypoints[next] != null) motor.MoveTo(waypoints[next].position, gait); }

        void SetGait(AgentMotor.Gait g) { gait = g; if (motor.IsMoving) motor.SetGait(g); }

        void OnGUI()
        {
            if (!showHelp) return;
            style ??= new GUIStyle(GUI.skin.label) { richText = true, fontSize = 14 };
            float turn = bridge != null ? bridge.TurnRate : 0f;
            GUILayout.BeginArea(new Rect(10, Screen.height - 120, 560, 110), GUI.skin.box);
            GUILayout.Label("<b>AGENT CONTROLLER</b>   F7 path lines   F8 smoothing   1/2/3 gait   F2/F3/F4 time", style);
            GUILayout.Label($"smoothing <b>{(motor.smoothPath ? "ON" : "OFF")}</b>   gait <b>{gait}</b>   " +
                            $"lines <b>{(AgentPathVisualizer.Visible ? "ON" : "OFF")}</b>   next <b>{(waypoints.Count > 0 && waypoints[next] != null ? waypoints[next].name : "-")}</b>", style);
            GUILayout.Label($"speed {motor.Velocity.magnitude:0.00} m/s   turn {turn:0} deg/s" +
                            (motor.IsTurningInPlace ? "   <color=yellow>turning in place</color>" : ""), style);
            GUILayout.EndArea();
        }
    }
}
