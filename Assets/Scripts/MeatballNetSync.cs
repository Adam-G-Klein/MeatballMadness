using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Broadcasts authoritative meatball physics state from the host to all clients each FixedUpdate.
///
/// HOST:   reads Rigidbody state and writes it into NetworkVariables.
/// CLIENTS: set Rigidbody to kinematic (no local simulation), then drive position/rotation
///          via MovePosition/MoveRotation, interpolating toward the latest received snapshot.
///          Hard-snaps if the gap exceeds snapDistance (e.g. after a respawn).
///
/// We do NOT use NetworkTransform — it only syncs the transform and fights AddForce.
/// We do NOT use NetworkRigidbody alone — it doesn't sync velocity or angular velocity.
/// Velocity is exposed via NetworkedVelocity so the tether and other scripts can read it
/// on both host and clients without querying a non-simulated Rigidbody.
/// </summary>
[RequireComponent(typeof(Rigidbody), typeof(NetworkObject))]
public class MeatballNetSync : NetworkBehaviour
{
    [Header("Client Interpolation")]
    [SerializeField] private float positionSmoothing = 12f;
    [SerializeField] private float rotationSmoothing = 12f;
    // Hard-snap to the authoritative position if further than this (e.g. after respawn).
    [SerializeField] private float snapDistance = 3f;

    private Rigidbody _rb;

    // NetworkVariables: host writes every FixedUpdate, all clients read.
    private readonly NetworkVariable<Vector3> _netPosition = new NetworkVariable<Vector3>(
        Vector3.zero, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    private readonly NetworkVariable<Quaternion> _netRotation = new NetworkVariable<Quaternion>(
        Quaternion.identity, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    private readonly NetworkVariable<Vector3> _netVelocity = new NetworkVariable<Vector3>(
        Vector3.zero, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    private readonly NetworkVariable<Vector3> _netAngularVelocity = new NetworkVariable<Vector3>(
        Vector3.zero, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    /// <summary>
    /// Returns the meatball's velocity valid on both host (live Rigidbody) and clients
    /// (latest synced value). Use this instead of rb.linearVelocity on non-host code paths
    /// (e.g. tether damping reads, visual effects).
    /// </summary>
    public Vector3 NetworkedVelocity => IsServer ? _rb.linearVelocity : _netVelocity.Value;

    private void Awake() => _rb = GetComponent<Rigidbody>();

    public override void OnNetworkSpawn()
    {
        if (!IsServer)
        {
            // Clients must not run their own physics simulation — kinematic means
            // the engine ignores forces and we drive the body entirely via MovePosition/MoveRotation.
            _rb.isKinematic = true;
        }
    }

    private void FixedUpdate()
    {
        if (IsServer)
            WriteState();
        else
            ReadState();
    }

    // -------------------------------------------------------------------------
    // Host path
    // -------------------------------------------------------------------------

    private void WriteState()
    {
        _netPosition.Value        = _rb.position;
        _netRotation.Value        = _rb.rotation;
        _netVelocity.Value        = _rb.linearVelocity;
        _netAngularVelocity.Value = _rb.angularVelocity;
    }

    // -------------------------------------------------------------------------
    // Client path
    // -------------------------------------------------------------------------

    private void ReadState()
    {
        ReconcilePosition();
        ReconcileRotation();
    }

    private void ReconcilePosition()
    {
        Vector3 target = _netPosition.Value;
        float dist = Vector3.Distance(_rb.position, target);

        if (dist > snapDistance)
        {
            _rb.MovePosition(target);
        }
        else
        {
            Vector3 smoothed = Vector3.Lerp(_rb.position, target, positionSmoothing * Time.fixedDeltaTime);
            _rb.MovePosition(smoothed);
        }
    }

    private void ReconcileRotation()
    {
        Quaternion smoothed = Quaternion.Slerp(_rb.rotation, _netRotation.Value, rotationSmoothing * Time.fixedDeltaTime);
        _rb.MoveRotation(smoothed);
    }
}
