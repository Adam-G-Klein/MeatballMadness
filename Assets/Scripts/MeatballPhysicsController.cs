using System;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;


/// <summary>
/// Host-only physics controller. Owns a tick-keyed input buffer fed by
/// <see cref="MeatballClientInputHandler"/>, and each FixedUpdate looks up the input
/// for the current <see cref="NetworkTick"/> and applies it via <see cref="MeatballMotor"/>.
/// Disabled on non-host clients — they receive state from MeatballNetSync instead.
///
/// Missing-input policy (Rocket League "decay by age"):
///  - If input for tick T is present: apply it.
///  - If absent but a newer tick arrives later: that newer frame wins and is stored,
///    but the older tick stays gapped.
///  - If absent: repeat the last known input for up to _maxRepeatTicks. After that,
///    zero move and release jump/sprint/reel.
///
/// Collision impulses are recorded per-tick and drained by MeatballNetSync into the
/// snapshot sidecar for broadcast to clients (unused until rollout step 8).
/// </summary>
[DefaultExecutionOrder(0)]
[RequireComponent(typeof(Rigidbody), typeof(Collider), typeof(NetworkObject))]
public class MeatballPhysicsController : NetworkBehaviour
{
    /// <summary>
    /// All MeatballPhysicsController instances currently running on the server.
    /// Populated in OnNetworkSpawn (IsServer only); tether force logic reads this list.
    /// </summary>
    public static readonly List<MeatballPhysicsController> ServerInstances = new();

    /// <summary>Fired on the server when a meatball's NetworkObject spawns.</summary>
    public static event Action<MeatballPhysicsController> OnMeatballSpawned;
    /// <summary>Fired on the server when a meatball's NetworkObject despawns.</summary>
    public static event Action<MeatballPhysicsController> OnMeatballDespawned;

    [SerializeField] private MeatballMovementSettings _settings;
    [SerializeField] private MeatballBounceSettings _bounceSettings;

    [Header("Input Buffering (host)")]
    [Tooltip("If an input frame for the current tick is missing, repeat the last known " +
             "input for up to this many ticks before zeroing move/jump/sprint/reel.")]
    [SerializeField] private int _maxRepeatTicks = 10;

    [Tooltip("Log a line each time a tick is processed: present / repeated / zeroed / future.")]
    [SerializeField] private bool _logInputApplication;

    [Tooltip("Log every collision impulse that gets recorded for the snapshot sidecar.")]
    [SerializeField] private bool _logCollisionImpulses = true;

    // ── Tick-keyed input buffer (host only) ───────────────────────────────────
    private readonly Dictionary<ulong, InputFrame> _inputsByTick = new();
    private InputFrame _lastAppliedInput;
    private int _ticksSinceFreshInput;
    private ulong _lastAppliedTick;

    // ── Public accessors for satellite scripts (e.g. SpaghettiReelAbility) ───
    public bool ReelHeld { get; private set; }
    public Vector2 LastAppliedMove => _lastAppliedInput.move;
    public ulong LastAppliedInputTick => _lastAppliedTick;

    // ── Collision impulse recording for the snapshot sidecar ──────────────────
    private readonly List<CollisionImpulse> _pendingCollisionImpulses = new();

    private Rigidbody _rb;
    private float _jumpHeightRampAugment;
    private static readonly Collider[] _groundHits = new Collider[8];

    public Rigidbody Rigidbody => _rb;
    public MeatballMovementSettings Settings => _settings;
    public MeatballBounceSettings BounceSettings => _bounceSettings;

    private void Awake()
    {
        _rb = GetComponent<Rigidbody>();
        _rb.interpolation = RigidbodyInterpolation.Interpolate;
        ApplyRigidbodySettings();
        ApplyPhysicsMaterial();
    }

    private void ApplyRigidbodySettings()
    {
        _rb.linearDamping = _settings.linearDrag;
        _rb.angularDamping = _settings.angularDrag;
    }

    private void ApplyPhysicsMaterial()
    {
        var mat = new PhysicsMaterial("MeatballPhysics")
        {
            dynamicFriction = _settings.dynamicFriction,
            staticFriction = _settings.staticFriction,
            bounciness = _settings.bounciness,
            frictionCombine = PhysicsMaterialCombine.Multiply,
            bounceCombine = PhysicsMaterialCombine.Maximum,
        };
        GetComponent<Collider>().material = mat;
    }

