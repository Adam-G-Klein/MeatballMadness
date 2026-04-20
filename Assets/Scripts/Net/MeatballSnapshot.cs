using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Authoritative per-meatball physics state at a given tick, plus any collision impulses
/// the host applied during that tick. Clients route snapshots into two paths:
///  - Owner path (once client prediction is turned on in rollout step 7): reconcile
///    buffer for rollback replay. Collision impulses are replayed in sync.
///  - Non-owner path: drive the existing position/rotation interpolation.
///
/// Step 6 ships the struct + broadcast + non-owner interpolation. Collision impulses are
/// recorded and sent but unused by the client until step 8.
/// </summary>
public struct MeatballSnapshot : INetworkSerializable
{
    public ulong tick;
    public Vector3 position;
    public Quaternion rotation;
    public Vector3 velocity;
    public Vector3 angularVelocity;

    public CollisionImpulse[] collisions;

    public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
    {
        serializer.SerializeValue(ref tick);
        serializer.SerializeValue(ref position);
        serializer.SerializeValue(ref rotation);
        serializer.SerializeValue(ref velocity);
        serializer.SerializeValue(ref angularVelocity);

        int length = collisions?.Length ?? 0;
        serializer.SerializeValue(ref length);

        if (serializer.IsReader)
        {
            collisions = length == 0 ? System.Array.Empty<CollisionImpulse>() : new CollisionImpulse[length];
        }

        if (length > 0)
        {
            for (int i = 0; i < length; i++)
                collisions[i].NetworkSerialize(serializer);
        }
    }
}

/// <summary>
/// A single meatball-vs-meatball bounce impulse applied by the host during a given tick.
/// Broadcast in the sidecar of <see cref="MeatballSnapshot"/> so the owning client
/// (in step 8) can replay it at the matching tick during reconciliation.
/// </summary>
public struct CollisionImpulse : INetworkSerializable
{
    public ulong tick;
    public Vector3 impulse;
    public Vector3 contactPoint;
    public ulong otherClientId;

    public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
    {
        serializer.SerializeValue(ref tick);
        serializer.SerializeValue(ref impulse);
        serializer.SerializeValue(ref contactPoint);
        serializer.SerializeValue(ref otherClientId);
    }
}
