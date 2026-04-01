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

    [SerializeField] private MeatballMovementSettings _settings;

    // ── Latency-simulation queue ──────────────────────────────────────────────
    private struct InputPacket
    {
        public float DueTime; // Time.fixedTime when the packet should be consumed
        public Vector2 Move;
        public bool Jump;
        public bool Sprint;
    }
    private readonly Queue<InputPacket> _inputQueue = new();

    // ── Pending state consumed by FixedUpdate ─────────────────────────────────
    private Vector2 _pendingMove;
    private bool _pendingJump;
    private bool _pendingSprint;

    private Rigidbody _rb;

    /// <summary>Exposes the Rigidbody for server-side tether force application.</summary>
    public Rigidbody Rigidbody => _rb;

    private void Awake()
    {
        _rb = GetComponent<Rigidbody>();
        ApplyRigidbodySettings();
        ApplyPhysicsMaterial();
    }

    /// <summary>Pushes drag values from the settings asset onto the Rigidbody.</summary>
    private void ApplyRigidbodySettings()
    {
        _rb.linearDamping  = _settings.linearDrag;
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
            staticFriction  = _settings.staticFriction,
            bounciness      = _settings.bounciness,
            frictionCombine = PhysicsMaterialCombine.Multiply,
            bounceCombine   = PhysicsMaterialCombine.Maximum,
        };
        GetComponent<Collider>().material = mat;
    }

    public override void OnNetworkSpawn()
    {
        if (IsServer)
        {
            ServerInstances.Add(this);
            Debug.Log($"[Tether] Meatball registered. Server count: {ServerInstances.Count}");
        }
    }

    public override void OnNetworkDespawn()
    {
        ServerInstances.Remove(this);
    }

    /// <summary>
    /// Called by MeatballClientInputHandler's ServerRpc. Runs on the host only.
    /// When latency simulation is enabled the packet is queued and replayed after the
    /// configured delay, modelling the round-trip felt by a non-host client.
    /// </summary>
    public void ReceiveInput(Vector2 move, bool jump, bool sprint)
    {
        float delaySeconds = _settings.simulatedLatencyMs / 1000f;
        if (delaySeconds <= 0f)
        {
            // No delay — update pending state immediately.
            _pendingMove   = move;
            _pendingSprint = sprint;
            if (jump) _pendingJump = true;
            return;
        }

        _inputQueue.Enqueue(new InputPacket
        {
            DueTime = Time.fixedTime + delaySeconds,
            Move    = move,
            Jump    = jump,
            Sprint  = sprint,
        });
    }

    private void FixedUpdate()
    {
        DrainInputQueue();
        ApplyMovement();
        ApplyJump();
    }

    /// <summary>
    /// Flushes all queued packets whose DueTime has arrived into the pending-state fields.
    /// When simulatedLatencyMs is 0 the queue stays empty and this is a no-op.
    /// </summary>
    private void DrainInputQueue()
    {
        while (_inputQueue.Count > 0 && _inputQueue.Peek().DueTime <= Time.fixedTime)
        {
            InputPacket p = _inputQueue.Dequeue();
            _pendingMove   = p.Move;
            _pendingSprint = p.Sprint;
            if (p.Jump) _pendingJump = true;
        }
    }

    private void ApplyMovement()
    {
        if (_pendingMove == Vector2.zero) return;

        bool grounded = IsGrounded();

        // Choose the correct speed cap based on sprint state.
        float speedCap = _pendingSprint ? _settings.maxRunHorizontalSpeed
                                        : _settings.maxWalkHorizontalSpeed;

        Vector3 horizontalVel = new Vector3(_rb.linearVelocity.x, 0f, _rb.linearVelocity.z);
        if (horizontalVel.magnitude >= speedCap) return;

        // Reduce control authority while airborne.
        float forceMult = grounded ? 1f : _settings.airControlFraction;

        Vector3 moveDir = new Vector3(_pendingMove.x, 0f, _pendingMove.y);
        _rb.AddForce(moveDir * (_settings.moveForce * forceMult), ForceMode.Force);
    }

    private void ApplyJump()
    {
        if (!_pendingJump) return;
        if (!IsGrounded()) return; // hold the latch until we touch down

        _rb.AddForce(Vector3.up * _settings.jumpImpulse, ForceMode.Impulse);
        _pendingJump = false;
    }

    private bool IsGrounded()
    {
        // Sphere positioned slightly below the meatball origin.
        Vector3 origin = transform.position + Vector3.down * (_settings.groundCheckRadius - 0.05f);
        return Physics.CheckSphere(origin, _settings.groundCheckRadius, _settings.groundMask,
                                   QueryTriggerInteraction.Ignore);
    }
}
