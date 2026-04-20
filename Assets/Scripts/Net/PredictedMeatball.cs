using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Owner-side client prediction + reconciliation for a single meatball.
///
/// Runs ONLY when <c>IsOwner &amp;&amp; !IsServer</c>. The host's own meatball is driven by
/// <see cref="MeatballPhysicsController"/>. Non-owner meatballs on a client are kinematic
/// and interpolated by <see cref="MeatballNetSync"/>.
///
/// Each FixedUpdate:
///   1. If an authoritative snapshot arrived since last tick, rollback to its tick,
///      replay broadcast collision impulses + locally-buffered inputs up to currentTick-1,
///      and record a visual offset equal to the pre-rollback Rigidbody position minus the
///      post-replay position so the mesh stays put for a few frames then eases back.
///   2. Apply the current tick's input via <see cref="MeatballMotor.ApplyTick"/>.
///   3. Manually step physics with <see cref="Physics.Simulate"/> so the rollback replay
///      path uses the exact same integration as the live tick (no auto-sim races).
///   4. Record the resulting Rigidbody state into a tick-keyed ring buffer so a future
///      snapshot can compare prediction vs. authority.
///
/// In LateUpdate the visual offset decays to zero on a <c>MeatballVisual</c> child, which
/// holds the mesh. Camera, tether, ground check, and collider all live on the root and
/// read the authoritative Rigidbody transform.
///
/// Rollout step 7 + step 8 from talk-me-through-what-rosy-pudding.md. Steps are fused
/// because prediction without reconciliation would drift immediately.
/// </summary>
[DefaultExecutionOrder(50)] // after MeatballClientInputHandler (-100), same frame's input is ready
[RequireComponent(typeof(Rigidbody))]
[RequireComponent(typeof(NetworkObject))]
[RequireComponent(typeof(MeatballPhysicsController))]
[RequireComponent(typeof(MeatballClientInputHandler))]
             
public class PredictedMeatball : NetworkBehaviour
{
    [Header("Prediction")]
    [SerializeField, Tooltip("Ring buffer size for stored predicted states. Needs to cover at least " +
                             "~1 second of 50 Hz ticks so a late snapshot can still find a matching prediction.")]
    private int _historySize = 64;

    [Header("Reconcile")]
    [SerializeField, Tooltip("Position error (m) above this triggers a hard snap with no input replay " +
                             "and clears the visual offset. Matches the existing MeatballNetSync.snapDistance.")]
    private float _hardSnapDistance = 3f;

    [SerializeField, Tooltip("Position errors below this are ignored — the reconcile still snaps the " +
                             "Rigidbody to authoritative but skips the visual-offset smoothing. Keeps " +
                             "the mesh steady when prediction is nearly perfect.")]
    private float _smoothErrorThreshold = 0.02f;

    [Header("Visual smoothing (mesh child)")]
    [SerializeField, Tooltip("Name of the child transform that holds the mesh. Receives a local-position " +
                             "offset that decays to zero each LateUpdate; the root + collider + Rigidbody " +
                             "stay at the authoritative position.")]
    private string _visualChildName = "MeatballVisual";

    [SerializeField, Tooltip("Higher = faster mesh catch-up (per-second exponential decay rate). 15-25 feels " +
                             "good at 60 fps; widen this for collision-replay frames (handled automatically).")]
    private float _visualDecayRate = 20f;

    [SerializeField, Tooltip("When a reconcile included a replayed collision impulse, the visual smoothing " +
                             "slows down by this factor so the bounce doesn't snap — reads as 'I got hit' " +
                             "instead of 'I teleported'.")]
    private float _collisionReplaySlowdown = 0.5f;

    [Header("Peer collision")]
    [SerializeField, Tooltip("Ignore local collisions between this meatball's collider and every other " +
                             "meatball collider. Prevents the owner from double-resolving bounces: the " +
                             "authoritative impulse arrives through the snapshot sidecar instead.")]
    private bool _ignorePeerCollisions = true;

