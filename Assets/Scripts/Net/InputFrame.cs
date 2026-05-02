using Unity.Netcode;
using UnityEngine;

/// <summary>
/// One tick of owner input, tagged with the <see cref="NetworkTick.Current"/> value it
/// was produced on. The owning client builds one of these each FixedUpdate and hands it
/// to its <see cref="MeatballInputDispatcher"/>, which both routes it to the host and
/// stores it for local consumers (PredictedMeatball reconcile replay; ChefAnimator
/// spine lean via <c>LatestFrame.move</c>).
///
/// Host stores these in a tick-keyed dictionary and applies the matching one each tick;
/// missing ticks decay to "repeat last input" then to zeroed input after a cap (see
/// <see cref="MeatballPhysicsController"/>). The dispatcher rebroadcasts event-bearing
/// frames immediately and the latest frame periodically so non-owner clients can
/// action cosmetic events (emote) and read the current movement direction.
/// </summary>
public struct InputFrame : INetworkSerializable
{
    public ulong tick;

    /// <summary>Camera-relative XZ movement on the owner's machine. Already rotated
    /// into world space by <see cref="MeatballClientInputHandler"/> before being
    /// stamped here, so the host (which has no knowledge of the owner's camera)
    /// can apply it directly.</summary>
    public Vector2 move;
    public bool jump;
    public bool sprint;
    public bool reel;
    public bool emote;

    public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
    {
        serializer.SerializeValue(ref tick);
        serializer.SerializeValue(ref move);
        serializer.SerializeValue(ref jump);
        serializer.SerializeValue(ref sprint);
        serializer.SerializeValue(ref reel);
        serializer.SerializeValue(ref emote);
    }

    public static InputFrame Zero(ulong tick) => new InputFrame
    {
        tick = tick,
        move = Vector2.zero,
        jump = false,
        sprint = false,
        reel = false,
        emote = false,
    };
}
