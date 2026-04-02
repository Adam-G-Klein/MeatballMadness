using System.Collections.Generic;
using Unity.Animation.Rigging;
using UnityEngine;

/// <summary>
/// Lives on the AnimatedChef prefab. Holds a list of chef skin prefabs,
/// picks one that no other active ChefAnimator is using, and spawns it
/// as a child. Uses a static registry so sibling instances can avoid
/// claiming the same skin.
///
/// Also owns root-motion tracking, facing/lean animation, and procedural leg IK.
/// Call SetFollowTarget() after spawning to drive all of the above.
///
/// Facing and lean are derived from NetworkedVelocity so they work on
/// both host and clients without needing direct input or Rigidbody access.
/// </summary>
public class ChefAnimator : MonoBehaviour
{
    [SerializeField] List<GameObject> _chefSkinPrefabs;
    [SerializeField] float _chefScale = 2f;

    [Tooltip("Height above the follow target's center.")]
    [SerializeField] float _heightOffset = 1.2f;

    [Header("Movement Response")]
    [Tooltip("How quickly the chef rotates to face the movement direction.")]
    [SerializeField] float _facingSpeed = 8f;

    [Tooltip("Minimum horizontal speed (m/s) before the chef updates its facing direction.")]
    [SerializeField] float _minSpeedForFacing = 0.2f;

    [Tooltip("Lean angle (degrees) per m/s² of horizontal acceleration.")]
    [SerializeField] float _leanFactor = 0.8f;

    [SerializeField] float _maxLeanAngle = 20f;

    [Tooltip("How quickly the lean angle tracks the current acceleration.")]
    [SerializeField] float _leanSmoothSpeed = 6f;

    [Header("IK Bones & Targets")]
    [Tooltip("IK goal transform for the left foot (TwoBoneIKConstraint data.target).")]
    [SerializeField] Transform _leftFootTarget;
    [Tooltip("IK goal transform for the right foot (TwoBoneIKConstraint data.target).")]
    [SerializeField] Transform _rightFootTarget;
    [Tooltip("Pole / hint transform for the left knee (TwoBoneIKConstraint data.hint).")]
    [SerializeField] Transform _leftKneePole;
    [Tooltip("Pole / hint transform for the right knee (TwoBoneIKConstraint data.hint).")]
    [SerializeField] Transform _rightKneePole;
    [Tooltip("Left upper-leg bone (TwoBoneIKConstraint data.root).")]
    [SerializeField] Transform _leftHip;
    [Tooltip("Right upper-leg bone (TwoBoneIKConstraint data.root).")]
    [SerializeField] Transform _rightHip;

    [Header("IK / Stepping")]
    [Tooltip("Radius of the meatball sphere used to project foot targets onto its surface.")]
    [SerializeField] float _meatballRadius = 1f;

    [Tooltip("Distance from hip to current foot position that triggers a new step.")]
    [SerializeField] float _maxStepDistance = 0.5f;

    [Tooltip("Time in seconds to animate a single step.")]
    [SerializeField] float _stepDuration = 0.25f;

    [Tooltip("Peak height of the foot arc during a step.")]
    [SerializeField] float _stepHeight = 0.3f;

    [Tooltip("How much meatball speed increases the forward reach of the next step.")]
    [SerializeField] float _velocityStepScale = 0.15f;

    [Tooltip("Maximum forward offset applied to a new step target (clamps velocity contribution).")]
    [SerializeField] float _maxVelocityStepOffset = 0.8f;

    [Tooltip("Left/right distance from the chef centerline to each foot target.")]
    [SerializeField] float _lateralSpread = 0.35f;

    [Tooltip("Forward distance from the knee midpoint to the pole hint.")]
    [SerializeField] float _kneePoleForwardDist = 0.5f;

    [Tooltip("Upward offset applied to each knee pole hint.")]
    [SerializeField] float _kneePoleUpDist = 0.2f;

    [Tooltip("Outward lateral offset applied to each knee pole hint.")]
    [SerializeField] float _kneePoleLatDist = 0.15f;

    // Tracks which prefabs are claimed by live ChefAnimator instances.
    static readonly HashSet<GameObject> _claimedPrefabs = new();

    GameObject _claimedPrefab;
    Transform _followTarget;
    Transform _boneChild;
    MeatballNetSync _netSync;