    [Header("Logging")]
    [SerializeField] private bool _logReconciles;
    [SerializeField] private bool _logPredictionSteps;

    // ── Ring buffer of predicted states ────────────────────────────────────
    private struct PredictedState
    {
        public ulong tick;
        public Vector3 position;
        public Quaternion rotation;
        public Vector3 velocity;
        public Vector3 angularVelocity;
    }

    private PredictedState[] _predicted;
    private int _predictedHead;
    private int _predictedCount;

    private Rigidbody _rb;
    private MeatballClientInputHandler _input;
    private MeatballMovementSettings _settings;

    private static readonly Collider[] _groundHits = new Collider[8];

    // Snapshot queued by MeatballNetSync; consumed at the top of the next FixedUpdate.
    private bool _hasPendingSnapshot;
    private MeatballSnapshot _pendingSnapshot;

    // Current visual offset on the MeatballVisual child (decays to 0 in LateUpdate).
    private Transform _visualChild;
    private Vector3 _visualOffset;
    private bool _lastReconcileReplayedCollision;

    // Peer colliders for which we have already called Physics.IgnoreCollision.
    private readonly HashSet<Collider> _ignoredPeerColliders = new();
    private Collider _selfCollider;

    // Remembers whether we toggled global simulation mode so despawn can restore it.
    private SimulationMode _prevSimulationMode;
    private bool _simulationModeOverridden;

    private void Awake()
    {
        _rb = GetComponent<Rigidbody>();
        _input = GetComponent<MeatballClientInputHandler>();
        _settings = GetComponent<MeatballPhysicsController>().Settings;
        _predicted = new PredictedState[Mathf.Max(8, _historySize)];
        _visualChild = transform.Find(_visualChildName);
        _selfCollider = GetComponent<Collider>();
        enabled = false; // wait for OnNetworkSpawn to decide
    }

    public override void OnNetworkSpawn()
    {
        // Host owns its own meatball but runs the authoritative controller already.
        // Remote-owner clients don't simulate — they interpolate via MeatballNetSync.
        if (!IsOwner || IsServer)
        {
            enabled = false;
            return;
        }

        enabled = true;

        // Take manual control of physics stepping so rollback replay uses the exact same
        // integration as the live tick. Restored on despawn.
        _prevSimulationMode = Physics.simulationMode;
        Physics.simulationMode = SimulationMode.Script;
        _simulationModeOverridden = true;

        Debug.Log($"[PredictedMeatball] Owner prediction enabled (clientId={NetworkManager.LocalClientId}, " +
                  $"simModeWas={_prevSimulationMode}).");
    }

    public override void OnNetworkDespawn()
    {
        if (_simulationModeOverridden)
        {
            Physics.simulationMode = _prevSimulationMode;
            _simulationModeOverridden = false;
        }
    }

    /// <summary>
    /// Called by <see cref="MeatballNetSync"/> on the owning client when a fresh snapshot
    /// arrives. We queue it and process in FixedUpdate so rollback doesn't run inside the
    /// RPC dispatch callback (which would race with the next physics step).
    /// </summary>
    public void ReceiveSnapshot(MeatballSnapshot snapshot)
    {
        // If a newer snapshot is already queued, drop the older one.
        if (_hasPendingSnapshot && snapshot.tick <= _pendingSnapshot.tick) return;
        _pendingSnapshot = snapshot;
        _hasPendingSnapshot = true;
    }

    private void FixedUpdate()
    {
        if (!IsOwner || IsServer) return;

        if (_ignorePeerCollisions) RefreshPeerCollisionIgnores();

        if (_hasPendingSnapshot)
        {
            ProcessReconcile(_pendingSnapshot);
            _hasPendingSnapshot = false;
        }

        ulong tick = NetworkTick.Instance != null ? NetworkTick.Instance.Current : 0;
        InputFrame frame = _input.LatestFrame;
        if (frame.tick != tick)
        {
            // Latest input was tagged with a different tick (e.g. handler hasn't run yet or
            // lagged a tick behind NetworkTick advancement). Use the frame but re-tag it.
            frame.tick = tick;
        }

        ApplyTickAndStep(frame, recordPrediction: true, manualStep: true);

        if (_logPredictionSteps)
            Debug.Log($"[PredictedMeatball] tick={tick} pos={_rb.position} vel={_rb.linearVelocity} " +
                      $"move={frame.move} jump={frame.jump}");
    }

