using UnityEngine;

public class TimelineSpawner : MonoBehaviour
{
    [SerializeField] private GameObject _prefabA;
    [SerializeField] private GameObject _prefabB;
    [SerializeField] private Transform _spawnPointA;
    [SerializeField] private Transform _spawnPointB;

    public void SpawnPair()
    {
        SpawnOne(_prefabA, _spawnPointA);
        SpawnOne(_prefabB, _spawnPointB);
    }

    private void SpawnOne(GameObject prefab, Transform point)
    {
        if (prefab == null) return;

        var pos = point != null ? point.position : transform.position;
        var rot = point != null ? point.rotation : transform.rotation;
        Instantiate(prefab, pos, rot);
    }
}