    // State for facing / lean computation.
    Quaternion _currentFacing = Quaternion.identity;
    Vector3 _currentLeanEuler;
    Vector3 _prevVelocity;
    bool _prevVelocityReady;

    // --- Per-leg IK state ---
    private class LegState
    {
        public bool      isLeft;
        public Transform ikTarget;       // TwoBoneIKConstraint.data.target
        public Transform ikHint;         // TwoBoneIKConstraint.data.hint (pole)
        public Transform hipBone;        // TwoBoneIKConstraint.data.root (upper leg)

        // Current foot position stored as a normalized direction in meatball LOCAL space.
        // Each frame: footWorldPos = meatballCenter + meatball.TransformDirection(footLocalDir) * radius
        public Vector3 footLocalDir;
        public Vector3 footWorldPos;

        // Step animation state
        public bool    isStepping;
        public float   stepTimer;
        public Vector3 stepFromWorld;    // foot world pos when step started
        public Vector3 stepToWorld;      // target world pos (fixed at step start)
        public Vector3 pendingLocalDir;  // new footLocalDir committed on step completion
    }

    LegState[] _legs;           // [0] = left, [1] = right
    int _steppingLegIndex = -1; // -1 = neither leg stepping

    // Convenience: velocity valid on host and clients.
    Vector3 MeatballVelocity => _netSync != null ? _netSync.NetworkedVelocity : Vector3.zero;

    void Awake()
    {
        if (_chefSkinPrefabs == null || _chefSkinPrefabs.Count == 0)
        {
            Debug.LogWarning("[ChefAnimator] No chef skin prefabs assigned.", this);
            return;
        }

        // Pick the first unclaimed prefab.
        foreach (GameObject prefab in _chefSkinPrefabs)
        {
            if (prefab != null && !_claimedPrefabs.Contains(prefab))
            {
                _claimedPrefab = prefab;
                _claimedPrefabs.Add(_claimedPrefab);
                break;
            }
        }

        if (_claimedPrefab == null)
        {
            Debug.LogWarning("[ChefAnimator] All chef skin prefabs are in use — falling back to first entry.", this);
            _claimedPrefab = _chefSkinPrefabs[0];
        }

        GameObject skin = Instantiate(_claimedPrefab, transform);
        skin.transform.localScale = Vector3.one * _chefScale;

        foreach (Component comp in GetComponentsInChildren<Component>())
        {
            if (comp.GetType().Name == "BoneRenderer")
            {
                _boneChild = comp.transform;
                break;
            }
        }
        if (_boneChild == null)
            Debug.LogWarning("[ChefAnimator] No child with BoneRenderer found.", this);
    }

    /// <summary>Called by MeatballChefController after spawning this prefab.</summary>
    public void SetFollowTarget(Transform target)
    {
        _followTarget = target;
        _netSync = target.GetComponent<MeatballNetSync>();

        if (_netSync == null)
            Debug.LogWarning("[ChefAnimator] No MeatballNetSync on follow target — velocity-based IK will be zero.", this);

        if (_boneChild != null)
            InitLegs();
    }

    // -------------------------------------------------------------------------
    // Leg IK initialization
    // -------------------------------------------------------------------------

    void InitLegs()
    {
        bool missingTargets = _leftFootTarget == null || _rightFootTarget == null;
        bool missingPoles   = _leftKneePole   == null || _rightKneePole   == null;
        bool missingHips    = _leftHip         == null || _rightHip        == null;

        if (missingTargets || missingHips)
        {
            Debug.LogWarning("[ChefAnimator] IK targets or hip bones not assigned — procedural IK disabled. " +
                             "Assign Left/RightFootTarget and Left/RightHip in the Inspector.", this);
            return;
        }

        if (missingPoles)
            Debug.LogWarning("[ChefAnimator] Knee pole transforms not assigned — knees will not be guided.", this);

        _legs = new LegState[2]
        {
            BuildLeg(isLeft: true,  _leftFootTarget,  _leftKneePole,  _leftHip),
            BuildLeg(isLeft: false, _rightFootTarget, _rightKneePole, _rightHip),
        };
    }

