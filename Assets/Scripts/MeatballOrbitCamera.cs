using System.Collections;
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

    // ── Position Smoothing ────────────────────────────────────────────────────

    [Header("Position Smoothing")]
    [Tooltip("Follow speed (m/s) when the meatball is near the camera pivot (lag distance near zero).")]
    [SerializeField] float _minFollowSpeed = 3f;

    [Tooltip("Follow speed (m/s) when the meatball reaches MaxLagDistance from the camera pivot.")]
    [SerializeField] float _maxFollowSpeed = 25f;

    [Tooltip("Lag distance (m) at which the follow speed curve reaches its maximum. Beyond this, speed is clamped to MaxFollowSpeed.")]
    [SerializeField] float _maxLagDistance = 8f;

    [Tooltip("Speed curve: X = lag distance normalized 0–1 (0 = no lag, 1 = MaxLagDistance), Y = speed lerp 0–1 (0 = MinFollowSpeed, 1 = MaxFollowSpeed). Increase curvature to make the camera snap harder when far behind.")]
    [SerializeField] AnimationCurve _followSpeedCurve = AnimationCurve.EaseInOut(0f, 0f, 1f, 1f);

    // ── Internals ─────────────────────────────────────────────────────────────

    InputSystem_Actions _actions;
    InputSystem_Actions.PlayerActions _player;

    Transform  _target;
    Vector2    _lookInput;
    float      _yaw;
    float      _pitch = 20f; // sensible default elevation on start
    bool       _lookingToggled;
    Quaternion _currentRotation;
    Vector3    _smoothedPivot;
    bool       _pivotInitialized;

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
        if (meatball != null) {
            _target = meatball.transform;
        } else
        {
            StartCoroutine(searchForMeatball());
        }
    }

    private IEnumerator searchForMeatball()
    {
        // TODO: assign camera on network spawn
        while(_target == null)
        {
            MeatballClientInputHandler meatballClient = FindFirstObjectByType<MeatballClientInputHandler>();
            if(meatballClient != null)
            {
                _target = meatballClient.transform;
                yield return null;
            }
            yield return null;
        }
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

        // Snap pivot on the first frame so the camera doesn't sweep from world origin.
        if (!_pivotInitialized)
        {
            _smoothedPivot    = _target.position;
            _pivotInitialized = true;
        }

        // Smooth the pivot toward the meatball. Speed is driven by the curve:
        // slow when the meatball is near-centered, faster as lag distance grows.
        float lagDist  = Vector3.Distance(_smoothedPivot, _target.position);
        float t        = Mathf.Clamp01(lagDist / _maxLagDistance);
        float speed    = Mathf.Lerp(_minFollowSpeed, _maxFollowSpeed, _followSpeedCurve.Evaluate(t));
        _smoothedPivot = Vector3.MoveTowards(_smoothedPivot, _target.position, speed * Time.deltaTime);

        // Accumulate orbit angles from Look input.
        _yaw   += _lookInput.x * _orbitSpeedH * Time.deltaTime;
        _pitch  = Mathf.Clamp(
            _pitch - _lookInput.y * _orbitSpeedV * Time.deltaTime, // subtract: stick-up decreases pitch
            _minPitch, _maxPitch);

        _currentRotation = Quaternion.Euler(_pitch, _yaw, 0f);

        // Position and orient the camera around the smoothed pivot.
        transform.position = _smoothedPivot - _currentRotation * Vector3.forward * _orbitDistance;
        transform.LookAt(_smoothedPivot, Vector3.up);
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
        _lookingToggled = Mathf.Approximately(context.ReadValue<float>(), 1);
    }
}
