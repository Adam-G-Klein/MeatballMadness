using Unity.Netcode;
using UnityEngine;

/// <summary>
/// A checkpoint trigger that becomes the active respawn checkpoint when touched by a player.
/// It can hide itself after activation and can also disable extra assigned objects.
/// </summary>
[DisallowMultipleComponent]
public class MeatballCheckpointTrigger : NetworkBehaviour
{
    [Header("Spawn Points")]
    [Tooltip("Player spawn points in OwnerClientId order. Slot 0 = lowest OwnerClientId.")]
    [SerializeField] private Transform[] playerSpawnPoints = new Transform[4];

    [Header("Activation")]
    [SerializeField] private bool triggerOnlyOnce = true;

    [Header("Hide On Trigger")]
    [SerializeField] private bool hideOnTrigger = true;
    [SerializeField] private GameObject visualRootToHide;
    [SerializeField] private bool disableAllCollidersOnTrigger = true;
    [SerializeField] private bool disableAllRenderersOnTrigger = true;

    [Header("Extra Objects To Disable")]
    [Tooltip("Any GameObjects assigned here will be set inactive when the checkpoint is triggered.")]
    [SerializeField] private GameObject[] extraObjectsToDisable;

    private bool hasBeenTriggered;

    public Transform[] PlayerSpawnPoints => playerSpawnPoints;

    private void Reset()
    {
        Collider col = GetComponent<Collider>();
        if (col != null)
            col.isTrigger = true;
    }

    private void OnTriggerEnter(Collider other)
    {
        if (!IsServer)
            return;

        if (triggerOnlyOnce && hasBeenTriggered)
            return;

        if (other == null)
            return;

        MeatballPhysicsController player = other.GetComponentInParent<MeatballPhysicsController>();
        if (player == null)
            return;

        if (MeatballCheckpointReturnManager.Instance == null)
        {
            Debug.LogWarning("[MeatballCheckpointTrigger] No MeatballCheckpointReturnManager found in scene.");
            return;
        }

        MeatballCheckpointReturnManager.Instance.SetActiveCheckpoint(this);
        hasBeenTriggered = true;

        if (hideOnTrigger)
        {
            HideCheckpointClientRpc();
        }
        else
        {
            DisableExtraObjectsClientRpc();
        }
    }

    public Transform GetSpotForIndex(int index, bool reuseLastSpotIfNeeded)
    {
        if (playerSpawnPoints == null || playerSpawnPoints.Length == 0)
        {
            Debug.LogWarning($"[MeatballCheckpointTrigger] {name} has no spawn points assigned.");
            return null;
        }

        if (index < playerSpawnPoints.Length)
            return playerSpawnPoints[index];

        if (reuseLastSpotIfNeeded && playerSpawnPoints.Length > 0)
            return playerSpawnPoints[playerSpawnPoints.Length - 1];

        Debug.LogWarning($"[MeatballCheckpointTrigger] No spawn point exists for player index {index} on checkpoint {name}.");
        return null;
    }

    [ClientRpc]
    private void HideCheckpointClientRpc()
    {
        GameObject hideTarget = visualRootToHide != null ? visualRootToHide : gameObject;

        if (disableAllRenderersOnTrigger)
        {
            Renderer[] renderers = hideTarget.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < renderers.Length; i++)
            {
                renderers[i].enabled = false;
            }
        }

        if (disableAllCollidersOnTrigger)
        {
            Collider[] colliders = GetComponentsInChildren<Collider>(true);
            for (int i = 0; i < colliders.Length; i++)
            {
                colliders[i].enabled = false;
            }
        }

        DisableExtraObjects();
    }

    [ClientRpc]
    private void DisableExtraObjectsClientRpc()
    {
        DisableExtraObjects();
    }

    private void DisableExtraObjects()
    {
        if (extraObjectsToDisable == null || extraObjectsToDisable.Length == 0)
            return;

        for (int i = 0; i < extraObjectsToDisable.Length; i++)
        {
            if (extraObjectsToDisable[i] == null)
                continue;

            extraObjectsToDisable[i].SetActive(false);
        }
    }
}