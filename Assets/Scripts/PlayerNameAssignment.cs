using System.Collections;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Assigns and tracks a randomly chosen nickname per connected player.
///
/// HOST: self-assigns a name when Initialize() is called.
/// CLIENT: sends a ServerRpc to request a name. The host responds with the
///         full current assignment map so late joiners catch up on all names
///         assigned before they connected.
///
/// Spawn once per session, inside the gameplay scene. Dies with the scene
/// on quit-to-menu so a re-host doesn't accumulate stale duplicates.
/// </summary>
public class PlayerNameAssignment : NetworkBehaviour
{
    public static PlayerNameAssignment Instance { get; private set; }

    private static readonly string[] NamePool =
    {
        "\"Tony\" Rigatoni",
        "\"Vinny\" Linguini",
        "\"Larry\" Lasagna",
        "\"Ricky\" Ravioli",
        "\"Mikey\" Meatball",
    };

    private readonly Dictionary<ulong, string> _nameMap = new();
    private readonly List<string> _availableNames = new();
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
        _nameMap.Clear();
        _initialized = false;

        if (IsServer)
        {
            _availableNames.Clear();
            _availableNames.AddRange(NamePool);
        }
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
    /// Yields until this client's name is confirmed by the host.
    /// Must complete before entering the gameplay scene.
    /// </summary>
    public IEnumerator Initialize()
    {
        if (_initialized) yield break;

        if (IsServer)
        {
            AssignRandomName(NetworkManager.LocalClientId);
            _initialized = true;
            LogAssignments();
            yield break;
        }

        RequestNameServerRpc();
        yield return new WaitUntil(() => _initialized);
    }

    /// <summary>Returns the name assigned to clientId, or an empty string if unknown.</summary>
    public string GetName(ulong clientId) =>
        _nameMap.TryGetValue(clientId, out string name) ? name : string.Empty;

    public bool HasName(ulong clientId) => _nameMap.ContainsKey(clientId);

    // -------------------------------------------------------------------------
    // Internal helpers
    // -------------------------------------------------------------------------

    private void AssignRandomName(ulong clientId)
    {
        if (_availableNames.Count == 0)
        {
            Debug.LogWarning("[PlayerNameAssignment] Name pool exhausted; reusing full pool.");
            _availableNames.AddRange(NamePool);
        }

        int idx = Random.Range(0, _availableNames.Count);
        string chosen = _availableNames[idx];
        _availableNames.RemoveAt(idx);
        _nameMap[clientId] = chosen;
    }

    private void LogAssignments()
    {
        ulong localId = NetworkManager.LocalClientId;
        Debug.Log($"[PlayerNameAssignment] Local — clientId={localId}, name={GetName(localId)}");

        foreach (var kvp in _nameMap)
        {
            if (kvp.Key != localId)
                Debug.Log($"[PlayerNameAssignment] Other — clientId={kvp.Key}, name={kvp.Value}");
        }
    }

    // -------------------------------------------------------------------------
    // Network messages
    // -------------------------------------------------------------------------

    [ServerRpc(RequireOwnership = false)]
    private void RequestNameServerRpc(ServerRpcParams rpcParams = default)
    {
        ulong requesterId = rpcParams.Receive.SenderClientId;
        AssignRandomName(requesterId);

        // Build a snapshot of the full map so the requester catches up on all
        // assignments made before they connected (host self-assign, prior clients).
        // NGO can't serialize string[] directly, so names are packed into a
        // pipe-delimited string and unpacked on the receiving end.
        int count = _nameMap.Count;
        ulong[] clientIds = new ulong[count];
        var nameList = new System.Text.StringBuilder();
        int i = 0;
        foreach (var kvp in _nameMap)
        {
            clientIds[i] = kvp.Key;
            if (i > 0) nameList.Append('|');
            nameList.Append(kvp.Value);
            i++;
        }

        // Full map → requesting client only.
        ReceiveFullNameMapClientRpc(clientIds, nameList.ToString(),
            new ClientRpcParams
            {
                Send = new ClientRpcSendParams { TargetClientIds = new[] { requesterId } }
            });

        // New entry only → all other non-host clients (they already have the rest).
        ulong[] others = GetAllClientsExcept(requesterId);
        if (others.Length > 0)
            ReceiveNewAssignmentClientRpc(requesterId, _nameMap[requesterId],
                new ClientRpcParams
                {
                    Send = new ClientRpcSendParams { TargetClientIds = others }
                });
    }

    [ClientRpc]
    private void ReceiveFullNameMapClientRpc(ulong[] clientIds, string packedNames,
        ClientRpcParams clientRpcParams = default)
    {
        string[] names = packedNames.Split('|');
        for (int i = 0; i < clientIds.Length; i++)
            _nameMap[clientIds[i]] = names[i];

        _initialized = true;
        LogAssignments();
    }

    [ClientRpc]
    private void ReceiveNewAssignmentClientRpc(ulong clientId, string name,
        ClientRpcParams clientRpcParams = default)
    {
        _nameMap[clientId] = name;
        Debug.Log($"[PlayerNameAssignment] New player joined — clientId={clientId}, name={name}");
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
