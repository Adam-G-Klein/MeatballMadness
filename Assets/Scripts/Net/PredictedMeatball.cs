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
/// The mesh lives on a separate, unparented <c>MeatballVisual</c> GameObject spawned
/// from a prefab reference. Each LateUpdate it is positioned at
/// <c>transform.position + _visualOffset</c> (rotation = transform.rotation) so the
/// visual can smoothly catch up to the physics body after a reconcile without being
/// slaved to the parent's interpolated transform. Camera, tether, ground check, and
/// collider all live on the root and read the authoritative Rigidbody transform.
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

    [Header("Visual (separate GameObject)")]
    [SerializeField, Tooltip("Prefab for the visual mesh. Instantiated unparented at spawn and " +
                             "its world position/rotation is driven from this script every LateUpdate. " +
                             "Keeping the visual outside the physics hierarchy is what lets the " +
                             "post-reconcile offset smoothing run without fighting Rigidbody interpolation.")]
    private GameObject _visualPrefab;

    [SerializeField, Tooltip("Higher = faster mesh catch-up (per-second exponential decay rate). 15-25 feels " +
                             "good at 60 fps; widen this for collision-replay frames (handled automatically).")]
    private float _visualDecayRate = 20f;

    [SerializeField, Tooltip("When a reconcile included a replayed collision impulse, the visual smoothing " +
                             "slows down by this factor so the bounce doesn't snap — reads as 'I got hit' " +
                             "instead of 'I teleported'.")]
    private float _collisionReplaySlowdown = 0.5f;

    [Header("Peer collision")]
    [SerializeField, Tooltip("When true, locally disables every meatball-vs-meatball collision on this " +
                             "client so the predicted ball never detects peer contacts — bounces only " +
                             "arrive via the authoritative snapshot. When false (default), the owning " +
                             "client speculatively resolves peer bounces with the same math as the host " +
                             "using MeatballBounceResolver; reconciliation corrects residual error.")]
    private bool _ignorePeerCollisions = false;

    [SerializeField, Tooltip("Log a line whenever the client speculatively applies a bounce impulse.")]
    private bool _logPredictedBounces;

    [Header("Logging")]
    [SerializeField] private bool _logReconciles;
    [SerializeField] private bool _logPredictionSteps;

    [Header("Gizmos (client-only)")]
    [SerializeField, Tooltip("Draw a wire sphere at the Rigidbody (physics) position and another at the " +
                             "spawned visual's position so the owner-side visual offset is visible " +
                             "in Scene view. Drawn only on the owning client.")]
    private bool _drawPhysicsVsVisualGizmos = true;

    [SerializeField] private float _gizmoSphereRadius = 0.4f;
    [SerializeField] private Color _physicsGizmoColor = new Color(0f, 1f, 0.3f, 0.9f);
    [SerializeField] private Color _visualGizmoColor = new Color(1f, 0.85f, 0f, 0.9f);
    [SerializeField] private Color _collisionTweenGizmoColor = new Color(1f, 0.15f, 0.15f, 0.9f);

    [SerializeField, Tooltip("Seconds the red 'collision tween kickoff' marker stays visible after a " +
                             "collision-bearing snapshot triggers a visual offset.")]
    private float _collisionTweenGizmoDuration = 0.6f;

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
    private MeatballPhysicsController _controller;
    private MeatballMovementSettings _settings;
    private MeatballBounceSettings _bounceSettings;

    private static readonly Collider[] _groundHits = new Collider[8];

    // Snapshot queued by MeatballNetSync; consumed at the top of the next FixedUpdate.
    private bool _hasPendingSnapshot;
    private MeatballSnapshot _pendingSnapshot;

    // Current visual offset applied on top of the Rigidbody transform (decays to 0 in LateUpdate).
    // The visual is a separate world-space GameObject — see _visualInstance.
    private GameObject _visualInstance;
    private Transform _visualTransform;
    private Vector3 _visualOffset;
    private bool _lastReconcileReplayedCollision;

    /// <summary>
    /// World-space visual GameObject spawned by this component. Other scripts that
    /// need a "where the mesh appears" reference (e.g. chef follower) should read this
    /// instead of looking up a child, since the visual is deliberately unparented.
    /// </summary>
    public Transform VisualTransform => _visualTransform;

    // Peer colliders for which we have already called Physics.IgnoreCollision.
    private readonly HashSet<Collider> _ignoredPeerColliders = new();
    private Collider _selfCollider;

    // Most recent location where ProcessReconcile applied a visual offset because the
    // incoming snapshot carried a collision impulse for its tick. Used purely for the
    // gizmo that marks where the visual "tween" was kicked off. Negative time = never.
    private Vector3 _lastCollisionTweenPos;
    private float _lastCollisionTweenTime = -1f;
    private float _lastCollisionTweenError;

    // Remembers whether we toggled global simulation mode so despawn can restore it.
    private SimulationMode _prevSimulationMode;
    private bool _simulationModeOverridden;

    private void Awake()
    {
        _rb = GetComponent<Rigidbody>();
        _input = GetComponent<MeatballClientInputHandler>();
        _controller = GetComponent<MeatballPhysicsController>();
        _settings = _controller.Settings;
        _bounceSettings = _controller.BounceSettings;
        _predicted = new PredictedState[Mathf.Max(8, _historySize)];
        _selfCollider = GetComponent<Collider>();

        if (_visualPrefab != null)
        {
            _visualInstance = Instantiate(_visualPrefab, transform.position, transform.rotation);
            _visualTransform = _visualInstance.transform;
            _visualTransform.localScale = Vector3.one * 2f;
        }
        else
        {
            Debug.LogWarning("[PredictedMeatball] No visual prefab assigned — mesh will not appear.", this);
        }
    }

    public override void OnNetworkSpawn()
    {
        // Prediction-only: host owns its own meatball via MeatballPhysicsController and
        // remote-owner clients interpolate via MeatballNetSync. Only the owning client
        // needs the rollback/replay machinery and the manual physics stepping.
        if (!IsOwner || IsServer) return;

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

    private void OnDestroy()
    {
        if (_visualInstance != null) Destroy(_visualInstance);
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

        // Collision impulses stamped with T_auth are already integrated into snap.velocity
        // (the host records rb.linearVelocity AFTER Physics.Simulate for the tick). Applying
        // them again would double-count. We treat the sidecar purely as an event signal here
        // so the visual smoothing slows down for a few frames; the impulse itself is already
        // in the authoritative velocity we just wrote to the rigidbody.
        if (snap.collisions != null)
        {
            for (int i = 0; i < snap.collisions.Length; i++)
            {
                if (snap.collisions[i].tick != snap.tick) continue;
                replayedCollision = true;
                break;
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

        // ── Visual "tween" kickoff marker ──────────────────────────────────
        // When the snapshot carried a collision impulse for its tick AND the reconcile
        // produced a visible offset (posError above smoothing threshold), the mesh begins
        // its slowed exponential decay back to the physics body — that's the "tween" the
        // gizmo highlights. Recorded at the post-replay Rigidbody position (where physics
        // landed) so the marker pins to where the bounce was authoritatively resolved.
        if (replayedCollision && posError > _smoothErrorThreshold)
        {
            _lastCollisionTweenPos = postReplayRbPos;
            _lastCollisionTweenTime = Time.time;
            _lastCollisionTweenError = posError;
        }

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

    // ── Visual positioning (world-space, unparented) ───────────────────────

    private void LateUpdate()
    {
        if (_visualTransform == null) return;

        // Only the predicting owner accumulates a reconcile offset. Host and remote-owner
        // clients leave _visualOffset at zero so the visual tracks the Rigidbody exactly.
        if (IsOwner && !IsServer)
        {
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
        }

        // Read from transform.position rather than _rb.position so host and remote-owner
        // paths (where Rigidbody.interpolation smooths the transform between fixed steps)
        // produce a visually smooth result. On the owning client, simulationMode=Script
        // means transform.position == rb.position anyway.
        Transform t = transform;
        _visualTransform.SetPositionAndRotation(t.position + _visualOffset, t.rotation);
    }

    // ── Gizmos (owning client only) ────────────────────────────────────────

    private void OnDrawGizmos()
    {
        if (!_drawPhysicsVsVisualGizmos) return;
        if (!Application.isPlaying) return;
        // OnDrawGizmos still fires when the component is disabled; gate explicitly so the
        // host and remote-owner clients (where this script is disabled in OnNetworkSpawn)
        // don't draw anything. Predicted-physics state only exists on the owning client.
        if (!IsSpawned || !IsOwner || IsServer) return;
        if (_rb == null) return;

        // Physics body (true authoritative-ish position the rest of the simulation reads).
        Gizmos.color = _physicsGizmoColor;
        Gizmos.DrawWireSphere(_rb.position, _gizmoSphereRadius);

        // Visual mesh position. With no offset this overlaps the physics sphere; during
        // post-reconcile smoothing the two diverge until the offset decays to zero.
        if (_visualTransform != null)
        {
            Vector3 visualPos = _visualTransform.position;
            Gizmos.color = _visualGizmoColor;
            Gizmos.DrawWireSphere(visualPos, _gizmoSphereRadius * 0.9f);
            Gizmos.DrawLine(_rb.position, visualPos);
        }

        // Recent collision-broadcast tween kickoff: marks where ProcessReconcile observed
        // a snapshot with a collision impulse and seeded the visual offset.
        if (_lastCollisionTweenTime >= 0f)
        {
            float age = Time.time - _lastCollisionTweenTime;
            if (age <= _collisionTweenGizmoDuration)
            {
                float t = 1f - (age / _collisionTweenGizmoDuration); // 1 → 0
                Color c = _collisionTweenGizmoColor;
                c.a *= t;
                Gizmos.color = c;
                float r = _gizmoSphereRadius * (1f + (1f - t) * 0.75f); // expands as it fades
                Gizmos.DrawWireSphere(_lastCollisionTweenPos, r);
                Gizmos.DrawLine(_lastCollisionTweenPos, _lastCollisionTweenPos + Vector3.up * (1f + _lastCollisionTweenError));
            }
        }
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

    // ── Speculative client-side peer bounce ────────────────────────────────

    /// <summary>
    /// Fires on the owning client only (this component is disabled on host and remote-owner
    /// clients via OnNetworkSpawn). When our predicted meatball contacts another meatball's
    /// ghost, compute the same impulse the host would compute using <see cref="MeatballBounceResolver"/>
    /// and apply it locally so the player sees an immediate bounce instead of visibly tunneling
    /// through for ~RTT before the authoritative impulse arrives via snapshot.
    ///
    /// The ghost is kinematic and host-authoritative — we never touch it. Reconciliation on
    /// the next snapshot absorbs residual error between our predicted impulse and the host's.
    /// </summary>
    private void OnCollisionEnter(Collision collision)
    {
        if (!IsOwner || IsServer) return;
        if (_ignorePeerCollisions) return;
        if (_bounceSettings == null) return;
        if (collision.rigidbody == null) return;

        // Only bounce off other meatballs. Floor / ramp / static geometry: let PhysX handle
        // normally via the PhysicsMaterial.
        if (!collision.rigidbody.TryGetComponent(out MeatballPhysicsController otherController)) return;

        Rigidbody otherRb = collision.rigidbody;
        // Ghost peers are kinematic, so their Rigidbody.linearVelocity is not maintained.
        // Read the authoritative velocity from the last received snapshot instead — it's
        // what the host actually used when computing its own bounce.
        Vector3 otherVel = otherRb.linearVelocity;
        if (otherRb.isKinematic && otherController.TryGetComponent(out MeatballNetSync otherSync))
            otherVel = otherSync.NetworkedVelocity;

        Vector3 impulse = MeatballBounceResolver.Compute(
            _rb.position, _rb.linearVelocity,
            otherRb.position, otherVel,
            _bounceSettings);

        _rb.AddForce(impulse, ForceMode.Impulse);

        if (_logPredictedBounces)
            Debug.Log($"[PredictedMeatball] speculative bounce vs owner={otherController.OwnerClientId} " +
                      $"|impulse|={impulse.magnitude:F2} tick={(NetworkTick.Instance != null ? NetworkTick.Instance.Current : 0)}");
    }
}
