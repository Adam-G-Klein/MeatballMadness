using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Orbits the camera around the first MeatballSolo found in the scene.
/// The Look axis (right stick / mouse delta) drives horizontal and vertical orbit.
/// Attach to the Camera GameObject.
/// </summary>
public class MeatballOrbitCamera : MonoBehaviour, InputSystem_Actions.IPlayerActions
{
    // ── Orbit ─────────────────────────────────────────────────────────────────

    [Header("Orbit")]
    [Tooltip("Distance (m) from the target meatball.")]
    [SerializeField] float _orbitDistance = 8f;

    [Tooltip("Horizontal orbit degrees per unit of Look input per second.")]
    [SerializeField] float _orbitSpeedH = 180f;

    [Tooltip("Vertical orbit degrees per unit of Look input per second.")]
    [SerializeField] float _orbitSpeedV = 120f;

    [Tooltip("Minimum vertical angle (degrees). Keeps camera above the ground plane.")]
    [SerializeField] float _minPitch = -10f;

    [Tooltip("Maximum vertical angle (degrees). Keeps camera from flipping over the top.")]
    [SerializeField] float _maxPitch = 75f;

    // ── Internals ─────────────────────────────────────────────────────────────

    InputSystem_Actions _actions;
    InputSystem_Actions.PlayerActions _player;

    Transform _target;
    Vector2   _lookInput;
    float     _yaw;
    float     _pitch = 20f; // sensible default elevation on start
    bool _lookingToggled;
    Quaternion _currentRotation;

    // ── Unity Lifecycle ───────────────────────────────────────────────────────

    void Awake()
    {
        _actions = new InputSystem_Actions();
        _player  = _actions.Player;
        _player.AddCallbacks(this);
    }

    void Start()
    {
        MeatballSolo meatball = FindFirstObjectByType<MeatballSolo>();
        if (meatball != null)
            _target = meatball.transform;
        else
            Debug.LogWarning("[MeatballOrbitCamera] No MeatballSolo found in scene — camera has no target.");
    }

    void OnEnable()  => _player.Enable();
    void OnDisable() => _player.Disable();
    void OnDestroy() => _actions.Dispose();

    /// <summary>
    /// LateUpdate runs after physics and movement, so the meatball position is settled
    /// before we reposition the camera.
    /// </summary>
    void LateUpdate()
    {
        if (_target == null) return;

        transform.position  = _target.position - _currentRotation * Vector3.forward * _orbitDistance;
        transform.LookAt(_target.position, Vector3.up);


        // Accumulate orbit angles from Look input.
        _yaw   += _lookInput.x * _orbitSpeedH * Time.deltaTime;
        _pitch  = Mathf.Clamp(
            _pitch - _lookInput.y * _orbitSpeedV * Time.deltaTime, // subtract: stick-up should decrease pitch
            _minPitch, _maxPitch);

        // Compute camera position: rotate a backwards vector by the orbit angles,
        // then offset from the target.
        _currentRotation = Quaternion.Euler(_pitch, _yaw, 0f);
    }

    // ── IPlayerActions ────────────────────────────────────────────────────────

    public void OnLook(InputAction.CallbackContext context)
    {
        _lookInput = context.ReadValue<Vector2>();
    }

    public void OnMove(InputAction.CallbackContext context)   { }
    public void OnJump(InputAction.CallbackContext context)   { }
    public void OnSprint(InputAction.CallbackContext context) { }

    public void OnToggleLooking(InputAction.CallbackContext context)
    {
        Debug.Log("Looking: " + context.ReadValue<float>());
        _lookingToggled = Mathf.Approximately(context.ReadValue<float>(), 1);
    }
}
