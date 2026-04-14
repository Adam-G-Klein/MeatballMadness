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

    [Header("Debug")]
    [Tooltip("Optional starting checkpoint active from scene load.")]
    [SerializeField] private MeatballCheckpointTrigger startingCheckpoint;

    private MeatballCheckpointTrigger activeCheckpoint;

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

    private void Update()
    {
        if (!IsClient)
            return;

        if (Keyboard.current == null)
            return;

        if (Keyboard.current[recallKey].wasPressedThisFrame)
        {
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
        if (!IsServer)
            return;

        if (checkpoint == null)
            return;

        activeCheckpoint = checkpoint;
        Debug.Log($"[MeatballCheckpointReturnManager] Active checkpoint set to: {checkpoint.name}");
    }

    [ContextMenu("Recall All Players")]
    public void RecallAllPlayers()
    {
        if (!IsServer)
        {
            Debug.LogWarning("[MeatballCheckpointReturnManager] RecallAllPlayers can only run on the server.");
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

        for (int i = 0; i < players.Count; i++)
        {
            Transform targetSpot = activeCheckpoint.GetSpotForIndex(i, reuseLastSpotIfNeeded);

            if (targetSpot == null)
                continue;

            MovePlayerToSpot(players[i], targetSpot);
        }
    }

    private List<MeatballPhysicsController> GetSortedPlayers()
    {
        MeatballPhysicsController[] found = FindObjectsByType<MeatballPhysicsController>(FindObjectsSortMode.None);
        List<MeatballPhysicsController> validPlayers = new List<MeatballPhysicsController>();

        for (int i = 0; i < found.Length; i++)
        {
            MeatballPhysicsController controller = found[i];

            if (controller == null)
                continue;

            if (controller.NetworkObject == null)
                continue;

            if (!controller.NetworkObject.IsSpawned)
                continue;

            validPlayers.Add(controller);
        }

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