    /// <summary>
    /// Applies one tick's input via MeatballMotor, manually steps physics, and optionally
    /// records the resulting state in the prediction ring buffer. Used by both the live
    /// FixedUpdate path and the reconcile replay loop.
    /// </summary>
    private void ApplyTickAndStep(InputFrame frame, bool recordPrediction, bool manualStep)
    {
        bool grounded = MeatballMotor.ComputeGrounded(
            _rb.position, _rb.linearVelocity, _settings, _groundHits, out float rampAugment);

        MeatballMotor.ApplyTick(_rb, frame, _settings, grounded, rampAugment, out _);

        if (manualStep)
            Physics.Simulate(Time.fixedDeltaTime);

        if (recordPrediction) RecordPredicted(frame.tick);
    }

    private void RecordPredicted(ulong tick)
    {
        _predicted[_predictedHead] = new PredictedState
        {
            tick = tick,
            position = _rb.position,
            rotation = _rb.rotation,
            velocity = _rb.linearVelocity,
            angularVelocity = _rb.angularVelocity,
        };
        _predictedHead = (_predictedHead + 1) % _predicted.Length;
        if (_predictedCount < _predicted.Length) _predictedCount++;
    }

    private bool TryGetPredicted(ulong tick, out PredictedState state)
    {
        for (int i = 0; i < _predictedCount; i++)
        {
            int idx = (_predictedHead - 1 - i + _predicted.Length) % _predicted.Length;
            if (_predicted[idx].tick == tick)
            {
                state = _predicted[idx];
                return true;
            }
        }
        state = default;
        return false;
    }

    private void ClearPredictionHistory()
    {
        _predictedHead = 0;
        _predictedCount = 0;
    }

    // ── Reconciliation ─────────────────────────────────────────────────────

    private void ProcessReconcile(MeatballSnapshot snap)
    {
        ulong currentTick = NetworkTick.Instance != null ? NetworkTick.Instance.Current : 0;

        // Capture where the visible mesh was before we rewrite the Rigidbody. Used to
        // preserve visual continuity across the snap+replay.
        Vector3 preReconcileRbPos = _rb.position;

        bool hadPrediction = TryGetPredicted(snap.tick, out PredictedState predicted);
        float posError = hadPrediction ? Vector3.Distance(predicted.position, snap.position) : float.PositiveInfinity;

        // Hard-snap path: teleport, respawn, or missing prediction history entirely.
        if (!hadPrediction || posError >= _hardSnapDistance)
        {
            HardSnap(snap);
            _visualOffset = Vector3.zero;
            _lastReconcileReplayedCollision = false;
            if (_logReconciles)
                Debug.LogWarning($"[PredictedMeatball] HARD SNAP tick={snap.tick} posError={posError:F2} " +
                                 $"(hadPrediction={hadPrediction}).");
            return;
        }

        // Soft snap + replay. Write authoritative state first, then replay subsequent ticks.
        _rb.position = snap.position;
        _rb.rotation = snap.rotation;
        _rb.linearVelocity = snap.velocity;
        _rb.angularVelocity = snap.angularVelocity;

        bool replayedCollision = false;

        // Apply any collision impulses stamped with T_auth itself. The motor hasn't run yet
        // for this tick on the replay side because input was already applied by the host.
        if (snap.collisions != null)
        {
            for (int i = 0; i < snap.collisions.Length; i++)
            {
                if (snap.collisions[i].tick != snap.tick) continue;
                _rb.AddForce(snap.collisions[i].impulse, ForceMode.Impulse);
                replayedCollision = true;
            }
        }

        // Replay ticks [T_auth+1 .. currentTick-1] using locally-buffered inputs. The live
        // FixedUpdate path below this call will apply the currentTick frame and simulate once.
        ClearPredictionHistory();
        RecordPredicted(snap.tick); // so future reconciles for T_auth still find a match

        for (ulong t = snap.tick + 1; t < currentTick; t++)
        {
            if (!_input.TryGetFrame(t, out InputFrame frame))
                frame = InputFrame.Zero(t);

            // Collision impulses for t > snap.tick haven't been broadcast yet — that's fine,
            // the next snapshot will re-reconcile with them.
            ApplyTickAndStep(frame, recordPrediction: true, manualStep: true);
        }

        // Visual offset: what the mesh saw vs. where physics now is. Decays to zero in LateUpdate.
        Vector3 postReplayRbPos = _rb.position;
        Vector3 delta = preReconcileRbPos - postReplayRbPos;

        if (posError > _smoothErrorThreshold)
            _visualOffset += delta;

        _lastReconcileReplayedCollision = replayedCollision;

        if (_logReconciles)
            Debug.Log($"[PredictedMeatball] reconcile tick={snap.tick} err={posError:F3} replay " +
                      $"ticks={(long)currentTick - (long)snap.tick - 1} collision={replayedCollision} " +
                      $"visualOffset+={delta.magnitude:F3}");
    }

