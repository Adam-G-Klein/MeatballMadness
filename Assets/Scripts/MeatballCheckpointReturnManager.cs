using System.Collections;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Scene manager that tracks the currently active checkpoint and recalls all spawned
/// MeatballPhysicsController players to that checkpoint's spawn transforms.
///
/// Setup:
/// 1. Create an empty scene object and add:
///    - NetworkObject
///    - MeatballCheckpointReturnManager
/// 2. Create checkpoint objects in the scene with MeatballCheckpointTrigger
/// 3. Each checkpoint holds its own player spawn points
/// 4. When a checkpoint is touched, it becomes the active checkpoint
/// 5. Press the recall key to move all players to the latest active checkpoint
/// </summary>
[DisallowMultipleComponent]
public class MeatballCheckpointReturnManager : NetworkBehaviour
{
    public static MeatballCheckpointReturnManager Instance { get; private set; }

    /// <summary>
    /// True when this machine should drive recall logic locally: either it's the NGO server,
    /// or a <see cref="MainMenuMeatballAnimationManager"/> singleton exists in the scene
    /// (the main-menu animatic runs without any NetworkManager, so IsServer is always false
    /// there even though the meatballs need to behave as if locally simulated).
    /// </summary>
    public bool ShouldSimulate => IsServer || MainMenuMeatballAnimationManager.Instance != null;

    /// <summary>
    /// True when this machine is acting as the main-menu animatic driver rather than a real
    /// networked host. Used to gate ServerRpc/ClientRpc paths that can't run without NGO.
    /// </summary>
    public bool IsMainMenu => MainMenuMeatballAnimationManager.Instance != null && !IsServer;

    [Header("Input")]
    [Tooltip("Keyboard key that requests all players be returned to the current checkpoint.")]
    [SerializeField] private Key recallKey = Key.R;

    [Header("Teleport Options")]
    [Tooltip("Also apply the checkpoint transform rotation.")]
    [SerializeField] private bool applyRotation = true;

    [Tooltip("Clear movement before placing players at the checkpoint.")]
    [SerializeField] private bool clearVelocity = true;

    [Tooltip("Temporarily make the rigidbody kinematic while repositioning.")]
    [SerializeField] private bool setKinematicDuringMove = true;

    [Tooltip("If there are more players than checkpoint spots, reuse the last spot.")]
    [SerializeField] private bool reuseLastSpotIfNeeded = false;

    [Tooltip("Safety fallback: each client must report back when it has processed the " +
             "post-teleport meatball snapshot before the spaghetti chains are rebuilt. If a " +
             "client never reports (packet loss, mid-recall disconnect) the chain stays " +
             "suppressed forever. After this many seconds the server forces the rebuild.")]
    [SerializeField] private float spaghettiRebuildAckTimeout = 2f;

    [Header("Debug")]
    [Tooltip("Optional starting checkpoint active from scene load.")]
    [SerializeField] private MeatballCheckpointTrigger startingCheckpoint;

    private MeatballCheckpointTrigger activeCheckpoint;

    // ── Recall ack tracking (server only) ──────────────────────────────────
    // The recall flow now waits for every remote client to acknowledge that it has processed
    // a meatball snapshot generated AFTER the host's teleport. Until every client acks, the
    // spaghetti chains stay suppressed so the rebuild always happens against post-teleport
    // anchor positions (not stale ones inherited from a delayed snapshot).
    private bool _recallInProgress;
    private ulong _recallEpoch;                       // bumps on every recall to invalidate stale acks
    private readonly HashSet<ulong> _pendingAcks = new();
    private Coroutine _ackTimeoutRoutine;

