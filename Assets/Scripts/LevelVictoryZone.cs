using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// A level goal volume. Players must all gather inside the box-collider zone to win.
///
/// State machine (host-authoritative, synced to clients via a NetworkVariable):
///   • Red    — no players are inside the zone.
///   • Yellow — at least one player (but not all) is inside the zone.
///   • Green  — every spawned player is inside the zone. The host declares victory.
///
/// The host is the only machine that evaluates membership and writes the state. Clients
/// read the replicated <see cref="_state"/> NetworkVariable to keep their local visuals in
/// sync. The mesh renderer's colour is driven from the current state by setting the
/// "_ZoneColor" property on the material (see Assets/Shaders/VictoryZone.shader).
///
/// On victory the host fires <see cref="DeclareVictoryClientRpc"/>, which shows the shared
/// "Victory!" overlay (<see cref="VictoryScreenController"/>) on every machine.
///
/// Setup:
/// 1. Create an empty GameObject in the level scene.
/// 2. Add a NetworkObject, a BoxCollider (Is Trigger), a MeshRenderer + MeshFilter (e.g. a cube),
///    and this component.
/// 3. Assign the VictoryZone material to the MeshRenderer.
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(BoxCollider), typeof(MeshRenderer))]
public class LevelVictoryZone : NetworkBehaviour
{
    public enum VictoryState : byte
    {
        Red = 0,
        Yellow = 1,
        Green = 2,
    }

    [Header("State Colours")]
    [Tooltip("Colour shown when no players are inside the zone.")]
    [SerializeField] private Color _redColor = new Color(0.85f, 0.15f, 0.15f, 1f);

    [Tooltip("Colour shown when at least one (but not all) player is inside the zone.")]
    [SerializeField] private Color _yellowColor = new Color(0.95f, 0.85f, 0.15f, 1f);

    [Tooltip("Colour shown when every player is inside the zone (victory).")]
    [SerializeField] private Color _greenColor = new Color(0.2f, 0.85f, 0.25f, 1f);

    // Material property the shader reads its colour from.
    private static readonly int ZoneColorId = Shader.PropertyToID("_ZoneColor");

    // Replicated state — written by the server, read by everyone.
    private readonly NetworkVariable<VictoryState> _state =
        new(VictoryState.Red, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    // Server-only: meatballs currently overlapping the trigger volume.
    private readonly HashSet<MeatballPhysicsController> _playersInside = new();

    private Material _material;
    private bool _victoryDeclared;
    private bool _victoryShown;

    private void Awake()
    {
        // Instance the material so colour changes don't leak onto the shared asset.
        _material = GetComponent<MeshRenderer>().material;

        BoxCollider box = GetComponent<BoxCollider>();
        box.isTrigger = true;
    }

    private void Reset()
    {
        BoxCollider box = GetComponent<BoxCollider>();
        if (box != null)
            box.isTrigger = true;
    }

    public override void OnNetworkSpawn()
    {
        // Apply the current state immediately. Live updates arrive via UpdateZoneColorClientRpc,
        // but that only reaches clients already connected when the host broadcasts it — a late
        // joiner reads the replicated NetworkVariable here to catch up to the current colour.
        ApplyStateVisual(_state.Value);

        if (IsServer)
        {
            // The set of players can change after the zone spawns (late joiners, respawns,
            // disconnects). Re-evaluate whenever the roster changes so "all players inside"
            // stays correct.
            MeatballPhysicsController.OnMeatballSpawned += OnRosterChanged;
            MeatballPhysicsController.OnMeatballDespawned += OnRosterChanged;
            EvaluateState();
        }
    }

    public override void OnNetworkDespawn()
    {
        if (IsServer)
        {
            MeatballPhysicsController.OnMeatballSpawned -= OnRosterChanged;
            MeatballPhysicsController.OnMeatballDespawned -= OnRosterChanged;
        }
    }

    private void OnRosterChanged(MeatballPhysicsController _)
    {
        if (!IsServer)
            return;

        // A despawned player can never be "inside" — drop any stale references so the
        // membership count doesn't include destroyed meatballs.
        _playersInside.RemoveWhere(p => p == null);
        EvaluateState();
    }

    private void OnTriggerEnter(Collider other)
    {
        if (!IsServer)
            return;

        MeatballPhysicsController player = other.GetComponentInParent<MeatballPhysicsController>();
        if (player == null)
            return;

        if (_playersInside.Add(player))
            EvaluateState();
    }

    private void OnTriggerExit(Collider other)
    {
        if (!IsServer)
            return;

        MeatballPhysicsController player = other.GetComponentInParent<MeatballPhysicsController>();
        if (player == null)
            return;

        if (_playersInside.Remove(player))
            EvaluateState();
    }

    /// <summary>
    /// Server-only. Recomputes the zone state from the current membership and writes it to
    /// the NetworkVariable. Declares victory the moment every player is inside.
    /// </summary>
    private void EvaluateState()
    {
        if (!IsServer || _victoryDeclared)
            return;

        _playersInside.RemoveWhere(p => p == null);

        int totalPlayers = MeatballPhysicsController.ServerInstances.Count;
        int insideCount = _playersInside.Count;

        VictoryState next;
        if (insideCount == 0)
            next = VictoryState.Red;
        else if (totalPlayers > 0 && insideCount >= totalPlayers)
            next = VictoryState.Green;
        else
            next = VictoryState.Yellow;

        if (next == _state.Value)
            return;

        // Record the canonical state (replicated so late joiners can read it), then tell every
        // connected client to update its colour locally. The host receives this ClientRpc too,
        // so the host's own visual updates through the same path.
        _state.Value = next;
        UpdateZoneColorClientRpc(next);

        if (next == VictoryState.Green)
            DeclareVictory();
    }

    private void DeclareVictory()
    {
        if (_victoryDeclared)
            return;

        _victoryDeclared = true;
        Debug.Log("[LevelVictoryZone] All players in the zone — victory declared.");
        DeclareVictoryClientRpc();
    }

    [ClientRpc]
    private void UpdateZoneColorClientRpc(VictoryState state)
    {
        ApplyStateVisual(state);
    }

    private void ApplyStateVisual(VictoryState state)
    {
        if (_material == null)
            return;

        Color color = state switch
        {
            VictoryState.Green => _greenColor,
            VictoryState.Yellow => _yellowColor,
            _ => _redColor,
        };

        _material.SetColor(ZoneColorId, color);
    }

    [ClientRpc]
    private void DeclareVictoryClientRpc()
    {
        if (_victoryShown)
            return;

        _victoryShown = true;

        // Reveal the shared Victory! overlay on this machine. The screen's own button drives
        // the return to the main menu (see VictoryScreenController).
        if (VictoryScreenController.Instance != null)
            VictoryScreenController.Instance.Show();
        else
            Debug.LogWarning("[LevelVictoryZone] No VictoryScreenController in scene to show the victory screen.");
    }
}
