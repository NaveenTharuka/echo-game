using Echo.Sound;
using UnityEngine;

namespace Echo.Player
{
    /// Motion of the first-person shotgun (presentation only, no gameplay): a recoil kick on every shot, the pump
    /// racked back and forward between shots (with the pump sound, audio only - not a SoundEvent), and the gun
    /// pulled back and lowered when the player stands close to a wall, so the barrel never pokes through it
    /// (there is no separate viewmodel camera). Sits on the gun model's root; HitscanWeapon spawns that model
    /// under the camera, and this listens to its Fired event.
    [DisallowMultipleComponent]
    public class ShotgunViewmodel : MonoBehaviour
    {
        [Tooltip("Moving fore-end (pump) child.")]
        public Transform pump;
        [Tooltip("How far the pump slides back (m).")]
        public float pumpTravel = 0.07f;
        [Tooltip("Pump starts this long after the shot (s).")]
        public float pumpDelay = 0.06f;
        [Tooltip("Back and forward again (s). Shorter than HitscanWeapon.fireInterval.")]
        public float pumpDuration = 0.22f;
        public float pumpVolume = 0.5f;

        [Header("Recoil")]
        public float kickBack = 0.06f;
        [Tooltip("Muzzle rise (degrees).")]
        public float kickPitch = 8f;
        [Tooltip("Recovery rate (1/s).")]
        public float kickRecovery = 12f;

        [Header("Walls")]
        [Tooltip("Muzzle distance from the model root (m, along +Z).")]
        public float muzzleReach = 0.43f;
        [Tooltip("Most the gun is pulled back near a wall (m).")]
        public float maxPull = 0.3f;
        [Tooltip("Nose-down tilt at full pull (degrees).")]
        public float pullTilt = 35f;

        HitscanWeapon weapon;
        Transform view;
        Vector3 restPos, pumpRest;
        Quaternion restRot;
        float kick, pull, pumpTime = -1f;
        bool pumpSoundPlayed = true;

        void Start()
        {
            restPos = transform.localPosition;
            restRot = transform.localRotation;
            if (pump != null) pumpRest = pump.localPosition;
            view = transform.parent;
            weapon = GetComponentInParent<HitscanWeapon>();
            if (weapon == null) weapon = FindFirstObjectByType<HitscanWeapon>();
            if (weapon != null) weapon.Fired += OnFired;
        }

        void OnDestroy()
        {
            if (weapon != null) weapon.Fired -= OnFired;
        }

        void OnFired(bool hit, RaycastHit info)
        {
            kick = 1f;
            pumpTime = 0f;
            pumpSoundPlayed = false;
        }

        void Update()
        {
            float dt = Time.deltaTime;
            kick *= Mathf.Exp(-kickRecovery * dt);

            // Walls: how far the muzzle would be inside the nearest surface straight ahead. The cast starts inside
            // the player's own capsule, which a cast never reports.
            float target = 0f;
            if (view != null)
            {
                float reach = restPos.z + muzzleReach + 0.05f;
                if (Physics.SphereCast(view.position, 0.06f, view.forward, out RaycastHit wall, reach, ~0, QueryTriggerInteraction.Ignore))
                    target = Mathf.Clamp(reach - wall.distance, 0f, maxPull);
            }
            pull = Mathf.MoveTowards(pull, target, dt * 1.5f);
            float p = maxPull > 0f ? pull / maxPull : 0f;

            transform.localPosition = restPos + new Vector3(0f, 0.012f * kick - 0.08f * p, -kickBack * kick - pull);
            transform.localRotation = restRot * Quaternion.Euler(-kickPitch * kick + pullTilt * p, 0f, 0f);

            if (pump == null || pumpTime < 0f) return;
            pumpTime += dt;
            float t = (pumpTime - pumpDelay) / Mathf.Max(0.01f, pumpDuration);
            if (t >= 0f && !pumpSoundPlayed)
            {
                pumpSoundPlayed = true;
                AudioService.Play(SoundType.Reload, pump.position, pumpVolume);
            }
            if (t >= 1f) { pump.localPosition = pumpRest; pumpTime = -1f; return; }
            pump.localPosition = pumpRest + Vector3.back * (pumpTravel * Mathf.Sin(Mathf.Clamp01(t) * Mathf.PI));
        }
    }
}
