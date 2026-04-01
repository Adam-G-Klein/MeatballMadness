using UnityEngine;

/// <summary>
/// Shared movement tuning data for meatball physics.
/// Assign to MeatballSolo (debug/local) and eventually MeatballPhysicsController
/// (networked) so both use identical feel.
/// </summary>
[CreateAssetMenu(fileName = "MeatballMovementSettings", menuName = "Meatball Madness/Meatball Movement Settings")]
public class MeatballMovementSettings : ScriptableObject
{
    // ── Movement ──────────────────────────────────────────────────────────────

    [Header("Movement")]
    [Tooltip("Continuous force (N) applied each FixedUpdate tick while input is held.")]
    public float moveForce = 15f;

    [Tooltip("Horizontal speed (m/s) cap while walking.")]
    public float maxWalkHorizontalSpeed = 8f;

    [Tooltip("Horizontal speed (m/s) cap while sprinting.")]
    public float maxRunHorizontalSpeed = 12f;

    [Tooltip("Fraction of moveForce applied while airborne (0 = no air control, 1 = full).")]
    [Range(0f, 1f)]
    public float airControlFraction = 0.25f;

    // ── Jump ──────────────────────────────────────────────────────────────────

    [Header("Jump")]
    [Tooltip("Upward impulse magnitude applied on jump.")]
    public float jumpImpulse = 7f;

    [Tooltip("Radius of the overlap sphere used for ground detection. "
           + "Should roughly match the meatball's collider radius.")]
    public float groundCheckRadius = 0.55f;

    [Tooltip("Layers treated as ground for jump detection.")]
    public LayerMask groundMask = ~0;

    // ── Drag ──────────────────────────────────────────────────────────────────

    [Header("Drag")]
    [Tooltip("Linear (translational) damping applied by the Rigidbody each frame.")]
    public float linearDrag = 1.5f;

    [Tooltip("Angular damping applied by the Rigidbody each frame. "
           + "Higher values damp the rolling spin faster.")]
    public float angularDrag = 1f;

    // ── Ground Friction ───────────────────────────────────────────────────────

    [Header("Ground Friction")]
    [Tooltip("Dynamic (kinetic) friction coefficient of the meatball surface. "
           + "Combined (Multiply) with the ground's friction.")]
    [Range(0f, 1f)]
    public float dynamicFriction = 0.6f;

    [Tooltip("Static friction coefficient. Higher values resist starting to slide.")]
    [Range(0f, 1f)]
    public float staticFriction = 0.6f;

    [Tooltip("Bounciness (coefficient of restitution). 0 = no bounce, 1 = perfectly elastic.")]
    [Range(0f, 1f)]
    public float bounciness = 0f;

    // ── Latency Simulation ────────────────────────────────────────────────────

    [Header("Latency Simulation")]
    [Tooltip("Simulated one-way network latency in milliseconds. Models the input delay "
           + "a client experiences in a host-authoritative session. Set to 0 to disable.")]
    [Min(0f)]
    public float simulatedLatencyMs = 0f;
}
