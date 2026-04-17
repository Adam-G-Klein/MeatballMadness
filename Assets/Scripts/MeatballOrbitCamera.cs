using System.Collections;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Orbits the camera around the first MeatballSolo found in the scene.
/// The Look axis (right stick / mouse delta) drives horizontal and vertical orbit.
/// Mouse scroll wheel zooms the camera in and out.
/// Attach to the Camera GameObject.
/// </summary>
public class MeatballOrbitCamera : MonoBehaviour, InputSystem_Actions.IPlayerActions
{
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

    [Header("Offset")]
    [Tooltip("Raises the camera pivot above the meatball.")]
    [SerializeField] float _pivotHeightOffset = 1.2f;

    [Header("Zoom")]
    [Tooltip("Minimum zoom distance.")]
    [SerializeField] float _minOrbitDistance = 3f;

    [Tooltip("Maximum zoom distance.")]
    [SerializeField] float _maxOrbitDistance = 12f;

    [Tooltip("How fast the mouse wheel zooms the camera.")]
    [SerializeField] float _scrollZoomSpeed = 0.02f;

    [Header("Position Smoothing")]
    [Tooltip("Follow speed (m/s) when the meatball is near the camera pivot (lag distance near zero).")]
    [SerializeField] float _minFollowSpeed = 3f;

    [Tooltip("Follow speed (m/s) when the meatball reaches MaxLagDistance from the camera pivot.")]
    [SerializeField] float _maxFollowSpeed = 25f;

    [Tooltip("Lag distance (m) at which the follow speed curve reaches its maximum.")]
    [SerializeField] float _maxLagDistance = 8f;

    [Tooltip("Speed curve for follow smoothing.")]
    [SerializeField] AnimationCurve _followSpeedCurve = AnimationCurve.EaseInOut(0f, 0f, 1f, 1f);

    [Tooltip("If the target moves farther than this distance from the camera pivot, the pivot snaps instantly.")]
    [SerializeField] float _snapDistance = 20f;

    [Header("Look")]
    [Tooltip("If enabled, the player must hold right mouse button to rotate the camera with the mouse.")]
    [SerializeField] private bool _requireRightMouseButtonToRotate = false;

    InputSystem_Actions _actions;
    InputSystem_Actions.PlayerActions _player;

    Transform _target;
    Vector2 _lookInput;
    float _yaw;
    float _pitch = 20f;
    Quaternion _currentRotation;
    Vector3 _smoothedPivot;
    bool _pivotInitialized;

    // Public access for debugger
    public float OrbitSpeedH
    {
        get => _orbitSpeedH;
        set => _orbitSpeedH = Mathf.Max(0f, value);
    }

    public float OrbitSpeedV
    {
        get => _orbitSpeedV;
        set => _orbitSpeedV = Mathf.Max(0f, value);
    }

    public bool RequireRightMouseButtonToRotate
    {
        get => _requireRightMouseButtonToRotate;
        set => _requireRightMouseButtonToRotate = value;
    }

    void Awake()
    {
        _actions = new InputSystem_Actions();
        _player = _actions.Player;
        _player.AddCallbacks(this);
    }

    void Start()
    {
        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;

        MeatballSolo meatball = FindFirstObjectByType<MeatballSolo>();
        if (meatball != null)
        {
            _target = meatball.transform;
        }
        else
        {
            StartCoroutine(searchForMeatball());
        }

        _orbitDistance = Mathf.Clamp(_orbitDistance, _minOrbitDistance, _maxOrbitDistance);
    }

    private IEnumerator searchForMeatball()
    {
        while (_target == null)
        {
            MeatballClientInputHandler meatballClient = FindFirstObjectByType<MeatballClientInputHandler>();
            if (meatballClient != null)
            {
                _target = meatballClient.transform;
                yield return null;
            }

            yield return null;
        }
    }

    void OnEnable() => _player.Enable();
    void OnDisable() => _player.Disable();
    void OnDestroy() => _actions.Dispose();

    void LateUpdate()
    {
        if (_target == null)
            return;

        HandleScrollZoom();

        Vector3 targetPos = _target.position + Vector3.up * _pivotHeightOffset;

        if (!_pivotInitialized)
        {
            _smoothedPivot = targetPos;
            _pivotInitialized = true;
        }

        float lagDist = Vector3.Distance(_smoothedPivot, targetPos);

        if (_snapDistance > 0f && lagDist > _snapDistance)
        {
            _smoothedPivot = targetPos;
        }
        else
        {
            float t = Mathf.Clamp01(lagDist / _maxLagDistance);
            float speed = Mathf.Lerp(_minFollowSpeed, _maxFollowSpeed, _followSpeedCurve.Evaluate(t));
            _smoothedPivot = Vector3.MoveTowards(_smoothedPivot, targetPos, speed * Time.deltaTime);
        }

        Vector2 effectiveLook = _lookInput;

        if (_requireRightMouseButtonToRotate)
        {
            if (Mouse.current == null || !Mouse.current.rightButton.isPressed)
                effectiveLook = Vector2.zero;
        }

        _yaw += effectiveLook.x * _orbitSpeedH * Time.deltaTime;
        _pitch = Mathf.Clamp(
            _pitch - effectiveLook.y * _orbitSpeedV * Time.deltaTime,
            _minPitch,
            _maxPitch
        );

        _currentRotation = Quaternion.Euler(_pitch, _yaw, 0f);

        transform.position = _smoothedPivot - _currentRotation * Vector3.forward * _orbitDistance;
        transform.LookAt(_smoothedPivot, Vector3.up);
    }

    void HandleScrollZoom()
    {
        if (Mouse.current == null)
            return;

        float scrollY = Mouse.current.scroll.ReadValue().y;
        if (Mathf.Abs(scrollY) < 0.01f)
            return;

        _orbitDistance -= scrollY * _scrollZoomSpeed;
        _orbitDistance = Mathf.Clamp(_orbitDistance, _minOrbitDistance, _maxOrbitDistance);
    }

    public void OnLook(InputAction.CallbackContext context)
    {
        _lookInput = context.ReadValue<Vector2>();
    }

    public void OnMove(InputAction.CallbackContext context) { }
    public void OnJump(InputAction.CallbackContext context) { }
    public void OnSprint(InputAction.CallbackContext context) { }
    public void OnToggleLooking(InputAction.CallbackContext context) { }
    public void OnMenu(InputAction.CallbackContext context) { }
}