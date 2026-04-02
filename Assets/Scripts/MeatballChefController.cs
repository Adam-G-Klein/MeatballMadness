using UnityEngine;

/// <summary>
/// Spawns the AnimatedChef prefab in worldspace. Position tracking is handled
/// by the ChefAnimator component on the spawned prefab.
/// </summary>
public class MeatballChefController : MonoBehaviour
{
    [Header("Chef")]
    [SerializeField] GameObject _chefPrefab;

    GameObject _chefInstance;

    void Start()
    {
        if (_chefPrefab == null)
        {
            Debug.LogWarning("[MeatballChefController] No chef prefab assigned.", this);
            return;
        }

        _chefInstance = Instantiate(_chefPrefab, transform.position, Quaternion.identity);
        _chefInstance.GetComponent<ChefAnimator>()?.SetFollowTarget(transform);
    }

    void OnDestroy()
    {
        if (_chefInstance != null)
            Destroy(_chefInstance);
    }
}
