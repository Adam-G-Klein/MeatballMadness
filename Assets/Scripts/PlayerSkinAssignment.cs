using System.Collections;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Assigns and tracks chef skin indices per connected player.
///
/// HOST: self-assigns index 0 when Initialize() is called.
/// CLIENT: sends a ServerRpc to request the next available index. The host
///         responds with the full current assignment map so late joiners
///         catch up on all skins assigned before they connected.
///
/// Spawn once per session, inside the gameplay scene. Dies with the scene
/// on quit-to-menu so a re-host doesn't accumulate stale duplicates in
/// DontDestroyOnLoad (which produced ScenePlacedObjects hash collisions).
/// </summary>
public class PlayerSkinAssignment : NetworkBehaviour
{
    public static PlayerSkinAssignment Instance { get; private set; }

    private readonly Dictionary<ulong, int> _skinMap = new();
    private int _nextIndex;
    private bool _initialized;

    void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }

        Instance = this;
    }

    public override void OnNetworkSpawn()
    {
        _skinMap.Clear();
        _nextIndex = 0;
        _initialized = false;
    }

    public override void OnNetworkDespawn()
    {
        if (Instance == this)
            Instance = null;
    }

    void OnDestroy()
    {
        if (Instance == this)
            Instance = null;
    }

    /// <summary>
    /// Yields until this client's skin index is confirmed by the host.
    /// Must complete before entering the gameplay scene.
    /// </summary>
    public IEnumerator Initialize()
    {
        if (_initialized) yield break;

        if (IsServer)
        {
            _skinMap[NetworkManager.LocalClientId] = _nextIndex++;
            _initialized = true;
            yield break;
        }

        RequestSkinIndexServerRpc();
        yield return new WaitUntil(() => _initialized);
    }

    /// <summary>Returns the skin index assigned to clientId, or 0 if unknown.</summary>
    public int GetSkinIndex(ulong clientId) =>
        _skinMap.TryGetValue(clientId, out int idx) ? idx : 0;

    public bool HasSkinIndex(ulong clientId) => _skinMap.ContainsKey(clientId);

    // -------------------------------------------------------------------------
    // Network messages
    // -------------------------------------------------------------------------

    [ServerRpc(RequireOwnership = false)]
    private void RequestSkinIndexServerRpc(ServerRpcParams rpcParams = default)
    {
        ulong requesterId = rpcParams.Receive.SenderClientId;
        _skinMap[requesterId] = _nextIndex++;

        // Build a snapshot of the full map so the requester catches up on all
        // assignments made before they connected (host self-assign, prior clients).
        int count = _skinMap.Count;
        ulong[] clientIds = new ulong[count];
        int[] indices = new int[count];
        int i = 0;
        foreach (var kvp in _skinMap)
        {
            clientIds[i] = kvp.Key;
            indices[i] = kvp.Value;
            i++;
        }

        // Full map → requesting client only.
        ReceiveFullSkinMapClientRpc(clientIds, indices,
            new ClientRpcParams
            {
                Send = new ClientRpcSendParams { TargetClientIds = new[] { requesterId } }
            });

        // New entry only → all other non-host clients (they already have the rest).
        ulong[] others = GetAllClientsExcept(requesterId);
        if (others.Length > 0)
            ReceiveNewAssignmentClientRpc(requesterId, _skinMap[requesterId],
                new ClientRpcParams
                {
                    Send = new ClientRpcSendParams { TargetClientIds = others }
                });
    }

    [ClientRpc]
    private void ReceiveFullSkinMapClientRpc(ulong[] clientIds, int[] indices,
        ClientRpcParams clientRpcParams = default)
    {
        for (int i = 0; i < clientIds.Length; i++)
            _skinMap[clientIds[i]] = indices[i];

        _initialized = true;
    }

    [ClientRpc]
    private void ReceiveNewAssignmentClientRpc(ulong clientId, int skinIndex,
        ClientRpcParams clientRpcParams = default)
    {
        _skinMap[clientId] = skinIndex;
    }

    private ulong[] GetAllClientsExcept(ulong excludeId)
    {
        var result = new List<ulong>();
        foreach (ulong id in NetworkManager.ConnectedClientsIds)
        {
            if (id != excludeId && id != NetworkManager.ServerClientId)
                result.Add(id);
        }
        return result.ToArray();
    }
}