    // ── Client-side ack watcher (every machine, including host's client side) ──
    private ulong _localRecallEpoch;
    private Coroutine _ackWatcherRoutine;

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Debug.LogWarning("[MeatballCheckpointReturnManager] Duplicate manager found. Destroying duplicate.");
            Destroy(gameObject);
            return;
        }

        Instance = this;
    }

    public override void OnNetworkSpawn()
    {
        if (IsServer && startingCheckpoint != null)
        {
            activeCheckpoint = startingCheckpoint;
        }
    }

    private void Start()
    {
        // The main-menu animatic never spawns this as a NetworkObject, so OnNetworkSpawn
        // never runs. Seed the active checkpoint from the inspector default here.
        if (activeCheckpoint == null && startingCheckpoint != null && ShouldSimulate)
            activeCheckpoint = startingCheckpoint;
    }

    private void Update()
    {
        // In the main menu there is no IsClient (no NetworkManager). Still let the recall
        // key fire locally so designers can manually trigger respawns while iterating.
        if (!IsClient && !IsMainMenu)
            return;

        if (Keyboard.current == null)
            return;

        if (Keyboard.current[recallKey].wasPressedThisFrame)
        {
            if (IsMainMenu)
                RecallAllPlayers();
            else
                RequestRecallServerRpc();
        }
    }

    [ServerRpc(RequireOwnership = false)]
    private void RequestRecallServerRpc(ServerRpcParams rpcParams = default)
    {
        RecallAllPlayers();
    }

    /// <summary>
    /// Called by a checkpoint trigger when a player activates it.
    /// Server only.
    /// </summary>
    public void SetActiveCheckpoint(MeatballCheckpointTrigger checkpoint)
    {
        if (!ShouldSimulate)
            return;

        if (checkpoint == null)
            return;

        activeCheckpoint = checkpoint;
        Debug.Log($"[MeatballCheckpointReturnManager] Active checkpoint set to: {checkpoint.name}");
    }

    [ContextMenu("Recall All Players")]
    public void RecallAllPlayers()
    {
        if (!ShouldSimulate)
        {
            Debug.LogWarning("[MeatballCheckpointReturnManager] RecallAllPlayers can only run on the server (or in main-menu animatic mode).");
            return;
        }

        if (activeCheckpoint == null)
        {
            Debug.LogWarning("[MeatballCheckpointReturnManager] No active checkpoint has been set.");
            return;
        }

        List<MeatballPhysicsController> players = GetSortedPlayers();

        if (players.Count == 0)
        {
            Debug.LogWarning("[MeatballCheckpointReturnManager] No spawned MeatballPhysicsController players found.");
            return;
        }

        if (IsMainMenu)
        {
            // No NGO, no ClientRpc, no acks. Just rebuild local spaghetti chains around the
            // teleport so the visual doesn't whip from the old anchor positions.
            for (int i = 0; i < SpaghettiRenderer.Instances.Count; i++)
                SpaghettiRenderer.Instances[i]?.BeginChainRebuild();

            for (int i = 0; i < players.Count; i++)
            {
                Transform targetSpot = activeCheckpoint.GetSpotForIndex(i, reuseLastSpotIfNeeded);
                if (targetSpot == null) continue;
                MovePlayerToSpot(players[i], targetSpot);
            }

            for (int i = 0; i < SpaghettiRenderer.Instances.Count; i++)
                SpaghettiRenderer.Instances[i]?.EndChainRebuild();

            return;
        }

        // Capture the post-teleport snapshot threshold BEFORE moving. The next host broadcast
        // (tick > currentTick) is the first one that reflects the new positions, so clients ack
        // on receiving any snapshot whose tick is at or beyond `requiredTick`. Bumping the epoch
        // up front invalidates any in-flight ack/timeout from a prior recall.
        ulong currentTick = NetworkTick.Instance != null ? NetworkTick.Instance.Current : 0;
        ulong requiredTick = currentTick + 1;
        _recallEpoch++;

        _pendingAcks.Clear();
        var connected = NetworkManager.Singleton.ConnectedClientsIds;
        for (int c = 0; c < connected.Count; c++)
        {
            ulong cid = connected[c];
            // Host doesn't ack — its meatballs are at new positions instantly via the teleport
            // below, and the End broadcast that releases the chain runs on host like any client.
            if (cid == NetworkManager.ServerClientId) continue;
            _pendingAcks.Add(cid);
        }
        _recallInProgress = true;

        // Trash every spaghetti chain on every machine BEFORE moving. Without this, interior
        // Verlet nodes carry over from the pre-teleport location and whip violently when the
        // constraint solver drags them toward the new anchors.
        BeginSpaghettiRebuildClientRpc(_recallEpoch, requiredTick);

        for (int i = 0; i < players.Count; i++)
        {
            Transform targetSpot = activeCheckpoint.GetSpotForIndex(i, reuseLastSpotIfNeeded);

            if (targetSpot == null)
                continue;

            MovePlayerToSpot(players[i], targetSpot);
        }

        if (_pendingAcks.Count == 0)
        {
            // Solo / host-only — no remote clients to wait on. Release immediately.
            FinishSpaghettiRebuild(_recallEpoch);
        }
        else
        {
            if (_ackTimeoutRoutine != null) StopCoroutine(_ackTimeoutRoutine);
            _ackTimeoutRoutine = StartCoroutine(AckTimeoutCoroutine(_recallEpoch));
        }
    }

    private IEnumerator AckTimeoutCoroutine(ulong epoch)
    {
        yield return new WaitForSeconds(spaghettiRebuildAckTimeout);
        if (epoch != _recallEpoch || !_recallInProgress) yield break;

        Debug.LogWarning($"[MeatballCheckpointReturnManager] Spaghetti rebuild ack timeout — " +
                         $"{_pendingAcks.Count} client(s) did not report. Forcing rebuild.");
        FinishSpaghettiRebuild(epoch);
    }

    private void FinishSpaghettiRebuild(ulong epoch)
    {
        if (epoch != _recallEpoch) return;
        if (!_recallInProgress) return;

        _recallInProgress = false;
        _pendingAcks.Clear();

        if (_ackTimeoutRoutine != null)
        {
            StopCoroutine(_ackTimeoutRoutine);
            _ackTimeoutRoutine = null;
        }

        EndSpaghettiRebuildClientRpc();
    }

    [ServerRpc(RequireOwnership = false)]
    private void RecallAcknowledgedServerRpc(ulong epoch, ServerRpcParams rpcParams = default)
    {
        // Stale acks from a prior recall — or a recall that's already concluded — are dropped.
        if (!_recallInProgress) return;
        if (epoch != _recallEpoch) return;

        ulong sender = rpcParams.Receive.SenderClientId;
        if (!_pendingAcks.Remove(sender)) return;

        if (_pendingAcks.Count == 0)
            FinishSpaghettiRebuild(epoch);
    }

    [ClientRpc]
    private void BeginSpaghettiRebuildClientRpc(ulong epoch, ulong requiredTick)
    {
        for (int i = 0; i < SpaghettiRenderer.Instances.Count; i++)
            SpaghettiRenderer.Instances[i]?.BeginChainRebuild();

        // Host doesn't run the ack watcher: the server already excluded itself from the
        // pending-ack set and its meatballs are at the new positions immediately.
        if (IsServer) return;

        _localRecallEpoch = epoch;
        if (_ackWatcherRoutine != null) StopCoroutine(_ackWatcherRoutine);
        _ackWatcherRoutine = StartCoroutine(AckWatcherCoroutine(epoch, requiredTick));
    }

    private IEnumerator AckWatcherCoroutine(ulong epoch, ulong requiredTick)
    {
        // Poll once per frame: every meatball's MeatballNetSync must have processed a snapshot
        // whose tick is at or beyond `requiredTick` before this client confirms it has the
        // new state. Snapshot ticks are stamped in the server's tick domain (see
        // MeatballNetSync.HostBroadcast) so comparison against the server-supplied requiredTick
        // is well-defined.
        while (true)
        {
            // A newer recall has superseded us — let its watcher take over.
            if (epoch != _localRecallEpoch)
            {
                _ackWatcherRoutine = null;
                yield break;
            }

            MeatballNetSync[] syncs = FindObjectsByType<MeatballNetSync>(FindObjectsSortMode.None);
            bool allReady = syncs.Length > 0;
            for (int i = 0; i < syncs.Length; i++)
            {
                if (syncs[i] == null) continue;
                if (syncs[i].LatestSnapshotTick < requiredTick) { allReady = false; break; }
            }

            if (allReady)
            {
                RecallAcknowledgedServerRpc(epoch);
                _ackWatcherRoutine = null;
                yield break;
            }

            yield return null;
        }
    }

    [ClientRpc]
    private void EndSpaghettiRebuildClientRpc()
    {
        for (int i = 0; i < SpaghettiRenderer.Instances.Count; i++)
            SpaghettiRenderer.Instances[i]?.EndChainRebuild();

        if (_ackWatcherRoutine != null)
        {
            StopCoroutine(_ackWatcherRoutine);
            _ackWatcherRoutine = null;
        }
    }

    private List<MeatballPhysicsController> GetSortedPlayers()
    {
        MeatballPhysicsController[] found = FindObjectsByType<MeatballPhysicsController>(FindObjectsSortMode.None);
        List<MeatballPhysicsController> validPlayers = new List<MeatballPhysicsController>();
        bool requireSpawned = !IsMainMenu;

        for (int i = 0; i < found.Length; i++)
        {
            MeatballPhysicsController controller = found[i];

            if (controller == null)
                continue;

            if (requireSpawned)
            {
                // Networked play: only consider meatballs that have completed NGO spawn so
                // OwnerClientId / network state are valid.
                if (controller.NetworkObject == null)
                    continue;

                if (!controller.NetworkObject.IsSpawned)
                    continue;
            }

            validPlayers.Add(controller);
        }

        // Stable order across recalls. In the main menu the NetworkObject isn't spawned so
        // OwnerClientId is the default 0 on every meatball — fall back to sibling index so
        // the spawn-spot mapping is deterministic and inspector-controllable.
        if (IsMainMenu)
            validPlayers.Sort((a, b) => a.transform.GetSiblingIndex().CompareTo(b.transform.GetSiblingIndex()));
        else
            validPlayers.Sort((a, b) => a.OwnerClientId.CompareTo(b.OwnerClientId));
        return validPlayers;
    }

    private void MovePlayerToSpot(MeatballPhysicsController player, Transform spot)
    {
        if (player == null || spot == null)
            return;

        Rigidbody rb = player.Rigidbody;

        if (rb == null)
        {
            Debug.LogWarning($"[MeatballCheckpointReturnManager] Player {player.name} has no Rigidbody.");
            return;
        }

        bool oldKinematic = rb.isKinematic;

        if (setKinematicDuringMove)
            rb.isKinematic = true;

        if (clearVelocity)
        {
            rb.linearVelocity = Vector3.zero;
            rb.angularVelocity = Vector3.zero;
        }

        rb.position = spot.position;

        if (applyRotation)
            rb.rotation = spot.rotation;

        if (setKinematicDuringMove)
            rb.isKinematic = oldKinematic;
    }

#if UNITY_EDITOR
    private void OnDrawGizmos()
    {
        if (activeCheckpoint == null)
            return;

        Transform[] spots = activeCheckpoint.PlayerSpawnPoints;
        if (spots == null)
            return;

        for (int i = 0; i < spots.Length; i++)
        {
            Transform spot = spots[i];

            if (spot == null)
                continue;

            Gizmos.color = Color.green;
            Gizmos.DrawWireSphere(spot.position, 0.5f);
            Gizmos.DrawLine(spot.position, spot.position + spot.forward * 1.25f);
        }
    }
#endif
}