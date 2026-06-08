using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Attach to the NetworkManager GameObject.
/// Enables connection approval and spawns each remote client that joins the
/// lobby at the host player's exact world location, offset along Y so the
/// joiner appears above the host. The host itself spawns at a configured base
/// position, since there is no host player to offset from when it connects.
/// </summary>
public class GameManager : MonoBehaviour, InputSystem_Actions.IPlayerActions
{
    [Tooltip("Offset added to the host's world position along Y so joining " +
             "clients spawn above the host player.")]
    [SerializeField] private float _spawnYOffset = 2f;

    [Tooltip("Position used for the very first player (the host itself), since " +
             "there is no existing host player to offset from yet.")]
    [SerializeField] private Vector3 _hostSpawnPosition = Vector3.zero;

    private InputSystem_Actions _actions;

    void Awake()
    {
        _actions = new InputSystem_Actions();
        _actions.Player.AddCallbacks(this);
    }

    void OnEnable()
    {
        _actions.Player.Enable();
    }

    void OnDisable()
    {
        _actions.Player.Disable();
    }

    void Start()
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

        // The host connects first and has no host player to offset from yet —
        // place it at the configured base spawn position.
        if (!TryGetHostPlayerTransform(out Vector3 hostPosition, out Quaternion hostRotation))
        {
            response.Position = _hostSpawnPosition;
            response.Rotation = Quaternion.identity;
            return;
        }

        // Remote client: spawn at the host player's exact world location, lifted
        // along Y so it appears above the host.
        response.Position = hostPosition + new Vector3(0f, _spawnYOffset, 0f);
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
        _actions.Dispose();
    }

    public void OnMenu(InputAction.CallbackContext context)
    {
        Application.Quit();
    }

    public void OnMove(InputAction.CallbackContext context) {}
    public void OnLook(InputAction.CallbackContext context) {}
    public void OnJump(InputAction.CallbackContext context) {}
    public void OnSprint(InputAction.CallbackContext context) {}
    public void OnToggleLooking(InputAction.CallbackContext context) {}
    public void OnReel(InputAction.CallbackContext context) {}
    public void OnEmote(InputAction.CallbackContext context) {}
}
