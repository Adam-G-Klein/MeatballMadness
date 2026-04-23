using UnityEngine;

/// <summary>
/// Pure function that returns the bounce impulse to apply to a meatball given its state
/// and a peer's state. Used by both the host (<see cref="MeatballPhysicsController"/> on
/// <c>OnCollisionEnter</c>, authoritative) and the owning client (<see cref="PredictedMeatball"/>
/// for speculative local bounces). Keeping the math in one place prevents host and client
/// from drifting apart on tuning changes.
///
/// The returned impulse is signed for THIS meatball — the peer receives the negation
/// on the host's authoritative path.
/// </summary>
public static class MeatballBounceResolver
{
    public static Vector3 Compute(
        Vector3 myPos, Vector3 myVel,
        Vector3 otherPos, Vector3 otherVel,
        MeatballBounceSettings settings)
    {
        Vector3 separation = myPos - otherPos;
        if (separation.sqrMagnitude < 1e-6f) separation = Vector3.right;
        Vector3 dir = separation.normalized;

        float approachSpeed = Mathf.Max(0f, Vector3.Dot(otherVel - myVel, dir));

        float magnitude = Mathf.Min(
            settings.baseBounceImpulse + settings.velocityScale * approachSpeed,
            settings.maxBounceImpulse);

        return dir * magnitude;
    }
}
