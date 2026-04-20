using System;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;


/// <summary>
/// Host-only physics controller. Receives input from MeatballClientInputHandler via ServerRpc
/// and drives the Rigidbody with AddForce. Disabled on non-host clients — they receive
/// state from MeatballNetSync instead.
///
/// All tuning lives in a MeatballMovementSettings ScriptableObject so values can be shared
/// with MeatballSolo and tweaked without touching code.
/// </summary>
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

    // ── Pending state consumed by FixedUpdate ─────────────────────────────────
    private Vector2 _pendingMove;
    private bool _pendingJump;
    private bool _pendingSprint;

    private Rigidbody _rb;

    // ── Ramp jump augment ─────────────────────────────────────────────────────
    private float _jumpHeightRampAugment;
    // Static buffer avoids per-frame allocation for ground overlap queries.
    private static readonly Collider[] _groundHits = new Collider[8];

    /// <summary>Exposes the Rigidbody for server-side tether force application.</summary>
    public Rigidbody Rigidbody => _rb;

    private void Awake()
    {
        _rb = GetComponent<Rigidbody>();
        _rb.interpolation = RigidbodyInterpolation.Interpolate;
        ApplyRigidbodySettings();
        ApplyPhysicsMaterial();
    }

    /// <summary>Pushes drag values from the settings asset onto the Rigidbody.</summary>
    private void ApplyRigidbodySettings()
    {
        _rb.linearDamping = _settings.linearDrag;
        _rb.angularDamping = _settings.angularDrag;
    }

    /// <summary>
    /// Creates a runtime PhysicsMaterial from the settings asset and assigns it to the
    /// meatball's Collider so friction and bounciness match the tuning data.
    /// </summary>
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
            Debug.Log($"[Tether] Meatball registered. Server count: {ServerInstances.Count}");
        }
    }

    public override void OnNetworkDespawn()
    {
        ServerInstances.Remove(this);
        OnMeatballDespawned?.Invoke(this);
    }

    /// <summary>
    /// Called by MeatballClientInputHandler's ServerRpc. Runs on the host only.
    /// Updates pending state immediately; FixedUpdate consumes it next tick.
    /// </summary>
    public void ReceiveInput(Vector2 move, bool jump, bool sprint)
    {
        _pendingMove = move;
        _pendingSprint = sprint;
        if (jump) _pendingJump = true;
    }

    private void FixedUpdate()
    {
        ApplyMovement();
        ApplyJump();
    }

    private void ApplyMovement()
    {
        if (_pendingMove == Vector2.zero) return;

        bool grounded = IsGrounded();

        // Sprint is only allowed while grounded.
        bool allowSprint = grounded && _pendingSprint;

        // Airborne movement is capped to walk speed.
        float speedCap = allowSprint
            ? _settings.maxRunHorizontalSpeed
            : _settings.maxWalkHorizontalSpeed;

        Vector3 horizontalVel = new Vector3(_rb.linearVelocity.x, 0f, _rb.linearVelocity.z);
        if (horizontalVel.magnitude >= speedCap) return;

        // Reduce control authority while airborne.
        float forceMult = grounded ? 1f : _settings.airControlFraction;

        Vector3 moveDir = new Vector3(_pendingMove.x, 0f, _pendingMove.y);
        _rb.AddForce(moveDir * (_settings.moveForce * forceMult), ForceMode.Force);
    }

    private float RampAugmentHeight() => _jumpHeightRampAugment;

    private void ApplyJump()
    {
        if (!_pendingJump) return;
        if (!IsGrounded()) return; // hold the latch until we touch down

        _rb.AddForce(Vector3.up * (_settings.jumpImpulse + RampAugmentHeight()), ForceMode.Impulse);
        _pendingJump = false;
    }

    /// <summary>
    /// Checks whether the meatball is touching ground. As a side effect, updates
    /// <see cref="_jumpHeightRampAugment"/> when a "Ramp"-layer object is detected:
    /// augment = dot(horizontalVelocity, rampDirection) * jumpHeightAugment.
    /// </summary>
    private bool IsGrounded()
    {
        Vector3 origin = transform.position + Vector3.down * (_settings.groundCheckRadius - 0.05f);

        // Combined mask: normal ground layers plus the Ramp layer so ramp objects
        // are captured in the same query without requiring the designer to add them
        // to groundMask manually.
        int rampLayer = LayerMask.NameToLayer("Ramp");
        int combinedMask = _settings.groundMask | (1 << rampLayer);

        int hitCount = Physics.OverlapSphereNonAlloc(
            origin,
            _settings.groundCheckRadius,
            _groundHits,
            combinedMask,
            QueryTriggerInteraction.Ignore
        );

        _jumpHeightRampAugment = 0f;
        for (int i = 0; i < hitCount; i++)
        {
            if (_groundHits[i].gameObject.layer != rampLayer) continue;

            if (!_groundHits[i].TryGetComponent(out RampSettings ramp)) break;

            Vector2 horizontalVel = new(_rb.linearVelocity.x, _rb.linearVelocity.z);
            _jumpHeightRampAugment = Vector2.Dot(horizontalVel, ramp.rampDirection) * ramp.jumpHeightAugment;
            break;
        }

        return hitCount > 0;
    }

    /// <summary>
    /// Host-only meatball-vs-meatball bounce. Applies an impulse to both rigidbodies
    /// along their separation axis, scaled by relative approach speed and capped by
    /// the bounce settings asset.
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

        Vector3 separation = _rb.position - other._rb.position;
        if (separation.sqrMagnitude < 1e-6f) separation = Vector3.right;
        Vector3 dir = separation.normalized;

        float approachSpeed = Mathf.Max(0f, Vector3.Dot(other._rb.linearVelocity - _rb.linearVelocity, dir));

        float impulse = Mathf.Min(
            _bounceSettings.baseBounceImpulse + _bounceSettings.velocityScale * approachSpeed,
            _bounceSettings.maxBounceImpulse);

        _rb.AddForce(dir * impulse, ForceMode.Impulse);
        other._rb.AddForce(-dir * impulse, ForceMode.Impulse);
    }

}