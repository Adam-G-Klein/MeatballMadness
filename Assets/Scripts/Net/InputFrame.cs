using Unity.Netcode;
using UnityEngine;

/// <summary>
/// One tick of owner input, tagged with the <see cref="NetworkTick.Current"/> value it
/// was produced on. The client appends one of these to a ring buffer each FixedUpdate
/// and sends the last few frames redundantly so a dropped UDP packet doesn't leave the
/// host with a gap.
///
/// Host stores these in a tick-keyed dictionary and applies the matching one each tick;
/// missing ticks decay to "repeat last input" then to zeroed input after a cap
/// (see MeatballPhysicsController).
/// </summary>
public struct InputFrame : INetworkSerializable
{
    public ulong tick;
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