    LegState BuildLeg(bool isLeft, Transform ikTarget, Transform ikHint, Transform hipBone)
    {
        var leg = new LegState
        {
            isLeft   = isLeft,
            ikTarget = ikTarget,
            ikHint   = ikHint,
            hipBone  = hipBone,
        };

        // Place the initial foot on the sphere surface below the hip.
        Vector3 toHip = hipBone.position - _followTarget.position;
        Vector3 dir = toHip.magnitude > 0.001f ? toHip.normalized : Vector3.up;
        leg.footLocalDir = _followTarget.InverseTransformDirection(dir).normalized;
        leg.footWorldPos = _followTarget.position + dir * _meatballRadius;
        return leg;
    }

    // -------------------------------------------------------------------------
    // LateUpdate — ordered phases
    // -------------------------------------------------------------------------

    void LateUpdate()
    {
        if (_followTarget == null || _boneChild == null) return;

        // Phase 1: position skeleton root above meatball.
        _boneChild.position = _followTarget.position + Vector3.up * _heightOffset;

        // Phase 2: rotate chef to face velocity; apply lean.
        UpdateFacingAndLean();

        if (_legs == null) return;

        // Phase 3: advance any in-progress step animation.
        UpdateStepAnimations();

        // Phase 4: recompute planted foot world positions from meatball-local anchors
        //          (rolling tracking — the contact point moves as the sphere rotates).
        UpdatePlantedFeetWorldPositions();

        // Phase 5: check if any foot has drifted too far from its hip → begin a step.
        CheckStepTriggers();

        // Phase 6: write computed positions to IK target transforms.
        ApplyIKTargets();

        // Phase 7: position pole hints so knees always bend forward.
        UpdatePoleVectors();
    }

    // -------------------------------------------------------------------------
    // Existing facing / lean logic (unchanged)
    // -------------------------------------------------------------------------

    void UpdateFacingAndLean()
    {
        if (_netSync == null) return;

        Vector3 vel = _netSync.NetworkedVelocity;
        Vector3 horizontalVel = new(vel.x, 0f, vel.z);

        // Rotate to face movement direction when moving fast enough.
        if (horizontalVel.sqrMagnitude > _minSpeedForFacing * _minSpeedForFacing)
        {
            Quaternion targetFacing = Quaternion.LookRotation(horizontalVel.normalized, Vector3.up);
            _currentFacing = Quaternion.Slerp(_currentFacing, targetFacing, _facingSpeed * Time.deltaTime);
        }

        // Derive acceleration by differentiating velocity each frame.
        Vector3 targetLeanEuler = Vector3.zero;
        if (_prevVelocityReady)
        {
            Vector3 accel = (vel - _prevVelocity) / Time.deltaTime;
            Vector3 horizontalAccel = new(accel.x, 0f, accel.z);

            if (horizontalAccel.sqrMagnitude > 0.1f)
            {
                // Express acceleration in the chef's facing space so lean is
                // always relative to which way the chef is looking.
                Vector3 localAccel = Quaternion.Inverse(_currentFacing) * horizontalAccel;

                // Negative X = lean forward, negative Z = lean right (Unity euler conventions).
                float forwardLean = Mathf.Clamp(-localAccel.z * _leanFactor, -_maxLeanAngle, _maxLeanAngle);
                float sideLean    = Mathf.Clamp(-localAccel.x * _leanFactor, -_maxLeanAngle, _maxLeanAngle);
                targetLeanEuler = new Vector3(forwardLean, 0f, sideLean);
            }
        }

        _prevVelocity = vel;
        _prevVelocityReady = true;

        _currentLeanEuler = Vector3.Lerp(_currentLeanEuler, targetLeanEuler, _leanSmoothSpeed * Time.deltaTime);
        _boneChild.rotation = _currentFacing * Quaternion.Euler(_currentLeanEuler);
    }

    // -------------------------------------------------------------------------
    // IK phases
    // -------------------------------------------------------------------------

    void UpdateStepAnimations()
    {
        if (_steppingLegIndex < 0) return;

        LegState leg = _legs[_steppingLegIndex];

        // Guard against zero duration set in the Inspector.
        if (_stepDuration <= 0f)
        {
            CompleteStep(leg);
            return;
        }

        leg.stepTimer += Time.deltaTime;
        float t = Mathf.Clamp01(leg.stepTimer / _stepDuration);

        Vector3 flatPos = Vector3.Lerp(leg.stepFromWorld, leg.stepToWorld, t);
        float arc = Mathf.Sin(t * Mathf.PI) * _stepHeight;
        leg.footWorldPos = flatPos + Vector3.up * arc;

        if (t >= 1f)
            CompleteStep(leg);
    }

