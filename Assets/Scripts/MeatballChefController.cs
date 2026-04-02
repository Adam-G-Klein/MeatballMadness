using UnityEngine;

/// <summary>
/// Spawns the AnimatedChef prefab in worldspace and keeps it directly above
/// the meatball each frame.
/// </summary>
public class MeatballChefController : MonoBehaviour
{
    [Header("Chef")]
    [SerializeField] GameObject _chefPrefab;

    [Tooltip("Height above the meatball's center.")]
    [SerializeField] float _heightOffset = 1.2f;

    // ── Internals ──────────────────────────────────────────────────────────────

    GameObject _chefInstance;

    void Start()
    {
        if (_chefPrefab == null)
        {
            Debug.LogWarning("[MeatballChefController] No chef prefab assigned.", this);
            return;
        }

        Vector3 spawnPos = transform.position + Vector3.up * _heightOffset;
        _chefInstance = Instantiate(_chefPrefab, spawnPos, Quaternion.identity);
    }

    void LateUpdate()
    {
        if (_chefInstance == null) return;

        _chefInstance.transform.position = transform.position + Vector3.up * _heightOffset;
    }

    void OnDestroy()
    {
        if (_chefInstance != null)
            Destroy(_chefInstance);
    }
}
