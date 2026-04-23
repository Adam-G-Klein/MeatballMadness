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

        // Follow the unparented MeatballVisual spawned by PredictedMeatball so the chef
        // rides the smoothed visual (which catches up after reconciles) rather than the
        // raw Rigidbody transform.
        PredictedMeatball predicted = GetComponent<PredictedMeatball>();
        Transform followTarget = predicted != null ? predicted.VisualTransform : null;
        if (followTarget == null)
        {
            Debug.LogWarning("[MeatballChefController] No PredictedMeatball.VisualTransform available — " +
                             "ensure PredictedMeatball has a visual prefab assigned.", this);
            return;
        }

        _chefInstance = Instantiate(_chefPrefab, transform.position, Quaternion.identity);
        _chefInstance.GetComponent<ChefAnimator>()?.Initialize(
            GetComponent<MeatballNetSync>(),
            GetComponent<MeatballClientInputHandler>(),
            followTarget);
    }

    void OnDestroy()
    {
        if (_chefInstance != null)
            Destroy(_chefInstance);
    }
}
