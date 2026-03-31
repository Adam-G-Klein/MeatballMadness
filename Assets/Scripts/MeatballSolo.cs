using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Standalone single-player test-bed for meatball physics and controls.
/// No network dependencies — attach to a sphere GameObject that has a Rigidbody
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

    [Tooltip("Horizontal speed (m/s) at which force application stops. Acts as a soft cap.")]
    [SerializeField] float _maxHorizontalSpeed = 8f;

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

    // ── Internals ─────────────────────────────────────────────────────────────

    Rigidbody              _rb;
    SphereCollider         _col;
    PhysicsMaterial        _runtimeMat;
    InputSystem_Actions    _actions;
    InputSystem_Actions.PlayerActions _player;

    Vector2 _moveInput;
    bool    _jumpQueued;
    bool    _isGrounded;

    bool looking;

    // ── Unity Lifecycle ───────────────────────────────────────────────────────

    void Awake()
    {
        _rb  = GetComponent<Rigidbody>();
        _col = GetComponent<SphereCollider>();

        SyncDragToRigidbody();
        CreateAndApplyPhysicsMaterial();

        if (_cameraTransform == null && Camera.main != null)
            _cameraTransform = Camera.main.transform;

        _actions = new InputSystem_Actions();
        _player  = _actions.Player;
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
            _runtimeMat.staticFriction  = _staticFriction;
            _runtimeMat.bounciness      = _bounciness;
        }
        if (_rb != null) SyncDragToRigidbody();
    }

    void FixedUpdate()
    {
        _isGrounded = CheckGround();
        ApplyMovement();
        if (_jumpQueued) ExecuteJump();
    }

    // ── IPlayerActions ────────────────────────────────────────────────────────

    public void OnMove(InputAction.CallbackContext context)
    {
        _moveInput = context.ReadValue<Vector2>();
    }

    public void OnJump(InputAction.CallbackContext context)
    {
        // Latch on started so short presses aren't dropped between FixedUpdate ticks.
        if (context.started && _isGrounded)
            _jumpQueued = true;
    }

    public void OnLook(InputAction.CallbackContext context) { }
    public void OnToggleLooking(InputAction.CallbackContext context) {}

    public void OnSprint(InputAction.CallbackContext context) { }

    // ── Ground Check ──────────────────────────────────────────────────────────

    bool CheckGround()
    {
        // Sphere slightly below center so it only triggers when touching the floor.
        Vector3 origin = transform.position + Vector3.down * (_groundCheckRadius - 0.05f);
        return Physics.CheckSphere(origin, _groundCheckRadius, _groundMask, QueryTriggerInteraction.Ignore);
    }

    // ── Movement ──────────────────────────────────────────────────────────────

    void ApplyMovement()
    {
        if (_moveInput == Vector2.zero) return;

        // Don't add more force once the horizontal speed cap is reached.
        Vector3 flatVel = new Vector3(_rb.linearVelocity.x, 0f, _rb.linearVelocity.z);
        if (flatVel.magnitude >= _maxHorizontalSpeed) return;

        Vector3 moveDir = BuildCameraRelativeMoveDir(_moveInput.x, _moveInput.y);
        float   control = _isGrounded ? 1f : _airControlFraction;
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
            Vector3 right   = Vector3.ProjectOnPlane(_cameraTransform.right,   Vector3.up).normalized;
            return (forward * v + right * h).normalized;
        }
        // Fallback: world-space axes.
        return new Vector3(h, 0f, v).normalized;
    }

    // ── Jump ──────────────────────────────────────────────────────────────────

    void ExecuteJump()
    {
        _jumpQueued = false;
        // Zero vertical velocity before the impulse so jump height is consistent
        // regardless of whether the meatball was sliding down a slope.
        _rb.linearVelocity = new Vector3(_rb.linearVelocity.x, 0f, _rb.linearVelocity.z);
        _rb.AddForce(Vector3.up * _jumpImpulse, ForceMode.Impulse);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    void SyncDragToRigidbody()
    {
        _rb.linearDamping  = _linearDrag;
        _rb.angularDamping = _angularDrag;
    }

    void CreateAndApplyPhysicsMaterial()
    {
        _runtimeMat = new PhysicsMaterial("MeatballSolo_Runtime")
        {
            dynamicFriction = _dynamicFriction,
            staticFriction  = _staticFriction,
            bounciness      = _bounciness,
            // Multiply means the effective friction = meatball * ground surface.
            // Swap to Average if you want a simpler model.
            frictionCombine = PhysicsMaterialCombine.Multiply,
            bounceCombine   = PhysicsMaterialCombine.Minimum,
        };
        _col.material = _runtimeMat;
    }

}