    private void HardSnap(MeatballSnapshot snap)
    {
        _rb.position = snap.position;
        _rb.rotation = snap.rotation;
        _rb.linearVelocity = snap.velocity;
        _rb.angularVelocity = snap.angularVelocity;
        ClearPredictionHistory();
        RecordPredicted(snap.tick);

        // Kill implicit Verlet velocity on every spaghetti chain so the rope doesn't whip
        // from the teleport. Runs on every SpaghettiRenderer since only the lower-indexed
        // partner in each pair actually owns the chain, and we don't know which side that is.
        for (int i = 0; i < SpaghettiRenderer.Instances.Count; i++)
            SpaghettiRenderer.Instances[i]?.ResetChainVelocities();

        // Step forward to currentTick so the next FixedUpdate applies the live input on top
        // of the correct starting state (no replay when we've hard-snapped).
        ulong currentTick = NetworkTick.Instance != null ? NetworkTick.Instance.Current : 0;
        for (ulong t = snap.tick + 1; t < currentTick; t++)
        {
            if (!_input.TryGetFrame(t, out InputFrame frame))
                frame = InputFrame.Zero(t);
            ApplyTickAndStep(frame, recordPrediction: true, manualStep: true);
        }
    }

    // ── Visual smoothing (mesh child) ──────────────────────────────────────

    private void LateUpdate()
    {
        if (!IsOwner || IsServer) return;
        if (_visualChild == null) return;

        float decay = _visualDecayRate;
        if (_lastReconcileReplayedCollision) decay *= _collisionReplaySlowdown;

        // Exponential decay toward zero with frame-independent rate.
        float k = 1f - Mathf.Exp(-decay * Time.deltaTime);
        _visualOffset = Vector3.Lerp(_visualOffset, Vector3.zero, k);

        if (_visualOffset.sqrMagnitude < 1e-8f)
        {
            _visualOffset = Vector3.zero;
            _lastReconcileReplayedCollision = false;
        }

        _visualChild.localPosition = _visualOffset;
    }

    // ── Peer collision ignore ──────────────────────────────────────────────

    private void RefreshPeerCollisionIgnores()
    {
        if (_selfCollider == null) return;
        var instances = TetherForce.Instances;
        for (int i = 0; i < instances.Count; i++)
        {
            TetherForce other = instances[i];
            if (other == null || other.gameObject == gameObject) continue;
            Collider otherCollider = other.GetComponent<Collider>();
            if (otherCollider == null) continue;
            if (_ignoredPeerColliders.Contains(otherCollider)) continue;

            Physics.IgnoreCollision(_selfCollider, otherCollider, true);
            _ignoredPeerColliders.Add(otherCollider);
        }
    }
}