    void CompleteStep(LegState leg)
    {
        leg.footWorldPos  = leg.stepToWorld;
        leg.footLocalDir  = leg.pendingLocalDir;
        leg.isStepping    = false;
        _steppingLegIndex = -1;
    }

    void UpdatePlantedFeetWorldPositions()
    {
        foreach (LegState leg in _legs)
        {
            if (leg == null || leg.isStepping) continue;

            // TransformDirection rotates the stored local-space direction with the sphere,
            // so the contact point tracks the rolling surface without any extra math.
            leg.footWorldPos = _followTarget.position
                             + _followTarget.TransformDirection(leg.footLocalDir) * _meatballRadius;
        }
    }

    void CheckStepTriggers()
    {
        if (_steppingLegIndex >= 0) return; // enforce one leg stepping at a time

        for (int i = 0; i < _legs.Length; i++)
        {
            LegState leg = _legs[i];
            if (leg == null || leg.isStepping || leg.hipBone == null) continue;

            float dist = Vector3.Distance(leg.hipBone.position, leg.footWorldPos);
            if (dist > _maxStepDistance)
            {
                BeginStep(leg, i);
                return; // only one step trigger per frame
            }
        }
    }

    void BeginStep(LegState leg, int legIndex)
    {
        _steppingLegIndex = legIndex;
        leg.isStepping    = true;
        leg.stepTimer     = 0f;
        leg.stepFromWorld = leg.footWorldPos;

        Vector3 meatballCenter = _followTarget.position;
        Vector3 flatVel = Vector3.ProjectOnPlane(MeatballVelocity, Vector3.up);
        float velMag = flatVel.magnitude;

        // Use velocity direction when available; fall back to current chef facing.
        Vector3 forwardDir = velMag > _minSpeedForFacing ? flatVel.normalized : _boneChild.forward;
        float forwardAmount = Mathf.Clamp(velMag * _velocityStepScale, 0f, _maxVelocityStepOffset);

        float lateralSign = leg.isLeft ? -1f : 1f;
        Vector3 lateralOffset = _boneChild.right * (lateralSign * _lateralSpread);

        Vector3 rawOffset = forwardDir * forwardAmount + lateralOffset;
        Vector3 targetDir = rawOffset.magnitude > 0.001f ? rawOffset.normalized : forwardDir;

        leg.stepToWorld     = meatballCenter + targetDir * _meatballRadius;
        leg.pendingLocalDir = _followTarget.InverseTransformDirection(targetDir).normalized;
    }

    void ApplyIKTargets()
    {
        foreach (LegState leg in _legs)
        {
            if (leg == null || leg.ikTarget == null) continue;
            leg.ikTarget.position = leg.footWorldPos;
        }
    }

    void UpdatePoleVectors()
    {
        Vector3 flatVel = Vector3.ProjectOnPlane(MeatballVelocity, Vector3.up);
        Vector3 fwd = flatVel.magnitude > _minSpeedForFacing ? flatVel.normalized : _boneChild.forward;

        foreach (LegState leg in _legs)
        {
            if (leg == null || leg.ikHint == null || leg.hipBone == null) continue;

            Vector3 kneeMid = (leg.hipBone.position + leg.footWorldPos) * 0.5f;
            float latSign = leg.isLeft ? -1f : 1f;
            Vector3 lateral = _boneChild.right * (latSign * _kneePoleLatDist);

            leg.ikHint.position = kneeMid
                                + fwd        * _kneePoleForwardDist
                                + Vector3.up * _kneePoleUpDist
                                + lateral;
        }
    }

    void OnDrawGizmosSelected()
    {
        if (_legs == null) return;
        foreach (LegState leg in _legs)
        {
            if (leg == null) continue;
            Gizmos.color = leg.isStepping ? Color.yellow : Color.green;
            Gizmos.DrawSphere(leg.footWorldPos, 0.05f);
            if (leg.ikHint != null)
            {
                Gizmos.color = Color.cyan;
                Gizmos.DrawSphere(leg.ikHint.position, 0.04f);
                if (leg.hipBone != null)
                    Gizmos.DrawLine(leg.hipBone.position, leg.ikHint.position);
            }
        }
    }

    void OnDestroy()
    {
        if (_claimedPrefab != null)
            _claimedPrefabs.Remove(_claimedPrefab);
    }
}
