using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Tracks spawning PlayerMeatballs and fires a MarinaraExplosion when ALL currently-spawned
/// players are simultaneously within <see cref="triggerRadius"/> of this GameObject.
///
/// Works in both solo (MeatballSolo) and networked (MeatballPhysicsController) modes:
///   - Networked: listens to MeatballPhysicsController.OnMeatballSpawned/Despawned.
///   - Solo: falls back to tag-based lookup each frame (no static events fire in solo).
///
/// The trigger requires at least <see cref="requiredPlayerCount"/> players to be spawned
/// before proximity is tested — prevents firing before all players have connected.
/// </summary>
public class MarinaraExplosionTrigger : MonoBehaviour
{
    [Tooltip("Prefab to instantiate at this object's position when triggered.")]
    [SerializeField] private GameObject explosionPrefab;

    [Tooltip("All spawned players must be within this radius to trigger.")]
    [SerializeField] private float triggerRadius = 5f;

    [Tooltip("Minimum number of spawned players required before proximity is tested.")]
    [SerializeField] private int requiredPlayerCount = 2;

    [Tooltip("If true, the trigger fires once and then disables itself.")]
    [SerializeField] private bool oneShot = true;

    [Tooltip("HUDController to notify when the trigger fires.")]
    [SerializeField] private HUDController hudController;

    // Meatballs registered via the static NetworkSpawn events (server / host path).
    private readonly HashSet<MeatballPhysicsController> _networkMeatballs = new();

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    private void OnEnable()
    {
        MeatballPhysicsController.OnMeatballSpawned  += HandleMeatballSpawned;
        MeatballPhysicsController.OnMeatballDespawned += HandleMeatballDespawned;

        // Catch any meatballs that were already spawned before this object enabled.
        foreach (var mb in MeatballPhysicsController.ServerInstances)
            _networkMeatballs.Add(mb);
    }

    private void OnDisable()
    {
        MeatballPhysicsController.OnMeatballSpawned  -= HandleMeatballSpawned;
        MeatballPhysicsController.OnMeatballDespawned -= HandleMeatballDespawned;
    }

    private void Update()
    {
        List<Transform> meatballs = GetActiveMeatballs();

        if (meatballs.Count < requiredPlayerCount)
            return; // wait until enough players have spawned

        foreach (Transform mb in meatballs)
        {
            if (Vector3.Distance(transform.position, mb.position) > triggerRadius)
                return; // at least one player is still outside the radius
        }

        Fire();
    }

    // ── Event handlers ────────────────────────────────────────────────────────

    private void HandleMeatballSpawned(MeatballPhysicsController mb)   => _networkMeatballs.Add(mb);
    private void HandleMeatballDespawned(MeatballPhysicsController mb) => _networkMeatballs.Remove(mb);

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the transforms to test for proximity.
    /// Prefers the network-event-maintained set on host; falls back to tag lookup for solo.
    /// </summary>
    private List<Transform> GetActiveMeatballs()
    {
        // Networked (host): use the set maintained by spawn/despawn events.
        if (_networkMeatballs.Count > 0)
        {
            var result = new List<Transform>(_networkMeatballs.Count);
            foreach (var mb in _networkMeatballs)
            {
                if (mb != null) result.Add(mb.transform);
            }
            return result;
        }

        // Solo fallback: find all GameObjects tagged "Player" that have a MeatballSolo component.
        // FindGameObjectsWithTag is only called when no network meatballs are registered,
        // so the per-frame cost is limited to solo / sandbox scenes.
        var soloResult = new List<Transform>();
        foreach (var go in GameObject.FindGameObjectsWithTag("Player"))
        {
            if (go.GetComponent<MeatballSolo>() != null)
                soloResult.Add(go.transform);
        }
        return soloResult;
    }

    private void Fire()
    {
        if (explosionPrefab != null)
            MarinaraExplosion.SpawnAt(transform.position, explosionPrefab);

        if (hudController != null)
            hudController.ShowWinScreen();

        if (oneShot)
            enabled = false;
    }

    // ── Editor helpers ────────────────────────────────────────────────────────

    private void OnDrawGizmosSelected()
    {
        Gizmos.color = new Color(0.85f, 0.12f, 0.04f, 0.25f);
        Gizmos.DrawSphere(transform.position, triggerRadius);
        Gizmos.color = new Color(0.85f, 0.12f, 0.04f, 0.9f);
        Gizmos.DrawWireSphere(transform.position, triggerRadius);
    }
}
