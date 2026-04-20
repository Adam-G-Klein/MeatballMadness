using System;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Broadcasts authoritative meatball physics state from the host to all clients on a
/// throttled schedule (targeting <see cref="_snapshotHz"/>). Each broadcast is a
/// tick-stamped <see cref="MeatballSnapshot"/> plus a sidecar of the collision impulses
/// the host applied since the previous snapshot.
///
/// HOST:   reads Rigidbody state; drains pending collision impulses from the controller;
///         sends one unreliable <see cref="ReceiveSnapshotClientRpc"/> per meatball per snapshot tick.
/// CLIENTS: set Rigidbody to kinematic (no local simulation), then drive position/rotation
///          via MovePosition/MoveRotation, interpolating toward the latest received snapshot.
///          Collision impulses are cached per snapshot for future use by reconciliation (step 8).
///
/// We do NOT use NetworkTransform — it only syncs the transform and fights AddForce.
/// We do NOT use NetworkRigidbody alone — it doesn't sync velocity or angular velocity.
/// </summary>
[RequireComponent(typeof(Rigidbody), typeof(NetworkObject))]
public class MeatballNetSync : NetworkBehaviour
{
    [Header("Client Interpolation")]
    [SerializeField] private float positionSmoothing = 12f;
    [SerializeField] private float rotationSmoothing = 12f;
    // Hard-snap to the authoritative position if further than this (e.g. after respawn).
    [SerializeField] private float snapDistance = 3f;

    [Header("Snapshot Broadcast (host)")]
    [Tooltip("Target snapshot broadcast rate, in Hz. 30 Hz is plenty because owners predict " +
             "locally (step 7) and non-owners interpolate. Host rounds to the nearest " +
             "FixedUpdate interval (50 Hz physics).")]
    [SerializeField] private float _snapshotHz = 30f;

    [Tooltip("Log a line on every broadcast (host) and every snapshot arrival (client).")]
    [SerializeField] private bool _logSnapshots;

    [Tooltip("Log a warning if a received snapshot tick goes backwards (reordered packet).")]
    [SerializeField] private bool _logStaleSnapshots = true;

    private Rigidbody _rb;
    private MeatballPhysicsController _controller;

    // Host-side broadcast pacing.
    private int _ticksSinceLastSnapshot;
    private int _ticksPerSnapshot = 2; // recomputed in Awake from _snapshotHz

    // Client-side snapshot cache.
    private bool _hasSnapshot;
    private MeatballSnapshot _latestSnapshot;

    // -------------------------------------------------------------------------
    // Skin index assignment
    // -------------------------------------------------------------------------

    static int _nextSkinIndex;

    public event Action<int> OnSkinIndexAssigned;
    public int SkinIndex { get; private set; } = -1;

    /// <summary>
    /// Latest collision impulses applied to this meatball by the host during the last
    /// broadcast tick. Currently unused on clients — wired up in rollout step 8.
    /// </summary>
    public CollisionImpulse[] LatestCollisionImpulses => _latestSnapshot.collisions ?? System.Array.Empty<CollisionImpulse>();

    /// <summary>
    /// Latest authoritative tick stamped on a received snapshot. 0 until the first
    /// snapshot arrives.
    /// </summary>
    public ulong LatestSnapshotTick => _hasSnapshot ? _latestSnapshot.tick : 0UL;

    /// <summary>
    /// Returns the meatball's velocity valid on both host (live Rigidbody) and clients
    /// (latest received snapshot). Use this instead of rb.linearVelocity on non-host code
    /// paths (e.g. tether damping reads, visual effects).
    /// </summary>
    public Vector3 NetworkedVelocity => IsServer ? _rb.linearVelocity : _latestSnapshot.velocity;

    private void Awake()
    {
        _rb = GetComponent<Rigidbody>();
        _controller = GetComponent<MeatballPhysicsController>();
        RecomputeSnapshotInterval();
    }

    private void OnValidate() => RecomputeSnapshotInterval();

