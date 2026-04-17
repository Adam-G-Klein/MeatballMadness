using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Lives on the AnimatedChef prefab. Spawns a single chef skin prefab as a child,
/// then selects hat/gloves/shirt meshes based on this client's lobby index so every
/// player looks distinct without claiming from a list.
///
/// Owns facing/lean animation driven from NetworkedVelocity.
/// Call SetFollowTarget() after spawning to drive all of the above.
/// </summary>
public class ChefAnimator : MonoBehaviour
{
    [SerializeField] GameObject _chefSkinPrefab;
    [SerializeField] float _chefScale = 2f;

    [Tooltip("Additional height above the ball surface (added on top of meatball radius).")]
    [SerializeField] float _heightOffset = 0.2f;

    [Tooltip("Radius of the meatball sphere used to position the chef above it.")]
    [SerializeField] float _meatballRadius = 1f;

    [Header("Animation")]
    [Tooltip("Horizontal speed (m/s) mapped to 1× walk animation playback speed.")]
    [SerializeField] float _walkSpeedReference = 5f;

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

    [Header("Spine / Lean Bone")]
    [Tooltip("Name of the spine bone that receives the directional lean rotation.")]
    [SerializeField] string _spineBoneName = "Spine";

    [Header("Upper Body Input Response")]
    [Tooltip("Maximum angle (degrees) the spine can twist/lean away from the velocity-facing direction.")]
    [SerializeField] float _maxSpineDeltaAngle = 45f;

    [Tooltip("How quickly the spine offset smoothly tracks the target.")]
    [SerializeField] float _spineLerpSpeed = 8f;

    [Tooltip("Scale for twist (Y-axis rotation). 1 = full clamped delta angle.")]
    [SerializeField] float _spineTwistScale = 1f;

    [Tooltip("Scale for lean (Z-axis rotation). Positive = lean into the turn.")]
    [SerializeField] float _spineLeanScale = 0.5f;

    [Tooltip("Angular range (degrees) around 180° that counts as 'opposite' the facing direction and triggers a backward lean.")]
    [SerializeField] float _backwardLeanThreshold = 45f;

    [Tooltip("Degrees to lean backward (X rotation) when input is opposite the facing direction.")]
    [SerializeField] float _backwardLeanAngle = 15f;

    [Tooltip("Degrees to lean forward (X rotation) when sprinting. Suppressed if a backward lean is active.")]
    [SerializeField] float _forwardLeanAngle = 10f;

    [Header("Chef Skin Parts")]
    [Tooltip("Name of the Hat child GameObject inside the chef skin.")]
    [SerializeField] string _hatObjectName = "Hat";

    [Tooltip("Name of the Gloves child GameObject inside the chef skin.")]
    [SerializeField] string _glovesObjectName = "Gloves";

    [Tooltip("Name of the Shirt child GameObject inside the chef skin.")]
    [SerializeField] string _shirtObjectName = "Shirt";

    [Tooltip("One mesh per lobby slot. Index 0 = first player, 1 = second, etc.")]
    [SerializeField] List<Mesh> _hatMeshes;

    [Tooltip("One mesh per lobby slot. Index 0 = first player, 1 = second, etc.")]
    [SerializeField] List<Mesh> _glovesMeshes;

    [Tooltip("One mesh per lobby slot. Index 0 = first player, 1 = second, etc.")]
    [SerializeField] List<Mesh> _shirtMeshes;

    Transform _skinRoot;
    Transform _spineBone;
    Animator _animator;

    Transform _followTarget;
    MeatballNetSync _netSync;
    MeatballClientInputHandler _inputHandler;
    float _smoothedSpineAngle;
    float _smoothedBackwardLean;
    float _smoothedForwardLean;

    void Awake()
    {
        if (_chefSkinPrefab == null)
        {
            Debug.LogWarning("[ChefAnimator] No chef skin prefab assigned.", this);
            return;
        }

        GameObject skin = Instantiate(_chefSkinPrefab, transform);
        skin.transform.localScale = Vector3.one * _chefScale;

        _animator = skin.GetComponentInChildren<Animator>();
        if (_animator == null)
            Debug.LogWarning("[ChefAnimator] No Animator found on chef skin.", this);

        _skinRoot = skin.transform;

        _spineBone = FindInChildren(skin.transform, _spineBoneName);
        if (_spineBone == null)
            Debug.LogWarning($"[ChefAnimator] Spine bone '{_spineBoneName}' not found in chef skin.", this);
    }

    /// <summary>Called by MeatballChefController after spawning this prefab.</summary>
    public void SetFollowTarget(Transform target)
    {
        _followTarget = target;
        _netSync = target.GetComponent<MeatballNetSync>();
        _inputHandler = target.GetComponent<MeatballClientInputHandler>();

        if (_netSync == null)
        {
            Debug.LogWarning("[ChefAnimator] No MeatballNetSync on follow target — velocity-based lean will be zero.", this);
            return;
        }

        // If the host already assigned a skin index before we subscribed, apply it immediately.
        // Otherwise wait for the ClientRpc to arrive.
        if (_netSync.SkinIndex >= 0)
            ApplySkin(_netSync.SkinIndex);
        else
            _netSync.OnSkinIndexAssigned += ApplySkin;
    }

