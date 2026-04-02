using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Lives on the AnimatedChef prefab. Holds a list of chef skin prefabs,
/// picks one that no other active ChefAnimator is using, and spawns it
/// as a child. Uses a static registry so sibling instances can avoid
/// claiming the same skin.
/// </summary>
public class ChefAnimator : MonoBehaviour
{
    [SerializeField] List<GameObject> _chefSkinPrefabs;
    [SerializeField] float _chefScale = 2f;

    // Tracks which prefabs are claimed by live ChefAnimator instances.
    static readonly HashSet<GameObject> _claimedPrefabs = new();

    GameObject _claimedPrefab;

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
    }

    void OnDestroy()
    {
        if (_claimedPrefab != null)
            _claimedPrefabs.Remove(_claimedPrefab);
    }
}
