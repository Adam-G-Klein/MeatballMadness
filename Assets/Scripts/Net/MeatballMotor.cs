using UnityEngine;

/// <summary>
/// Pure-function movement force application shared by the host-authoritative
/// MeatballPhysicsController and (in a later rollout step) the owning client's
/// PredictedMeatball. Keeping one copy avoids host/client drift from two slightly
/// different force equations.
///
/// No hidden state: everything the motor needs is passed in.
/// </summary>
public static class MeatballMotor
{
    /// <summary>
    /// Applies one tick of movement + jump using the same rules as the old
    /// MeatballPhysicsController path. <paramref name="grounded"/> and
    /// <paramref name="rampJumpAugment"/> are computed by the caller (typically via
    /// <see cref="ComputeGrounded"/>) because ground detection touches physics queries.
    /// </summary>
    /// <param name="rb">Rigidbody to push.</param>
    /// <param name="input">Tick-tagged input for this FixedUpdate.</param>
    /// <param name="settings">Shared tuning asset.</param>
    /// <param name="grounded">True if the caller's ground check passed this tick.</param>
    /// <param name="rampJumpAugment">Extra jump impulse from ramp augment (see RampSettings).</param>
    /// <param name="consumeJump">
    /// Out-param: true if the motor consumed a jump impulse this tick. The caller uses it
    /// to clear its own jump latch.
    /// </param>
    public static void ApplyTick(
        Rigidbody rb,
        InputFrame input,
        MeatballMovementSettings settings,
        bool grounded,
        float rampJumpAugment,
        out bool consumeJump)
    {
        consumeJump = false;
        if (rb == null || settings == null) return;

        ApplyMovement(rb, input, settings, grounded);

        if (input.jump && grounded)
        {
            rb.AddForce(Vector3.up * (settings.jumpImpulse + rampJumpAugment), ForceMode.Impulse);
            consumeJump = true;
        }
    }

    private static void ApplyMovement(
        Rigidbody rb,
        InputFrame input,
        MeatballMovementSettings settings,
        bool grounded)
    {
        if (input.move == Vector2.zero) return;

        bool allowSprint = grounded && input.sprint;
        float speedCap = allowSprint
            ? settings.maxRunHorizontalSpeed
            : settings.maxWalkHorizontalSpeed;

        Vector3 horizontalVel = new Vector3(rb.linearVelocity.x, 0f, rb.linearVelocity.z);
        if (horizontalVel.magnitude >= speedCap) return;

        float forceMult = grounded ? 1f : settings.airControlFraction;
        Vector3 moveDir = new Vector3(input.move.x, 0f, input.move.y);
        rb.AddForce(moveDir * (settings.moveForce * forceMult), ForceMode.Force);
    }

    /// <summary>
    /// Ground overlap query that matches MeatballPhysicsController's prior inline logic.
    /// Also computes a ramp-jump augment: dot(horizontalVelocity, ramp.rampDirection) * ramp.jumpHeightAugment.
    /// </summary>
    public static bool ComputeGrounded(
        Vector3 position,
        Vector3 horizontalVelocity,
        MeatballMovementSettings settings,
        Collider[] hitBuffer,
        out float rampJumpAugment)
    {
        rampJumpAugment = 0f;
        if (settings == null) return false;

        Vector3 origin = position + Vector3.down * (settings.groundCheckRadius - 0.05f);

        int rampLayer = LayerMask.NameToLayer("Ramp");
        int combinedMask = settings.groundMask | (1 << rampLayer);

        int hitCount = Physics.OverlapSphereNonAlloc(
            origin,
            settings.groundCheckRadius,
            hitBuffer,
            combinedMask,
            QueryTriggerInteraction.Ignore);

        for (int i = 0; i < hitCount; i++)
        {
            if (hitBuffer[i].gameObject.layer != rampLayer) continue;
            if (!hitBuffer[i].TryGetComponent(out RampSettings ramp)) break;

            Vector2 horizontalXZ = new Vector2(horizontalVelocity.x, horizontalVelocity.z);
            rampJumpAugment = Vector2.Dot(horizontalXZ, ramp.rampDirection) * ramp.jumpHeightAugment;
            break;
        }

        return hitCount > 0;
    }
}