    void ApplySkin(int index)
    {
        _netSync.OnSkinIndexAssigned -= ApplySkin;

        if (_skinRoot == null) return;

        ApplyMesh(_skinRoot, _hatObjectName, _hatMeshes, index);
        ApplyMesh(_skinRoot, _glovesObjectName, _glovesMeshes, index);
        ApplyMesh(_skinRoot, _shirtObjectName, _shirtMeshes, index);
    }

    /// <summary>
    /// Finds the named child and writes the indexed mesh into its SkinnedMeshRenderer
    /// (or MeshFilter for non-skinned objects). Uses modulo so the list never needs to
    /// be as long as the maximum player count.
    /// </summary>
    void ApplyMesh(Transform root, string objectName, List<Mesh> meshes, int index)
    {
        if (meshes == null || meshes.Count == 0) return;

        Transform target = FindInChildren(root, objectName);
        if (target == null) return;

        Mesh mesh = meshes[index % meshes.Count];

        if (target.TryGetComponent<SkinnedMeshRenderer>(out var skinnedMR))
        {
            skinnedMR.sharedMesh = mesh;
            return;
        }

        if (target.TryGetComponent<MeshFilter>(out var meshFilter))
            meshFilter.sharedMesh = mesh;
        else
            Debug.LogWarning($"[ChefAnimator] '{objectName}' has neither SkinnedMeshRenderer nor MeshFilter.", this);
    }

    static Transform FindInChildren(Transform root, string name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        foreach (Transform t in root.GetComponentsInChildren<Transform>(includeInactive: true))
        {
            if (t.name == name) return t;
        }
        Debug.LogWarning($"[ChefAnimator] Could not find '{name}' in chef skin hierarchy.");
        return null;
    }

    void LateUpdate()
    {
        if (_followTarget == null) return;

        // Phase 1: position skeleton root on top of meatball.
        _skinRoot.position = _followTarget.position + Vector3.up * (_meatballRadius + _heightOffset);

        // Phase 2: rotate chef to face velocity direction (lower body).
        UpdateFacingAndLean();

        // Phase 3: twist/lean spine bone toward player input direction (upper body).
        UpdateSpineLean();
    }

    void UpdateFacingAndLean()
    {
        if (_netSync == null) return;

        Vector3 vel = _netSync.NetworkedVelocity;
        Vector3 horizontalVel = new(vel.x, 0f, vel.z);

        // Drive idle ↔ walk transitions and walk playback speed.
        if (_animator != null)
        {
            float normalizedSpeed = Mathf.Clamp(horizontalVel.magnitude / _walkSpeedReference, 0f, 2f);
            _animator.SetFloat("Speed", normalizedSpeed);
        }

        // Keep the armature root upright; apply facing + lean only to Spine1.
        if (_skinRoot != null && horizontalVel.magnitude > _minSpeedForFacing)
        {
            Quaternion targetFacing = Quaternion.LookRotation(horizontalVel.normalized, Vector3.up);
            _skinRoot.rotation = Quaternion.Slerp(_skinRoot.rotation, targetFacing, _facingSpeed * Time.deltaTime);
        }
    }

    void UpdateSpineLean()
    {
        if (_spineBone == null || _inputHandler == null) return;

        Vector2 input = _inputHandler.CameraRelativeInput;

        float targetAngle = 0f;
        float clampedTargetAngle = 0f;
        if (input.sqrMagnitude > 0.01f)
        {
            Vector3 inputDir = new Vector3(input.x, 0f, input.y).normalized;
            Vector3 facingDir = _skinRoot.forward;
            facingDir.y = 0f;

            if (facingDir.sqrMagnitude > 0.001f)
            {
                facingDir.Normalize();
                targetAngle = Vector3.SignedAngle(facingDir, inputDir, Vector3.up);
                clampedTargetAngle = Mathf.Clamp(targetAngle, -_maxSpineDeltaAngle, _maxSpineDeltaAngle);
            }
        }


        bool isOpposite = input.sqrMagnitude > 0.01f && Mathf.Abs(targetAngle) > 180f - _backwardLeanThreshold;
        float targetSideAngle   = isOpposite ? 0f : clampedTargetAngle;
        float targetBackAngle   = isOpposite ? _backwardLeanAngle : 0f;
        float targetForwardLean = (!isOpposite && _inputHandler.SprintHeld) ? _forwardLeanAngle : 0f;

        _smoothedSpineAngle   = Mathf.Lerp(_smoothedSpineAngle,   targetSideAngle,   _spineLerpSpeed * Time.deltaTime);
        _smoothedBackwardLean = Mathf.Lerp(_smoothedBackwardLean, targetBackAngle,   _spineLerpSpeed * Time.deltaTime);
        _smoothedForwardLean  = Mathf.Lerp(_smoothedForwardLean,  targetForwardLean, _spineLerpSpeed * Time.deltaTime);

        // Overlay forward/backward lean (X) + twist (Y) + side lean (Z) on top of whatever the Animator set.
        // Backward lean suppresses forward lean via the targetForwardLean condition above.
        float twist = _smoothedSpineAngle * _spineTwistScale;
        float lean  = -_smoothedSpineAngle * _spineLeanScale;
        _spineBone.localRotation = Quaternion.Euler(_smoothedForwardLean - _smoothedBackwardLean, twist, lean);
    }

}
