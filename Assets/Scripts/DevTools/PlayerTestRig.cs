using System.Collections;
using Echo.Interaction;
using StarterAssets;
using UnityEngine;

namespace Echo.DevTools
{
    /// Drives the Starter Assets player for scripted Play Mode checks: disables move/look input,
    /// places and aims the player, and walks it with its real CharacterController (real collisions).
    /// Restore() puts everything back. Used by InteractionSelfTest and WarehouseDoorWalkthrough.
    public class PlayerTestRig
    {
        public readonly PlayerInteractor Interactor;
        public readonly Transform Player;
        public readonly Vector3 Origin, EyeOffset;

        readonly Transform cameraRoot;
        readonly FirstPersonController fpc;
        readonly StarterAssetsInputs inputs;
        readonly CharacterController cc;
        readonly Quaternion startYaw, startPitch;

        public PlayerTestRig(PlayerInteractor interactor)
        {
            Interactor = interactor;
            Player = interactor.transform;
            fpc = Player.GetComponent<FirstPersonController>();
            inputs = Player.GetComponent<StarterAssetsInputs>();
            cc = Player.GetComponent<CharacterController>();
            cameraRoot = fpc != null && fpc.CinemachineCameraTarget != null
                ? fpc.CinemachineCameraTarget.transform
                : interactor.viewPoint.parent;

            if (fpc != null) fpc.enabled = false;
            ZeroInputs();

            Origin = Player.position;
            EyeOffset = interactor.viewPoint.position - Player.position;
            startYaw = Player.rotation;
            startPitch = cameraRoot.localRotation;
        }

        public float Radius => cc != null ? cc.radius : 0.5f;

        public void Restore(bool backToOrigin = true)
        {
            if (backToOrigin) Player.position = Origin;
            Player.rotation = startYaw;
            cameraRoot.localRotation = startPitch;
            Physics.SyncTransforms();
            ZeroInputs();
            if (fpc != null) fpc.enabled = true;
        }

        void ZeroInputs()
        {
            if (inputs == null) return;
            inputs.move = Vector2.zero;
            inputs.look = Vector2.zero;
            inputs.jump = false;
            inputs.sprint = false;
        }

        /// Places the player's feet at `feet`, turns body (yaw) and camera root (pitch) to look at `target`,
        /// then refreshes the interactor's focus.
        public void Aim(Vector3 feet, Vector3 target)
        {
            Vector3 d = target - (feet + EyeOffset);
            float yaw = Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg;
            float pitch = -Mathf.Atan2(d.y, new Vector2(d.x, d.z).magnitude) * Mathf.Rad2Deg;

            Player.SetPositionAndRotation(feet, Quaternion.Euler(0f, yaw, 0f));
            cameraRoot.localRotation = Quaternion.Euler(pitch, 0f, 0f);
            if (Interactor.viewPoint != cameraRoot) Interactor.viewPoint.localRotation = Quaternion.identity;
            Physics.SyncTransforms();   // CharacterController + colliders must see the new pose
            Interactor.RefreshFocus();
        }

        /// Turns to look at `target` from where the player stands.
        public void Look(Vector3 target) => Aim(Player.position, target);

        /// Walks horizontally towards `direction` for `distance` metres at `speed` m/s using CharacterController.Move,
        /// so walls and doors stop the player exactly as in play. Stops early if blocked for a few frames.
        public IEnumerator Walk(Vector3 direction, float distance, float speed = 3f)
        {
            direction.y = 0f;
            direction.Normalize();
            Player.rotation = Quaternion.LookRotation(direction);
            float moved = 0f, stuckTime = 0f;
            while (moved < distance && stuckTime < 0.3f)
            {
                Vector3 before = Player.position;
                float step = Mathf.Min(speed * Time.deltaTime, distance - moved);
                cc.Move(direction * step + Vector3.down * (2f * Time.deltaTime));
                float progress = Vector3.ProjectOnPlane(Player.position - before, Vector3.up).magnitude;
                moved += progress;
                stuckTime = progress < step * 0.1f ? stuckTime + Time.deltaTime : 0f;
                yield return null;
            }
        }
    }
}
