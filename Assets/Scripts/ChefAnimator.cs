using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Lives on the AnimatedChef prefab. Holds a list of chef skin prefabs,
/// picks one that no other active ChefAnimator is using, and spawns it
/// as a child. Uses a static registry so sibling instances can avoid
/// claiming the same skin.
///
/// Owns facing/lean animation driven from NetworkedVelocity.
/// Call SetFollowTarget() after spawning to drive all of the above.
/// </summary>
public class ChefAnimator : MonoBehaviour
{
    [SerializeField] List<GameObject> _chefSkinPrefabs;
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
    [SerializeField] string _spine1BoneName = "Spine1";

    Transform _spine1Bone;
    Animator _animator;

    // Tracks which prefabs are claimed by live ChefAnimator instances.
    static readonly HashSet<GameObject> _claimedPrefabs = new();

    GameObject _claimedPrefab;
    Transform _followTarget;
    MeatballNetSync _netSync;

    // State for facing / lean computation.
    Quaternion _currentFacing = Quaternion.identity;
    Vector3 _currentLeanEuler;
    Vector3 _prevVelocity;
    bool _prevVelocityReady;

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

        _animator = skin.GetComponentInChildren<Animator>();
        if (_animator == null)
            Debug.LogWarning("[ChefAnimator] No Animator found on chef skin.", this);

        _spine1Bone = skin.transform;

    }

    /// <summary>Called by MeatballChefController after spawning this prefab.</summary>
    public void SetFollowTarget(Transform target)
    {
        _followTarget = target;
        _netSync = target.GetComponent<MeatballNetSync>();

        if (_netSync == null)
            Debug.LogWarning("[ChefAnimator] No MeatballNetSync on follow target — velocity-based lean will be zero.", this);
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
        _spine1Bone.position = _followTarget.position + Vector3.up * (_meatballRadius + _heightOffset);

        // Phase 2: rotate chef to face velocity; apply lean.
        UpdateFacingAndLean();
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

        Quaternion targetFacing = Quaternion.LookRotation(horizontalVel.normalized, Vector3.up);

        // Keep the armature root upright; apply facing + lean only to Spine1.
        if (_spine1Bone != null)
            _spine1Bone.rotation = targetFacing; //* Quaternion.Euler(_currentLeanEuler);
    }

    void OnDestroy()
    {
        if (_claimedPrefab != null)
            _claimedPrefabs.Remove(_claimedPrefab);
    }
}
