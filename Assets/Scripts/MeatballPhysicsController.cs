using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Host-only physics controller. Receives input from MeatballClientController via ServerRpc
/// and drives the Rigidbody with AddForce. Disabled on non-host clients — they receive
/// state from MeatballNetSync instead.
///
/// Movement is applied as a continuous force capped by maxHorizontalSpeed.
/// Jump is latched until the meatball is grounded so a jump press is never silently dropped.
/// </summary>
[RequireComponent(typeof(Rigidbody), typeof(NetworkObject))]
public class MeatballPhysicsController : NetworkBehaviour
{
    [Header("Movement")]
    [SerializeField] private float moveForce = 15f;
    [SerializeField] private float maxHorizontalSpeed = 8f;

    [Header("Jump")]
    [SerializeField] private float jumpImpulse = 7f;
    // groundCheckRadius should roughly match the meatball's collider radius.
    [SerializeField] private float groundCheckRadius = 0.55f;

    [SerializeField] private LayerMask groundLayers = ~0;

    private Rigidbody _rb;
    private Vector2 _pendingMove;
    private bool _pendingJump;

    private void Awake() => _rb = GetComponent<Rigidbody>();

    public override void OnNetworkSpawn()
    {
        //if (!IsServer) enabled = false;
    }

    /// <summary>
    /// Called by MeatballClientController's ServerRpc. Runs on the host only.
    /// The jump flag is latched (OR-assigned) so a press is never clobbered by a
    /// subsequent no-jump packet before FixedUpdate gets a chance to consume it.
    /// </summary>
    public void ReceiveInput(Vector2 move, bool jump)
    {
        _pendingMove = move;
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

        // Only add force while below the speed cap so the meatball doesn't accelerate forever.
        Vector3 horizontalVel = new Vector3(_rb.linearVelocity.x, 0f, _rb.linearVelocity.z);
        if (horizontalVel.magnitude >= maxHorizontalSpeed) return;

        Vector3 moveDir = new Vector3(_pendingMove.x, 0f, _pendingMove.y);
        _rb.AddForce(moveDir * moveForce, ForceMode.Force);
    }

    private void ApplyJump()
    {
        if (!_pendingJump) return;
        if (!IsGrounded()) return; // hold the latch until we touch down

        _rb.AddForce(Vector3.up * jumpImpulse, ForceMode.Impulse);
        _pendingJump = false;
    }

    private bool IsGrounded()
    {
        // Sphere positioned slightly below the meatball origin.
        Vector3 origin = transform.position + Vector3.down * (groundCheckRadius - 0.05f);
        return Physics.CheckSphere(origin, groundCheckRadius, groundLayers, QueryTriggerInteraction.Ignore);
    }
}
