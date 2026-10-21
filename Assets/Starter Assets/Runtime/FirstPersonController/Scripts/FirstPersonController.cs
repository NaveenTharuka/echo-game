using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace StarterAssets
{
	[RequireComponent(typeof(CharacterController))]
#if ENABLE_INPUT_SYSTEM
	[RequireComponent(typeof(PlayerInput))]
#endif
	public class FirstPersonController : MonoBehaviour
	{
		[Header("Player")]
		[Tooltip("Move speed of the character in m/s")]
		public float MoveSpeed = 4.0f;
		[Tooltip("Sprint speed of the character in m/s")]
		public float SprintSpeed = 6.0f;
		[Tooltip("Rotation speed of the character")]
		public float RotationSpeed = 1.0f;
		[Tooltip("Acceleration and deceleration")]
		public float SpeedChangeRate = 10.0f;

		[Space(10)]
		[Tooltip("The height the player can jump")]
		public float JumpHeight = 1.2f;
		[Tooltip("The character uses its own gravity value. The engine default is -9.81f")]
		public float Gravity = -15.0f;

		[Space(10)]
		[Tooltip("Time required to pass before being able to jump again. Set to 0f to instantly jump again")]
		public float JumpTimeout = 0.1f;
		[Tooltip("Time required to pass before entering the fall state. Useful for walking down stairs")]
		public float FallTimeout = 0.15f;

		[Header("Player Grounded")]
		[Tooltip("If the character is grounded or not. Not part of the CharacterController built in grounded check")]
		public bool Grounded = true;
		[Tooltip("Useful for rough ground")]
		public float GroundedOffset = -0.14f;
		[Tooltip("The radius of the grounded check. Should match the radius of the CharacterController")]
		public float GroundedRadius = 0.5f;
		[Tooltip("What layers the character uses as ground")]
		public LayerMask GroundLayers;
		[Tooltip("ECHO: walking off a step at most this high (m) keeps the feet on the ground (stairs, ramps). 0 = off")]
		public float GroundSnapDistance = 0.5f;

		[Header("Cinemachine")]
		[Tooltip("The follow target set in the Cinemachine Virtual Camera that the camera will follow")]
		public GameObject CinemachineCameraTarget;
		[Tooltip("How far in degrees can you move the camera up")]
		public float TopClamp = 90.0f;
		[Tooltip("How far in degrees can you move the camera down")]
		public float BottomClamp = -90.0f;

		// cinemachine
		private float _cinemachineTargetPitch;

		// player
		private float _speed;
		private float _rotationVelocity;
		private float _verticalVelocity;
		private float _terminalVelocity = 53.0f;

		// timeout deltatime
		private float _jumpTimeoutDelta;
		private float _fallTimeoutDelta;

	
#if ENABLE_INPUT_SYSTEM
		private PlayerInput _playerInput;
#endif
		private CharacterController _controller;
		private StarterAssetsInputs _input;
		private GameObject _mainCamera;
		// ECHO hooks (set by Echo.Player.PlayerExertion: stamina, exhaustion, crouch). This assembly cannot see game code.
		/// (sprintHeld, walkSpeed, sprintSpeed) -> target speed. Null = Starter Assets behaviour.
		public System.Func<bool, float, float, float> TargetSpeedOverride;
		/// False = no jump now (crouched, out of breath). Null = always allowed.
		public System.Func<bool> JumpAllowed;

		private const float _threshold = 0.01f;

		private bool IsCurrentDeviceMouse
		{
			get
			{
				#if ENABLE_INPUT_SYSTEM
				return _playerInput.currentControlScheme == "KeyboardMouse";
				#else
				return false;
				#endif
			}
		}

		private void Awake()
		{
			// get a reference to our main camera
			if (_mainCamera == null)
			{
				_mainCamera = GameObject.FindGameObjectWithTag("MainCamera");
			}
		}

		private void Start()
		{
			_controller = GetComponent<CharacterController>();
			_input = GetComponent<StarterAssetsInputs>();
#if ENABLE_INPUT_SYSTEM
			_playerInput = GetComponent<PlayerInput>();
#else
			Debug.LogError( "Starter Assets package is missing dependencies. Please use Tools/Starter Assets/Reinstall Dependencies to fix it");
#endif

			// reset our timeouts on start
			_jumpTimeoutDelta = JumpTimeout;
			_fallTimeoutDelta = FallTimeout;
		}

		private void Update()
		{
			JumpAndGravity();
			GroundedCheck();
			Move();
		}

		private void LateUpdate()
		{
			CameraRotation();
		}

		private void GroundedCheck()
		{
			// set sphere position, with offset
			Vector3 spherePosition = new Vector3(transform.position.x, transform.position.y - GroundedOffset, transform.position.z);
			// The Sci-Fi lab floor prefabs are on Unity layer 9. Always include that
			// layer so an inspector override cannot make the player fall through the lab.
			int floorLayers = GroundLayers.value | 1 | (1 << 9);
			Grounded = _controller.isGrounded || TouchesGround(spherePosition, floorLayers);
		}

		// The player sits on the same layer as the SampleScene floors (8), so the check must skip
		// its own colliders, otherwise it is always "grounded" and can jump again in mid-air.
		private readonly Collider[] _groundHits = new Collider[16];

		private bool TouchesGround(Vector3 spherePosition, int floorLayers)
		{
			int count = Physics.OverlapSphereNonAlloc(spherePosition, GroundedRadius, _groundHits, floorLayers, QueryTriggerInteraction.Ignore);
			for (int i = 0; i < count; i++)
			{
				if (!_groundHits[i].transform.IsChildOf(transform)) return true;
			}
			return false;
		}

		private void CameraRotation()
		{
			// if there is an input
			if (_input.look.sqrMagnitude >= _threshold)
			{
				//Don't multiply mouse input by Time.deltaTime
				float deltaTimeMultiplier = IsCurrentDeviceMouse ? 1.0f : Time.deltaTime;
				
				_cinemachineTargetPitch += _input.look.y * RotationSpeed * deltaTimeMultiplier;
				_rotationVelocity = _input.look.x * RotationSpeed * deltaTimeMultiplier;

				// clamp our pitch rotation
				_cinemachineTargetPitch = ClampAngle(_cinemachineTargetPitch, BottomClamp, TopClamp);

				// Update Cinemachine camera target pitch
				CinemachineCameraTarget.transform.localRotation = Quaternion.Euler(_cinemachineTargetPitch, 0.0f, 0.0f);

				// rotate the player left and right
				transform.Rotate(Vector3.up * _rotationVelocity);
			}
		}

		private void Move()
		{
			// set target speed based on move speed, sprint speed and if sprint is pressed
			// (ECHO: stamina / crouch decide the real speed when PlayerExertion is present)
			float targetSpeed = TargetSpeedOverride != null ? TargetSpeedOverride(_input.sprint, MoveSpeed, SprintSpeed)
			                                                : (_input.sprint ? SprintSpeed : MoveSpeed);

			// a simplistic acceleration and deceleration designed to be easy to remove, replace, or iterate upon

			// note: Vector2's == operator uses approximation so is not floating point error prone, and is cheaper than magnitude
			// if there is no input, set the target speed to 0
			if (_input.move == Vector2.zero) targetSpeed = 0.0f;

			// a reference to the players current horizontal velocity
			float currentHorizontalSpeed = new Vector3(_controller.velocity.x, 0.0f, _controller.velocity.z).magnitude;

			float speedOffset = 0.1f;
			float inputMagnitude = _input.analogMovement ? _input.move.magnitude : 1f;

			// accelerate or decelerate to target speed
			if (currentHorizontalSpeed < targetSpeed - speedOffset || currentHorizontalSpeed > targetSpeed + speedOffset)
			{
				// creates curved result rather than a linear one giving a more organic speed change
				// note T in Lerp is clamped, so we don't need to clamp our speed
				_speed = Mathf.Lerp(currentHorizontalSpeed, targetSpeed * inputMagnitude, Time.deltaTime * SpeedChangeRate);

				// round speed to 3 decimal places
				_speed = Mathf.Round(_speed * 1000f) / 1000f;
			}
			else
			{
				_speed = targetSpeed;
			}

			// normalise input direction
			Vector3 inputDirection = new Vector3(_input.move.x, 0.0f, _input.move.y).normalized;

			// note: Vector2's != operator uses approximation so is not floating point error prone, and is cheaper than magnitude
			// if there is a move input rotate player when the player is moving
			if (_input.move != Vector2.zero)
			{
				// move
				inputDirection = transform.right * _input.move.x + transform.forward * _input.move.y;
			}

			// move the player
			Vector3 horizontal = inputDirection.normalized * (_speed * Time.deltaTime);
			Vector3 vertical = new Vector3(0.0f, _verticalVelocity, 0.0f) * Time.deltaTime;

			// ECHO: stick to stairs and slopes going down instead of flying off each step. Done inside the one Move,
			// so the controller's velocity stays the real walking velocity (footsteps and head bob read it).
			// Also just after walking off an edge (the controller loses contact the frame it passes the step's lip).
			_airTime = _controller.isGrounded ? 0f : _airTime + Time.deltaTime;
			if (GroundSnapDistance > 0f && _airTime < 0.15f && _verticalVelocity <= 0.0f)
			{
				float drop = FloorDropAt(transform.position + horizontal);
				if (drop > 0f) vertical.y = Mathf.Min(vertical.y, -(drop + 0.02f));
			}
			_controller.Move(horizontal + vertical);
		}

		// ECHO: how far below the feet the floor is at `feet` (0 = none within GroundSnapDistance). Own colliders are
		// skipped: the player shares layer 8 with the SampleScene floors.
		private readonly RaycastHit[] _snapHits = new RaycastHit[8];
		private float _airTime;

		private float FloorDropAt(Vector3 feet)
		{
			float r = _controller.radius * 0.9f;
			const float lift = 0.05f;
			Vector3 origin = feet + Vector3.up * (r + lift);
			int floorLayers = GroundLayers.value | 1 | (1 << 9);
			int count = Physics.SphereCastNonAlloc(origin, r, Vector3.down, _snapHits, GroundSnapDistance + lift, floorLayers, QueryTriggerInteraction.Ignore);
			float best = float.MaxValue;
			for (int i = 0; i < count; i++)
			{
				if (_snapHits[i].transform.IsChildOf(transform) || _snapHits[i].distance <= 0f) continue;
				if (_snapHits[i].normal.y < 0.6f) continue;   // walls and steep slopes are not floors
				best = Mathf.Min(best, _snapHits[i].distance);
			}
			return best < float.MaxValue ? Mathf.Max(0f, best - lift) : 0f;
		}

		private void JumpAndGravity()
		{
			if (Grounded)
			{
				// reset the fall timeout timer
				_fallTimeoutDelta = FallTimeout;

				// stop our velocity dropping infinitely when grounded
				if (_verticalVelocity < 0.0f)
				{
					_verticalVelocity = -2f;
				}

				// Jump
				// ECHO: no jump while crouched or out of breath (and the press is not saved for later)
				if (_input.jump && JumpAllowed != null && !JumpAllowed()) _input.jump = false;
				if (_input.jump && _jumpTimeoutDelta <= 0.0f)
				{
					// the square root of H * -2 * G = how much velocity needed to reach desired height
					_verticalVelocity = Mathf.Sqrt(JumpHeight * -2f * Gravity);
				}

				// jump timeout
				if (_jumpTimeoutDelta >= 0.0f)
				{
					_jumpTimeoutDelta -= Time.deltaTime;
				}
			}
			else
			{
				// reset the jump timeout timer
				_jumpTimeoutDelta = JumpTimeout;

				// fall timeout
				if (_fallTimeoutDelta >= 0.0f)
				{
					_fallTimeoutDelta -= Time.deltaTime;
				}

				// if we are not grounded, do not jump
				_input.jump = false;
			}

			// apply gravity over time if under terminal (multiply by delta time twice to linearly speed up over time)
			if (_verticalVelocity < _terminalVelocity)
			{
				_verticalVelocity += Gravity * Time.deltaTime;
			}
		}

		private static float ClampAngle(float lfAngle, float lfMin, float lfMax)
		{
			if (lfAngle < -360f) lfAngle += 360f;
			if (lfAngle > 360f) lfAngle -= 360f;
			return Mathf.Clamp(lfAngle, lfMin, lfMax);
		}

		private void OnDrawGizmosSelected()
		{
			Color transparentGreen = new Color(0.0f, 1.0f, 0.0f, 0.35f);
			Color transparentRed = new Color(1.0f, 0.0f, 0.0f, 0.35f);

			if (Grounded) Gizmos.color = transparentGreen;
			else Gizmos.color = transparentRed;

			// when selected, draw a gizmo in the position of, and matching radius of, the grounded collider
			Gizmos.DrawSphere(new Vector3(transform.position.x, transform.position.y - GroundedOffset, transform.position.z), GroundedRadius);
		}
	}
}
