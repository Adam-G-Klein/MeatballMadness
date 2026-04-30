using UnityEngine;

/// <summary>
/// Spawns the PlayerNameDisplay prefab in worldspace and initializes it
/// with this meatball's owner clientId.
/// </summary>
public class PlayerNameDisplayController : MonoBehaviour
{
    [SerializeField] GameObject _nameDisplayPrefab;

    GameObject _displayInstance;

    void Start()
    {
        if (_nameDisplayPrefab == null)
        {
            Debug.LogWarning("[PlayerNameDisplayController] No name display prefab assigned.", this);
            return;
        }

        MeatballNetSync netSync = GetComponent<MeatballNetSync>();
        if (netSync == null)
        {
            Debug.LogWarning("[PlayerNameDisplayController] No MeatballNetSync found.", this);
            return;
        }

        _displayInstance = Instantiate(_nameDisplayPrefab, transform.position, Quaternion.identity);
        _displayInstance.GetComponent<PlayerNameDisplay>()?.Initialize(netSync.OwnerClientId, transform);
    }

    void OnDestroy()
    {
        if (_displayInstance != null)
            Destroy(_displayInstance);
    }
}
