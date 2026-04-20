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
        // Get the child named "MeatballVisual" and have the follow target be that
        Transform visualChild = transform.Find("MeatballVisual");
        if (visualChild != null)
        {
            // This will find the right visual target for following
            _chefInstance = Instantiate(_chefPrefab, transform.position, Quaternion.identity);
            _chefInstance.GetComponent<ChefAnimator>()?.Initialize(GetComponent<MeatballNetSync>(), GetComponent<MeatballClientInputHandler>(), visualChild);
        }
    }

    void OnDestroy()
    {
        if (_chefInstance != null)
            Destroy(_chefInstance);
    }
}
