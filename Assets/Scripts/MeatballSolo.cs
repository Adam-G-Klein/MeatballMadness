using UnityEngine;
using UnityEngine.InputSystem;
using System.Collections.Generic;

/// <summary>
/// Standalone single-player test-bed for meatball physics and controls.
/// No network dependencies, attach to a sphere GameObject that has a Rigidbody
/// and a SphereCollider.
///
/// Input is driven by InputSystem_Actions (IPlayerActions callback interface).
/// Rolling is handled naturally by PhysX friction (rotation is NOT frozen).
/// Ground friction is applied via a runtime PhysicsMaterial and is tunable live
/// from the Inspector while in Play mode.
/// </summary>
[RequireComponent(typeof(Rigidbody))]
[RequireComponent(typeof(SphereCollider))]
public class MeatballSolo : MonoBehaviour, InputSystem_Actions.IPlayerActions
{
    // ── Movement ──────────────────────────────────────────────────────────────

    [Header("Movement")]
    [Tooltip("Continuous force (N) applied each FixedUpdate tick while input is held.")]
    [SerializeField] float _moveForce = 15f;

    [Tooltip("Horizontal speed (m/s) cap while walking.")]
    [SerializeField] float _maxWalkHorizontalSpeed = 8f;

    [Tooltip("Horizontal speed (m/s) cap while sprinting.")]
    [SerializeField] float _maxRunHorizontalSpeed = 12f;

    [Tooltip("Fraction of moveForce applied while airborne (0 = no air control, 1 = full).")]
    [Range(0f, 1f)]
    [SerializeField] float _airControlFraction = 0.25f;

    // ── Jump ──────────────────────────────────────────────────────────────────

    [Header("Jump")]
    [Tooltip("Upward impulse magnitude applied on jump.")]
    [SerializeField] float _jumpImpulse = 7f;

    [Tooltip("Radius of the overlap sphere used for ground detection. "
           + "Should roughly match the meatball's collider radius.")]
    [SerializeField] float _groundCheckRadius = 0.55f;

    [Tooltip("Layers treated as ground for jump detection.")]
    [SerializeField] LayerMask _groundMask = ~0;

    // ── Drag ──────────────────────────────────────────────────────────────────

    [Header("Drag")]
    [Tooltip("Linear (translational) damping applied by the Rigidbody each frame.")]
    [SerializeField] float _linearDrag = 1.5f;

    [Tooltip("Angular damping applied by the Rigidbody each frame. "
           + "Higher values damp the rolling spin faster.")]
    [SerializeField] float _angularDrag = 1f;

    // ── Ground Friction (applied to the meatball's PhysicsMaterial) ───────────

    [Header("Ground Friction")]
    [Tooltip("Dynamic (kinetic) friction coefficient of the meatball surface. "
           + "Combined (Multiply) with the ground's friction.")]
    [Range(0f, 1f)]
    [SerializeField] float _dynamicFriction = 0.6f;

    [Tooltip("Static friction coefficient. Higher values resist starting to slide.")]
    [Range(0f, 1f)]
    [SerializeField] float _staticFriction = 0.6f;

    [Tooltip("Bounciness (coefficient of restitution). 0 = no bounce, 1 = perfectly elastic.")]
    [Range(0f, 1f)]
    [SerializeField] float _bounciness = 0f;

    // ── Camera ────────────────────────────────────────────────────────────────

    [Header("Camera, found in scene on awake")]
    [Tooltip("Transform used to orient movement relative to the camera view. "
           + "Defaults to Camera.main if left empty.")]
    [SerializeField] Transform _cameraTransform;

    // ── Latency Simulation ────────────────────────────────────────────────────

    [Header("Latency Simulation")]
    [Tooltip("Simulated one-way network latency in milliseconds. Models the input delay "
           + "a client experiences in a host-authoritative session: input travels to the "
           + "host, the host applies physics, and the result travels back. Set to 0 to "
           + "disable. Tune this to feel the difference before wiring up real networking.")]
    [Min(0f)]
    [SerializeField] float _simulatedLatencyMs = 0f;

    // ── Internals ─────────────────────────────────────────────────────────────

    Rigidbody _rb;
    SphereCollider _col;
    PhysicsMaterial _runtimeMat;
    InputSystem_Actions _actions;
    InputSystem_Actions.PlayerActions _player;

    // Raw input, written immediately by input callbacks.
    Vector2 _rawMoveInput;
    bool _rawJumpQueued;
    bool _rawSprintHeld;

    bool _isGrounded;

    bool looking;

    struct InputSnapshot
    {
        public float timestamp;
        public Vector2 moveInput;
        public bool jumpPressed;
        public bool sprintHeld;
    }

    readonly Queue<InputSnapshot> _inputQueue = new Queue<InputSnapshot>();

    // ── Unity Lifecycle ───────────────────────────────────────────────────────

    void Awake()
    {
        _rb = GetComponent<Rigidbody>();
        _col = GetComponent<SphereCollider>();

        SyncDragToRigidbody();
        CreateAndApplyPhysicsMaterial();

        if (_cameraTransform == null && Camera.main != null)
            _cameraTransform = Camera.main.transform;

        _actions = new InputSystem_Actions();
        _player = _actions.Player;
        _player.AddCallbacks(this);
    }

    void OnEnable()
    {
        _player.Enable();
    }

    void OnDisable()
    {
        _player.Disable();
    }

    void OnDestroy()
    {
        _actions.Dispose();
    }

