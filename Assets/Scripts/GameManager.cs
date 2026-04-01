using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Attach to the NetworkManager GameObject.
/// Enables connection approval and assigns each connecting player a spawn point
/// in order — index 0 for the host, index 1 for the joining client.
/// </summary>
public class GameManager : MonoBehaviour
{
    [SerializeField] private Transform[] _spawnPoints;

    private int _nextSpawnIndex;

    private void Awake()
    {
        NetworkManager.Singleton.NetworkConfig.ConnectionApproval = true;
        NetworkManager.Singleton.ConnectionApprovalCallback = ApprovalCheck;
    }

    private void ApprovalCheck(
        NetworkManager.ConnectionApprovalRequest request,
        NetworkManager.ConnectionApprovalResponse response)
    {
        response.Approved = true;
        response.CreatePlayerObject = true;

        if (_spawnPoints != null && _spawnPoints.Length > 0)
        {
            int index = _nextSpawnIndex % _spawnPoints.Length;
            response.Position = _spawnPoints[index].position;
            response.Rotation = _spawnPoints[index].rotation;
            _nextSpawnIndex++;
        }
    }

    private void OnDestroy()
    {
        if (NetworkManager.Singleton != null)
            NetworkManager.Singleton.ConnectionApprovalCallback = null;
    }
}
