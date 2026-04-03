using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Scene manager that recalls all spawned MeatballPhysicsController players
/// to designated checkpoint transforms.
///
/// Setup:
/// 1. Create an empty scene object and add:
///    - NetworkObject
///    - MeatballCheckpointReturnManager
/// 2. Create empty GameObjects in the scene for each player return spot
/// 3. Assign those transforms into checkpointSpots in the inspector
///
/// This does not modify MeatballPhysicsController.
/// It simply finds active spawned controllers and repositions them on the server.
/// </summary>
[DisallowMultipleComponent]
public class MeatballCheckpointReturnManager : NetworkBehaviour
{
    [Header("Input")]
    [Tooltip("Keyboard key that requests all players be returned to their checkpoint spots.")]
    [SerializeField] private Key recallKey = Key.R;

    [Header("Checkpoint Spots")]
    [Tooltip("Players are assigned to these spots in sorted OwnerClientId order.")]
    [SerializeField] private Transform[] checkpointSpots;

    [Header("Teleport Options")]
    [Tooltip("Also apply the checkpoint transform rotation.")]
    [SerializeField] private bool applyRotation = true;

    [Tooltip("Clear movement before placing players at the checkpoint.")]
    [SerializeField] private bool clearVelocity = true;

    [Tooltip("Temporarily make the rigidbody kinematic while repositioning.")]
    [SerializeField] private bool setKinematicDuringMove = true;

    [Tooltip("If there are more players than spots, reuse the last spot.")]
    [SerializeField] private bool reuseLastSpotIfNeeded = false;

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

    [ContextMenu("Recall All Players")]
    public void RecallAllPlayers()
    {
        if (!IsServer)
        {
            Debug.LogWarning("[MeatballCheckpointReturnManager] RecallAllPlayers can only run on the server.");
            return;
        }

        if (checkpointSpots == null || checkpointSpots.Length == 0)
        {
            Debug.LogWarning("[MeatballCheckpointReturnManager] No checkpoint spots assigned.");
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
            Transform targetSpot = GetSpotForIndex(i);

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

    private Transform GetSpotForIndex(int index)
    {
        if (index < checkpointSpots.Length)
            return checkpointSpots[index];

        if (reuseLastSpotIfNeeded && checkpointSpots.Length > 0)
            return checkpointSpots[checkpointSpots.Length - 1];

        Debug.LogWarning($"[MeatballCheckpointReturnManager] No checkpoint spot exists for player index {index}.");
        return null;
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
        if (checkpointSpots == null)
            return;

        for (int i = 0; i < checkpointSpots.Length; i++)
        {
            Transform spot = checkpointSpots[i];

            if (spot == null)
                continue;

            Gizmos.color = Color.green;
            Gizmos.DrawWireSphere(spot.position, 0.5f);
            Gizmos.DrawLine(spot.position, spot.position + spot.forward * 1.25f);
        }
    }
#endif
}