    public override void OnNetworkSpawn()
    {
        if (IsServer)
        {
            ServerInstances.Add(this);
            OnMeatballSpawned?.Invoke(this);
            Debug.Log($"[MeatballPhysics] Meatball registered (owner={OwnerClientId}). Server count: {ServerInstances.Count}");
        }
    }

    public override void OnNetworkDespawn()
    {
        ServerInstances.Remove(this);
        OnMeatballDespawned?.Invoke(this);
    }

    /// <summary>
    /// Called by MeatballClientInputHandler's redundant ServerRpc. Stores each frame
    /// in the tick-keyed dictionary; duplicates are ignored so redundancy costs nothing
    /// beyond the bytes on the wire.
    /// </summary>
    public void ReceiveInputs(InputFrame[] frames)
    {
        if (!IsServer || frames == null) return;
        for (int i = 0; i < frames.Length; i++)
        {
            var f = frames[i];
            // Never overwrite — the packet with the earliest tick wins on duplicate keys
            // (the semantics are the same either way since frames for the same tick must
            // be identical, but skipping the write keeps this allocation-free).
            if (!_inputsByTick.ContainsKey(f.tick))
                _inputsByTick.Add(f.tick, f);
        }
    }

    private void FixedUpdate()
    {
        if (!IsServer) return;

        ulong currentTick = NetworkTick.Instance != null ? NetworkTick.Instance.Current : 0;

        InputFrame frame = ResolveInputForTick(currentTick);
        ReelHeld = frame.reel;

        bool grounded = MeatballMotor.ComputeGrounded(
            _rb.position,
            _rb.linearVelocity,
            _settings,
            _groundHits,
            out _jumpHeightRampAugment);

        MeatballMotor.ApplyTick(
            _rb, frame, _settings, grounded, _jumpHeightRampAugment,
            out bool jumpConsumed);

        // Clear the jump bit on the remembered frame so the decay-by-age "repeat last
        // input" rule doesn't cause the host to fire a second jump once the meatball
        // becomes grounded mid-repeat-window.
        if (jumpConsumed) _lastAppliedInput.jump = false;

        PruneOldInputs(currentTick);
    }

    /// <summary>
    /// Decide which InputFrame to apply on tick T. Implements the decay-by-age rule.
    /// Includes a lookback step: if tick T is missing but an input for an earlier tick
    /// arrived after its own tick was already processed (possible under RPC-scheduling
    /// races), pick up the newest such frame before falling back to repeat-last-input.
    /// </summary>
    private InputFrame ResolveInputForTick(ulong tick)
    {
        if (_inputsByTick.TryGetValue(tick, out var fresh))
        {
            _lastAppliedInput = fresh;
            _lastAppliedInput.tick = tick;
            _ticksSinceFreshInput = 0;
            _lastAppliedTick = tick;
            if (_logInputApplication)
                Debug.Log($"[MeatballPhysics] tick={tick} FRESH move={fresh.move} jump={fresh.jump} sprint={fresh.sprint} reel={fresh.reel}");
            return fresh;
        }

        if (TryFindLatestUnconsumed(tick, out var recovered))
        {
            _lastAppliedInput = recovered;
            _lastAppliedInput.tick = tick;
            _ticksSinceFreshInput = 0;
            _lastAppliedTick = tick;
            if (_logInputApplication)
                Debug.Log($"[MeatballPhysics] tick={tick} RECOVERED (from tick={recovered.tick}) move={recovered.move} jump={recovered.jump}");
            return _lastAppliedInput;
        }

        _ticksSinceFreshInput++;

        if (_ticksSinceFreshInput <= _maxRepeatTicks)
        {
            if (_logInputApplication)
                Debug.Log($"[MeatballPhysics] tick={tick} REPEAT (age={_ticksSinceFreshInput}) move={_lastAppliedInput.move}");
            _lastAppliedTick = tick;
            return _lastAppliedInput;
        }

        // Beyond the cap — zero out. Also clears the remembered latch so a stale jump/sprint
        // doesn't sit on the controller indefinitely.
        _lastAppliedInput = InputFrame.Zero(tick);
        _lastAppliedTick = tick;
        if (_logInputApplication)
            Debug.LogWarning($"[MeatballPhysics] tick={tick} ZEROED (no input for {_ticksSinceFreshInput} ticks)");
        return _lastAppliedInput;
    }