    /// <summary>
    /// Pushes Inspector changes into live objects while the game is running.
    /// Lets you tune friction and drag without stopping Play mode.
    /// </summary>
    void OnValidate()
    {
        if (_runtimeMat != null)
        {
            _runtimeMat.dynamicFriction = _dynamicFriction;
            _runtimeMat.staticFriction = _staticFriction;
            _runtimeMat.bounciness = _bounciness;
        }

        if (_rb != null)
            SyncDragToRigidbody();
    }

    void FixedUpdate()
    {
        _isGrounded = CheckGround();

        // Snapshot raw input, then retrieve the version delayed by _simulatedLatencyMs.
        EnqueueSnapshot();
        DrainDelayedInput(out Vector2 moveInput, out bool jumpQueued, out bool sprintHeld);

        ApplyMovement(moveInput, sprintHeld);

        if (jumpQueued)
            ExecuteJump();
    }

    // ── Latency queue ─────────────────────────────────────────────────────────

    void EnqueueSnapshot()
    {
        _inputQueue.Enqueue(new InputSnapshot
        {
            timestamp = Time.fixedTime,
            moveInput = _rawMoveInput,
            jumpPressed = _rawJumpQueued,
            sprintHeld = _rawSprintHeld
        });

        _rawJumpQueued = false; // consumed into the queue; don't double-fire
    }

    /// <summary>
    /// Dequeues all snapshots whose timestamp is old enough to satisfy the
    /// configured latency. Returns the most-recent dequeued move input,
    /// sprint state, and the logical OR of any jump presses in the drained window.
    /// When latency is 0 the snapshot enqueued this tick is drained immediately.
    /// </summary>
    void DrainDelayedInput(out Vector2 moveInput, out bool jumpQueued, out bool sprintHeld)
    {
        float threshold = Time.fixedTime - _simulatedLatencyMs / 1000f;
        moveInput = Vector2.zero;
        jumpQueued = false;
        sprintHeld = false;

        while (_inputQueue.Count > 0 && _inputQueue.Peek().timestamp <= threshold)
        {
            InputSnapshot s = _inputQueue.Dequeue();
            moveInput = s.moveInput;
            jumpQueued |= s.jumpPressed;
            sprintHeld = s.sprintHeld;
        }
    }

    // ── IPlayerActions ────────────────────────────────────────────────────────

    public void OnMove(InputAction.CallbackContext context)
    {
        _rawMoveInput = context.ReadValue<Vector2>();
    }

    public void OnJump(InputAction.CallbackContext context)
    {
        // Latch on started so short presses aren't dropped between FixedUpdate ticks.
        if (context.started && _isGrounded)
            _rawJumpQueued = true;
    }

    public void OnLook(InputAction.CallbackContext context) { }

    public void OnToggleLooking(InputAction.CallbackContext context) { }

    public void OnSprint(InputAction.CallbackContext context)
    {
        _rawSprintHeld = context.ReadValue<float>() == 1f;
    }

    // ── Ground Check ──────────────────────────────────────────────────────────

    bool CheckGround()
    {
        // Sphere slightly below center so it only triggers when touching the floor.
        Vector3 origin = transform.position + Vector3.down * (_groundCheckRadius - 0.05f);
        return Physics.CheckSphere(origin, _groundCheckRadius, _groundMask, QueryTriggerInteraction.Ignore);
    }

    // ── Movement ──────────────────────────────────────────────────────────────

    void ApplyMovement(Vector2 moveInput, bool sprintHeld)
    {
        if (moveInput == Vector2.zero)
            return;

        float currentMaxSpeed = sprintHeld ? _maxRunHorizontalSpeed : _maxWalkHorizontalSpeed;

        // Don't add more force once the horizontal speed cap is reached.
        Vector3 flatVel = new Vector3(_rb.linearVelocity.x, 0f, _rb.linearVelocity.z);
        if (flatVel.magnitude >= currentMaxSpeed)
            return;

        Vector3 moveDir = BuildCameraRelativeMoveDir(moveInput.x, moveInput.y);
        float control = _isGrounded ? 1f : _airControlFraction;
        _rb.AddForce(moveDir * _moveForce * control, ForceMode.Force);
    }

    /// <summary>
    /// Projects camera forward/right onto the XZ plane so movement is always
    /// relative to where the camera is looking.
    /// </summary>
    Vector3 BuildCameraRelativeMoveDir(float h, float v)
    {
        if (_cameraTransform != null)
        {
            Vector3 forward = Vector3.ProjectOnPlane(_cameraTransform.forward, Vector3.up).normalized;
            Vector3 right = Vector3.ProjectOnPlane(_cameraTransform.right, Vector3.up).normalized;
            return (forward * v + right * h).normalized;
        }

        // Fallback: world-space axes.
        return new Vector3(h, 0f, v).normalized;
    }

    // ── Jump ──────────────────────────────────────────────────────────────────

    void ExecuteJump()
    {
        // Zero vertical velocity before the impulse so jump height is consistent
        // regardless of whether the meatball was sliding down a slope.
        _rb.linearVelocity = new Vector3(_rb.linearVelocity.x, 0f, _rb.linearVelocity.z);
        _rb.AddForce(Vector3.up * _jumpImpulse, ForceMode.Impulse);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    void SyncDragToRigidbody()
    {
        _rb.linearDamping = _linearDrag;
        _rb.angularDamping = _angularDrag;
    }

    void CreateAndApplyPhysicsMaterial()
    {
        _runtimeMat = new PhysicsMaterial("MeatballSolo_Runtime")
        {
            dynamicFriction = _dynamicFriction,
            staticFriction = _staticFriction,
            bounciness = _bounciness,
            frictionCombine = PhysicsMaterialCombine.Multiply,
            bounceCombine = PhysicsMaterialCombine.Minimum,
        };

        _col.material = _runtimeMat;
    }
}