using Unity.Netcode;
using UnityEngine;

/// <summary>
/// General-purpose host-authoritative physics sync for any kinematic Rigidbody.
///
/// HOST:    Call MovePosition / MoveRotation instead of calling rb.MovePosition / rb.MoveRotation
///          directly. MMMovement forwards the call to the Rigidbody and broadcasts state each
///          FixedUpdate via NetworkVariables.
/// CLIENTS: MovePosition / MoveRotation are no-ops. MMMovement drives the Rigidbody toward the
///          latest received snapshot each FixedUpdate, interpolating smoothly or hard-snapping
///          when the gap exceeds snapDistance (e.g. after a teleport).
///
/// Usage: add this component alongside Rigidbody + NetworkObject on any physics-driven platform,
/// obstacle, or prop that needs to move the same way on all clients.
/// </summary>
[RequireComponent(typeof(Rigidbody), typeof(NetworkObject))]
public class MMMovement : NetworkBehaviour
{
    [Header("Client Interpolation")]
    [SerializeField] private float positionSmoothing = 12f;
    [SerializeField] private float rotationSmoothing = 12f;
    // Hard-snap to authoritative position if further than this (e.g. after a level reset).
    [SerializeField] private float snapDistance = 3f;

    private Rigidbody _rb;

    private readonly NetworkVariable<Vector3> _netPosition = new NetworkVariable<Vector3>(
        Vector3.zero, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    private readonly NetworkVariable<Quaternion> _netRotation = new NetworkVariable<Quaternion>(
        Quaternion.identity, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    private readonly NetworkVariable<Vector3> _netVelocity = new NetworkVariable<Vector3>(
        Vector3.zero, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    private readonly NetworkVariable<Vector3> _netAngularVelocity = new NetworkVariable<Vector3>(
        Vector3.zero, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    /// <summary>
    /// Velocity valid on both host (live Rigidbody) and clients (latest synced value).
    /// </summary>
    public Vector3 NetworkedVelocity => IsServer ? _rb.linearVelocity : _netVelocity.Value;

    private void Awake()
    {
        _rb = GetComponent<Rigidbody>();
        // Seed NetworkVariables with the object's actual world state so clients never
        // interpolate toward the Vector3.zero default before the first WriteState fires.
        _netPosition.Value = _rb.position;
        _netRotation.Value = _rb.rotation;
    }

    public override void OnNetworkSpawn()
    {
        if (!IsServer)
        {
            // Clients must not simulate — we drive the body entirely via MovePosition/MoveRotation.
            _rb.isKinematic = true;
        }
    }

    // -------------------------------------------------------------------------
    // Public API — call these instead of accessing the Rigidbody directly
    // -------------------------------------------------------------------------

    /// <summary>The Rigidbody's world position. On clients this reflects the interpolated position.</summary>
    public Vector3 Position => _rb.position;

    public bool IsKinematic { get => _rb.isKinematic; set => _rb.isKinematic = value; }
    public bool UseGravity  { get => _rb.useGravity;  set => _rb.useGravity  = value; }


    /// <summary>
    /// Moves the Rigidbody to <paramref name="position"/> this physics step.
    /// On clients this is a no-op; the client position is driven by the synced snapshot.
    /// </summary>
    public void MovePosition(Vector3 position)
    {
        if (IsServer)
            _rb.MovePosition(position);
    }

    /// <summary>
    /// Rotates the Rigidbody to <paramref name="rotation"/> this physics step.
    /// On clients this is a no-op; the client rotation is driven by the synced snapshot.
    /// </summary>
    public void MoveRotation(Quaternion rotation)
    {
        if (IsServer)
            _rb.MoveRotation(rotation);
    }

    // -------------------------------------------------------------------------
    // Sync loop
    // -------------------------------------------------------------------------

    private void FixedUpdate()
    {
        if (IsServer)
            WriteState();
        else
            ReadState();
    }

    private void WriteState()
    {
        _netPosition.Value        = _rb.position;
        _netRotation.Value        = _rb.rotation;
        _netVelocity.Value        = _rb.linearVelocity;
        _netAngularVelocity.Value = _rb.angularVelocity;
    }

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
