using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Attach to the NetworkManager GameObject.
///
/// Spawns every remote client that joins the lobby at the host player's exact
/// world location, offset along Z so the joiner appears above the host. This
/// keeps newly joined meatballs grouped with the host instead of dropping into
/// fixed spawn points.
///
/// Runs host-authoritative: the connection approval callback only executes on
/// the server (host), which is also where the host player's transform is
/// canonical, so the read of the host position is always the source of truth.
/// </summary>
public class PlayerSpawnOffset : MonoBehaviour
{
    [Tooltip("Offset added to the host's world position along Z so joining " +
             "clients spawn above the host player.")]
    [SerializeField] private float _spawnZOffset = 2f;

    [Tooltip("Position used for the very first player (the host itself), since " +
             "there is no existing host player to offset from yet.")]
    [SerializeField] private Vector3 _hostSpawnPosition = Vector3.zero;

    void Start()
    {
        var nm = NetworkManager.Singleton;
        if (nm == null)
        {
            Debug.LogError("PlayerSpawnOffset: no NetworkManager.Singleton found.");
            return;
        }

        nm.NetworkConfig.ConnectionApproval = true;
        nm.ConnectionApprovalCallback = ApprovalCheck;
    }

    private void ApprovalCheck(
        NetworkManager.ConnectionApprovalRequest request,
        NetworkManager.ConnectionApprovalResponse response)
    {
        response.Approved = true;
        response.CreatePlayerObject = true;

        // The host connects first and has no host player to offset from yet —
        // place it at the configured base spawn position.
        if (!TryGetHostPlayerTransform(out Vector3 hostPosition, out Quaternion hostRotation))
        {
            response.Position = _hostSpawnPosition;
            response.Rotation = Quaternion.identity;
            return;
        }

        // Remote client: spawn at the host player's exact world location, lifted
        // along Z so it appears above the host.
        response.Position = hostPosition + new Vector3(0f, 0f, _spawnZOffset);
        response.Rotation = hostRotation;
    }

    /// <summary>
    /// Reads the host player object's world position/rotation. Returns false when
    /// the host player has not spawned yet (e.g. the host's own connection).
    /// </summary>
    private bool TryGetHostPlayerTransform(out Vector3 position, out Quaternion rotation)
    {
        position = Vector3.zero;
        rotation = Quaternion.identity;

        var nm = NetworkManager.Singleton;
        if (nm == null)
            return false;

        if (!nm.ConnectedClients.TryGetValue(NetworkManager.ServerClientId, out var hostClient))
            return false;

        if (hostClient.PlayerObject == null)
            return false;

        var hostTransform = hostClient.PlayerObject.transform;
        position = hostTransform.position;
        rotation = hostTransform.rotation;
        return true;
    }

    private void OnDestroy()
    {
        if (NetworkManager.Singleton != null)
            NetworkManager.Singleton.ConnectionApprovalCallback = null;
    }
}