    private void RecomputeSnapshotInterval()
    {
        if (_snapshotHz <= 0f) { _ticksPerSnapshot = 1; return; }
        float fixedHz = 1f / Mathf.Max(0.0001f, Time.fixedDeltaTime);
        _ticksPerSnapshot = Mathf.Max(1, Mathf.RoundToInt(fixedHz / _snapshotHz));
    }

    public override void OnNetworkSpawn()
    {
        if (IsServer)
        {
            AssignSkinIndexClientRpc(_nextSkinIndex++);
            _ticksSinceLastSnapshot = _ticksPerSnapshot; // broadcast immediately on first tick
            Debug.Log($"[MeatballNetSync] Host spawn (owner={OwnerClientId}) snapshotHz={_snapshotHz} ticksPerSnapshot={_ticksPerSnapshot}");
        }
        else
        {
            // Clients must not run their own physics simulation — kinematic means
            // the engine ignores forces and we drive the body entirely via MovePosition/MoveRotation.
            // (Rollout step 7 will branch on IsOwner && !IsServer so the owner predicts locally.)
            _rb.isKinematic = true;
            Debug.Log($"[MeatballNetSync] Client spawn (owner={OwnerClientId}, local={NetworkManager.LocalClientId}) — kinematic.");
        }
    }

    [ClientRpc]
    void AssignSkinIndexClientRpc(int index)
    {
        SkinIndex = index;
        OnSkinIndexAssigned?.Invoke(index);
    }

    private void FixedUpdate()
    {
        if (IsServer) HostBroadcast();
        else          ClientInterpolate();
    }

    // -------------------------------------------------------------------------
    // Host path
    // -------------------------------------------------------------------------

    private void HostBroadcast()
    {
        _ticksSinceLastSnapshot++;
        if (_ticksSinceLastSnapshot < _ticksPerSnapshot) return;
        _ticksSinceLastSnapshot = 0;

        ulong tick = NetworkTick.Instance != null ? NetworkTick.Instance.Current : 0;
        CollisionImpulse[] impulses = _controller != null
            ? _controller.DrainPendingCollisionImpulses()
            : System.Array.Empty<CollisionImpulse>();

        var snapshot = new MeatballSnapshot
        {
            tick = tick,
            position = _rb.position,
            rotation = _rb.rotation,
            velocity = _rb.linearVelocity,
            angularVelocity = _rb.angularVelocity,
            collisions = impulses,
        };

        ReceiveSnapshotClientRpc(snapshot);

        if (_logSnapshots)
            Debug.Log($"[MeatballNetSync] HOST broadcast tick={tick} pos={snapshot.position} " +
                      $"vel={snapshot.velocity} collisions={impulses.Length}");
    }

    [ClientRpc(Delivery = RpcDelivery.Unreliable)]
    private void ReceiveSnapshotClientRpc(MeatballSnapshot snapshot)
    {
        if (IsServer) return; // host is authoritative — no echo back to self

        if (_hasSnapshot && snapshot.tick < _latestSnapshot.tick)
        {
            if (_logStaleSnapshots)
                Debug.LogWarning($"[MeatballNetSync] CLIENT stale snapshot tick={snapshot.tick} " +
                                 $"< latest={_latestSnapshot.tick}; ignoring.");
            return;
        }

        _latestSnapshot = snapshot;
        _hasSnapshot = true;

        if (_logSnapshots)
            Debug.Log($"[MeatballNetSync] CLIENT received tick={snapshot.tick} pos={snapshot.position} " +
                      $"collisions={snapshot.collisions?.Length ?? 0}");
    }

    // -------------------------------------------------------------------------
    // Client path
    // -------------------------------------------------------------------------

    private void ClientInterpolate()
    {
        if (!_hasSnapshot) return;

        ReconcilePosition();
        ReconcileRotation();
    }

    private void ReconcilePosition()
    {
        Vector3 target = _latestSnapshot.position;
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
        Quaternion smoothed = Quaternion.Slerp(_rb.rotation, _latestSnapshot.rotation, rotationSmoothing * Time.fixedDeltaTime);
        _rb.MoveRotation(smoothed);
    }
}
