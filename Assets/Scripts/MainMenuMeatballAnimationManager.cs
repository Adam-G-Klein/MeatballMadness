using UnityEngine;

/// <summary>
/// Scene-scoped singleton that drives the main-menu meatball animatic. Each frame it
/// checks every <see cref="MeatballPhysicsController"/> currently in the scene against
/// <see cref="Camera.main"/>'s view frustum. When every meatball has drifted out of view,
/// it asks <see cref="MeatballCheckpointReturnManager"/> to recall everyone to the
/// active checkpoint.
///
/// The mere presence of this singleton also flips
/// <see cref="MeatballCheckpointReturnManager.ShouldSimulate"/> to true even when
/// there is no NetworkManager in the scene, which is how recall logic runs in the
/// main menu where no NGO host exists.
///
/// This component is intentionally NOT marked DontDestroyOnLoad, so it is destroyed
/// automatically when the scene unloads.
/// </summary>
[DisallowMultipleComponent]
[DefaultExecutionOrder(100)] // after physics controllers so freshly spawned meatballs are visible to FindObjectsByType
public class MainMenuMeatballAnimationManager : MonoBehaviour
{
    public static MainMenuMeatballAnimationManager Instance { get; private set; }

    [Header("Visibility Check")]
    [Tooltip("How often (seconds) to test meatball visibility against Camera.main. 0 = every frame.")]
    [SerializeField] private float _checkInterval = 0.25f;

    [Tooltip("Skip the visibility check for this many seconds after a recall, so the meatballs " +
             "have time to settle at the checkpoint before another off-screen test runs.")]
    [SerializeField] private float _postRecallCooldown = 1f;

    [Tooltip("Maximum distance from Camera.main a meatball can be while still counting " +
             "as 'on-camera'. Beyond this it's treated as out-of-shot even if it happens " +
             "to be inside the frustum.")]
    [SerializeField] private float _maxDistanceFromCamera = 10f;

    [Tooltip("If true, log when a recall is triggered and which meatball was off-screen.")]
    [SerializeField] private bool _logRecalls = true;

    [Header("Long-Distance Recall")]
    [Tooltip("If all meatballs stay further than this distance from Camera.main for the cooldown duration, a recall is triggered.")]
    [SerializeField] private float _longRecallDistance = 45f;

    [Tooltip("How long (seconds) all meatballs must remain beyond the long-recall distance before a recall fires.")]
    [SerializeField] private float _longRecallCooldownDuration = 6f;

    private float _nextCheckTime;
    private float _recallCooldownUntil;
    private float _allFarSince = float.MaxValue;
    private readonly Plane[] _frustumPlanes = new Plane[6];

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Debug.LogWarning("[MainMenuMeatballAnimationManager] Duplicate singleton found. Destroying duplicate.");
            Destroy(gameObject);
            return;
        }

        Instance = this;
    }

    private void OnDestroy()
    {
        if (Instance == this)
            Instance = null;
    }

    private void Update()
    {
        if (Time.time < _recallCooldownUntil) return;
        if (Time.time < _nextCheckTime) return;
        _nextCheckTime = Time.time + _checkInterval;

        Camera cam = Camera.main;
        if (cam == null) return;

        MeatballPhysicsController[] meatballs = FindObjectsByType<MeatballPhysicsController>(FindObjectsSortMode.None);
        if (meatballs.Length == 0) return;

        GeometryUtility.CalculateFrustumPlanes(cam, _frustumPlanes);

        int considered = 0;
        bool anyVisibleAndClose = false;
        bool anyWithinLongRecallDistance = false;

        for (int i = 0; i < meatballs.Length; i++)
        {
            MeatballPhysicsController mb = meatballs[i];
            if (mb == null) continue;

            considered++;
            if (IsVisibleAndClose(mb, cam))
                anyVisibleAndClose = true;

            Vector3 pos = mb.Rigidbody != null ? mb.Rigidbody.position : mb.transform.position;
            if ((pos - cam.transform.position).sqrMagnitude <= _longRecallDistance * _longRecallDistance)
                anyWithinLongRecallDistance = true;
        }

        if (considered == 0) return;

        // Off-screen recall: all meatballs have left the camera view.
        if (!anyVisibleAndClose)
        {
            if (_logRecalls)
                Debug.Log($"[MainMenuMeatballAnimationManager] All {considered} meatball(s) off-camera — recalling all players.");

            _allFarSince = float.MaxValue;
            TriggerRecall();
            return;
        }

        // Long-distance recall: all meatballs have been beyond _longRecallDistance for too long.
        if (!anyWithinLongRecallDistance)
        {
            if (_allFarSince == float.MaxValue)
                _allFarSince = Time.time;

            if (Time.time - _allFarSince >= _longRecallCooldownDuration)
            {
                if (_logRecalls)
                    Debug.Log($"[MainMenuMeatballAnimationManager] All {considered} meatball(s) beyond {_longRecallDistance}u for {_longRecallCooldownDuration}s — recalling all players.");

                _allFarSince = float.MaxValue;
                TriggerRecall();
            }
        }
        else
        {
            _allFarSince = float.MaxValue;
        }
    }

    private bool IsVisibleAndClose(MeatballPhysicsController mb, Camera cam)
    {
        Vector3 pos = mb.Rigidbody != null ? mb.Rigidbody.position : mb.transform.position;
        if ((pos - cam.transform.position).sqrMagnitude > _maxDistanceFromCamera * _maxDistanceFromCamera)
            return false;

        // Prefer renderer bounds so off-screen detection accounts for the chef visual,
        // not just the meatball collider centerpoint. Fall back to a small AABB around
        // the rigidbody position if no renderer is found.
        Renderer rend = mb.GetComponentInChildren<Renderer>();
        if (rend != null)
            return GeometryUtility.TestPlanesAABB(_frustumPlanes, rend.bounds);

        Bounds b = new Bounds(pos, Vector3.one);
        return GeometryUtility.TestPlanesAABB(_frustumPlanes, b);
    }

    private void TriggerRecall()
    {
        _recallCooldownUntil = Time.time + _postRecallCooldown;
        _allFarSince = float.MaxValue;

        MeatballCheckpointReturnManager mgr = MeatballCheckpointReturnManager.Instance;
        if (mgr == null)
        {
            Debug.LogWarning("[MainMenuMeatballAnimationManager] No MeatballCheckpointReturnManager in scene.");
            return;
        }

        mgr.RecallAllPlayers();
    }
}