    /// <summary>
    /// Scans the input dict for the newest entry with tick &gt; <see cref="_lastAppliedTick"/>
    /// and tick &lt;= <paramref name="upToTick"/>. Covers the case where an input for tick T
    /// landed in the dict after tick T was already processed (RPC scheduling race), so the
    /// controller can still apply it on T+1 instead of leaving the frame stranded.
    /// </summary>
    private bool TryFindLatestUnconsumed(ulong upToTick, out InputFrame frame)
    {
        frame = default;
        bool found = false;
        ulong bestTick = 0;
        foreach (var kvp in _inputsByTick)
        {
            ulong t = kvp.Key;
            if (t <= _lastAppliedTick) continue;
            if (t > upToTick) continue;
            if (!found || t > bestTick)
            {
                bestTick = t;
                frame = kvp.Value;
                found = true;
            }
        }
        return found;
    }

    /// <summary>
    /// Drops entries older than the max-repeat window so the dictionary doesn't grow
    /// without bound. We keep a small trailing margin in case reordered packets arrive
    /// moments after their tick has been processed.
    /// </summary>
    private void PruneOldInputs(ulong currentTick)
    {
        const int trailingMargin = 8;
        if (currentTick <= (ulong)(_maxRepeatTicks + trailingMargin)) return;

        ulong cutoff = currentTick - (ulong)(_maxRepeatTicks + trailingMargin);
        List<ulong> toRemove = null;
        foreach (var kvp in _inputsByTick)
        {
            if (kvp.Key < cutoff)
            {
                toRemove ??= new List<ulong>();
                toRemove.Add(kvp.Key);
            }
        }
        if (toRemove == null) return;
        for (int i = 0; i < toRemove.Count; i++) _inputsByTick.Remove(toRemove[i]);
    }

    /// <summary>
    /// Host-only meatball-vs-meatball bounce. Applies an impulse to both rigidbodies
    /// and records it into each side's pending collision list for the snapshot sidecar.
    /// </summary>
    private void OnCollisionEnter(Collision collision)
    {
        if (!IsServer) return;
        if (_bounceSettings == null) return;

        if (!collision.rigidbody) return;
        if (!collision.rigidbody.TryGetComponent(out MeatballPhysicsController other)) return;

        // Each collision fires OnCollisionEnter on both meatballs. Only process once
        // (from the lower instance ID) and apply the impulse to both ends here.
        if (GetInstanceID() >= other.GetInstanceID()) return;

        Vector3 selfImpulse = MeatballBounceResolver.Compute(
            _rb.position, _rb.linearVelocity,
            other._rb.position, other._rb.linearVelocity,
            _bounceSettings);
        Vector3 otherImpulse = -selfImpulse;

        _rb.AddForce(selfImpulse, ForceMode.Impulse);
        other._rb.AddForce(otherImpulse, ForceMode.Impulse);

        ulong tick = NetworkTick.Instance != null ? NetworkTick.Instance.Current : 0;
        Vector3 contact = collision.contactCount > 0 ? collision.GetContact(0).point : _rb.position;

        RecordCollisionImpulse(tick, selfImpulse, contact, other.OwnerClientId);
        other.RecordCollisionImpulse(tick, otherImpulse, contact, OwnerClientId);

        if (_logCollisionImpulses)
            Debug.Log($"[MeatballPhysics] Collision tick={tick} self={OwnerClientId} other={other.OwnerClientId} " +
                      $"|impulse|={selfImpulse.magnitude:F2}");
    }

    private void RecordCollisionImpulse(ulong tick, Vector3 impulse, Vector3 contactPoint, ulong otherClientId)
    {
        _pendingCollisionImpulses.Add(new CollisionImpulse
        {
            tick = tick,
            impulse = impulse,
            contactPoint = contactPoint,
            otherClientId = otherClientId,
        });
    }

    /// <summary>
    /// Called by MeatballNetSync each time it broadcasts a snapshot. Returns the pending
    /// impulses accumulated since the last drain and clears the internal list.
    /// </summary>
    public CollisionImpulse[] DrainPendingCollisionImpulses()
    {
        if (_pendingCollisionImpulses.Count == 0) return System.Array.Empty<CollisionImpulse>();
        var arr = _pendingCollisionImpulses.ToArray();
        _pendingCollisionImpulses.Clear();
        return arr;
    }
}
