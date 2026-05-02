using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Per-meatball networked input router. Lives on every PlayerMeatball alongside
/// <see cref="MeatballPhysicsController"/> and <see cref="MeatballClientInputHandler"/>.
///
/// Responsibilities:
///   1. Owns the canonical <see cref="InputFrame"/> ring buffer for a meatball on
///      every machine. Owner pushes new frames in via <see cref="EmitOwnerFrame"/>;
///      the host pushes frames it receives via the redundant ServerRpc; non-owner
///      clients push frames they receive via the broadcast ClientRpc(s).
///   2. Routes owner-side frames to the host (RPC) or applies them directly when the
///      host is the owner. Replaces the host short-circuit + ServerRpc that previously
///      lived in MeatballClientInputHandler.
///   3. Re-broadcasts inputs from the host out to non-owner clients so they can action
///      cosmetic events (currently just emote). Two flavors:
///        - Event-bearing broadcast: fires immediately when a frame contains a jump
///          or emote bit.
///        - Periodic state broadcast: fires every N host ticks with the most recent
///          frame so non-owner views always have a fresh `move` direction for
///          ChefAnimator's spine lean (replaces the old _networkCameraRelativeInput
///          NetworkVariable on MeatballClientInputHandler).
///   4. Actions cosmetic input on every machine: plays the emote on this machine's
///      ChefAnimator, deduped by tick so periodic state broadcasts of an old
///      emote-bearing frame don't re-fire it.
///
/// Physics application is still server-only — <see cref="MeatballPhysicsController.ReceiveInputs"/>
/// is invoked from the dispatcher whenever a frame lands on the server.
/// </summary>
[DefaultExecutionOrder(-90)] // after MeatballClientInputHandler (-100), before controller (0)
[RequireComponent(typeof(NetworkObject), typeof(MeatballPhysicsController))]
public class MeatballInputDispatcher : NetworkBehaviour
{
    [Header("Send pipeline")]
    [Tooltip("Number of recent input frames included in each redundant SubmitFrameRedundantServerRpc " +
             "call. Higher = more tolerant of packet loss, but more bandwidth per tick.")]
    [SerializeField] private int _redundancy = 4;

    [Tooltip("Size of the input history ring buffer. Needs to cover at least a second of " +
             "FixedUpdate ticks so PredictedMeatball reconciliation can replay.")]
    [SerializeField] private int _historySize = 64;

    [Header("State broadcast")]
    [Tooltip("Host re-broadcasts the most recently received frame to non-owner clients " +
             "every N host ticks. Keeps remote views' `move` direction fresh for ChefAnimator's " +
             "spine lean. Event-bearing frames (jump/emote) bypass this and broadcast immediately.")]
    [SerializeField] private int _broadcastIntervalTicks = 2;

    [Header("Logging")]
    [SerializeField] private bool _logDispatch;
    [SerializeField] private bool _logReceipt;
    [SerializeField] private bool _logBroadcast;

    private MeatballPhysicsController _controller;
    private MeatballChefController _chef;

    // Tick-keyed ring buffer of recent frames known to this machine.
    private InputFrame[] _history;
    private int _historyHead;
    private int _historyCount;

    // Reusable buffer for redundant ServerRpc sends.
    private InputFrame[] _sendBuffer;

    // Last tick at which we played an emote on this machine. Sentinel = never.
    private const ulong NoEmoteTick = ulong.MaxValue;
    private ulong _lastActionedEmoteTick = NoEmoteTick;

    // Periodic state broadcast bookkeeping (host only).
    private ulong _lastStateBroadcastTick;

    /// <summary>
    /// Most-recent input frame known to this machine. Owner: their own latest emit.
    /// Host (for a remote-owned ball): the latest frame received via SubmitFrameRedundantServerRpc.
    /// Non-owner clients: the latest frame received via either ClientRpc broadcast.
    /// Returns a zero frame at tick 0 until any frame arrives.
    /// </summary>
    public InputFrame LatestFrame
    {
        get
        {
            if (_historyCount == 0) return InputFrame.Zero(0);
            int idx = (_historyHead - 1 + _history.Length) % _history.Length;
            return _history[idx];
        }
    }

    /// <summary>
    /// Look up the input frame stored for <paramref name="tick"/>. Returns false if the
    /// frame has aged out. Used by <see cref="PredictedMeatball"/> to replay locally
    /// buffered owner inputs during reconciliation.
    /// </summary>
    public bool TryGetFrame(ulong tick, out InputFrame frame)
    {
        for (int i = 0; i < _historyCount; i++)
        {
            int idx = (_historyHead - 1 - i + _history.Length) % _history.Length;
            if (_history[idx].tick == tick)
            {
                frame = _history[idx];
                return true;
            }
        }
        frame = InputFrame.Zero(tick);
        return false;
    }

    private void Awake()
    {
        _controller = GetComponent<MeatballPhysicsController>();
        _chef = GetComponent<MeatballChefController>();
        _history = new InputFrame[Mathf.Max(8, _historySize)];
        _sendBuffer = new InputFrame[Mathf.Clamp(_redundancy, 1, _history.Length)];
    }

    /// <summary>
    /// Called by the owner's <see cref="MeatballClientInputHandler"/> each FixedUpdate
    /// after building the local input frame. Pushes the frame into history, actions it
    /// locally (controller on host, emote on every owner), and routes it to the host
    /// (direct call when the host is the owner, ServerRpc otherwise).
    /// </summary>
    public void EmitOwnerFrame(InputFrame frame)
    {
        PushHistory(frame);
        ActionFrameLocally(frame);

        bool eventBearing = IsEventBearing(frame);

        if (IsServer)
        {
            // Host-owned: physics already applied via ActionFrameLocally → ReceiveInputs.
            // Just rebroadcast cosmetic events to non-owner clients.
            if (eventBearing)
            {
                BroadcastEventClientRpc(frame);
                if (_logBroadcast)
                    Debug.Log($"[MeatballInputDispatcher] HOST-OWNED event broadcast tick={frame.tick} " +
                              $"jump={frame.jump} emote={frame.emote}");
            }
            return;
        }

        // Client-owned: send redundant frames to the host so packet loss doesn't drop a tick.
        SendRedundantInputs();
    }

    private void SendRedundantInputs()
    {
        int count = Mathf.Min(_redundancy, _historyCount);
        if (count <= 0) return;

        for (int i = 0; i < count; i++)
        {
            int idx = (_historyHead - count + i + _history.Length) % _history.Length;
            _sendBuffer[i] = _history[idx];
        }

        InputFrame[] payload;
        if (count < _sendBuffer.Length)
        {
            payload = new InputFrame[count];
            System.Array.Copy(_sendBuffer, payload, count);
        }
        else
        {
            payload = _sendBuffer;
        }

        SubmitFrameRedundantServerRpc(payload);
        if (_logDispatch)
            Debug.Log($"[MeatballInputDispatcher] Send ticks=[{payload[0].tick}..{payload[count - 1].tick}] (count={count})");
    }

    [ServerRpc]
    private void SubmitFrameRedundantServerRpc(InputFrame[] frames)
    {
        if (frames == null || frames.Length == 0) return;

        // Apply each frame the host hasn't seen yet. Duplicates are harmless because
        // ActionFrameLocally → ReceiveInputs uses a tick-keyed dict that ignores
        // duplicate keys, and the emote dedupe key is _lastActionedEmoteTick.
        for (int i = 0; i < frames.Length; i++)
        {
            var f = frames[i];
            if (HistoryContainsTick(f.tick)) continue;
            PushHistory(f);
            ActionFrameLocally(f);
        }

        // Use the freshest frame to decide whether to fire an event broadcast.
        InputFrame newest = frames[frames.Length - 1];
        if (IsEventBearing(newest))
        {
            BroadcastEventClientRpc(newest);
            if (_logBroadcast)
                Debug.Log($"[MeatballInputDispatcher] HOST event broadcast tick={newest.tick} " +
                          $"jump={newest.jump} emote={newest.emote} owner={OwnerClientId}");
        }

        if (_logReceipt)
            Debug.Log($"[MeatballInputDispatcher] Host received ticks=[{frames[0].tick}..{frames[frames.Length - 1].tick}] " +
                      $"from owner={OwnerClientId} (count={frames.Length})");
    }

    [ClientRpc(RequireOwnership = false)]
    private void BroadcastEventClientRpc(InputFrame frame)
    {
        // Skip on host (already actioned during EmitOwnerFrame or SubmitFrameRedundantServerRpc)
        // and on the owner (already actioned during EmitOwnerFrame on its own machine).
        if (IsServer || IsOwner) return;

        PushHistory(frame);
        ActionFrameLocally(frame);
    }

    [ClientRpc(RequireOwnership = false)]
    private void BroadcastStateClientRpc(InputFrame frame)
    {
        if (IsServer || IsOwner) return;

        // Periodic refresh: store so LatestFrame stays fresh for ChefAnimator / lean direction.
        // Do NOT call ActionFrameLocally — emote bit may still be set on a frame from N ticks
        // ago that we already actioned via the event broadcast.
        if (HistoryContainsTick(frame.tick)) return;
        PushHistory(frame);
    }

    private void FixedUpdate()
    {
        // Periodic state broadcast: only the host fans out, and only when there's something
        // to fan out (history non-empty).
        if (!IsServer) return;
        if (_historyCount == 0) return;

        ulong tick = NetworkTick.Instance != null ? NetworkTick.Instance.Current : 0;
        if (tick - _lastStateBroadcastTick < (ulong)Mathf.Max(1, _broadcastIntervalTicks)) return;

        _lastStateBroadcastTick = tick;
        InputFrame latest = LatestFrame;
        BroadcastStateClientRpc(latest);
        if (_logBroadcast)
            Debug.Log($"[MeatballInputDispatcher] HOST state broadcast tick={latest.tick} " +
                      $"move={latest.move} sprint={latest.sprint} owner={OwnerClientId}");
    }

    /// <summary>
    /// Apply a frame's effects on this machine: physics on the host, cosmetic emote
    /// everywhere. Called from the owner-side emit path, the host's ServerRpc receipt,
    /// and the non-owner ClientRpc receipt — same routine in all three places.
    /// </summary>
    private void ActionFrameLocally(InputFrame frame)
    {
        if (IsServer)
        {
            // ReceiveInputs is no-op on non-server, so the IsServer gate is also defensive
            // (matches the existing controller contract).
            _controller.ReceiveInputs(new[] { frame });
        }

        if (frame.emote && (_lastActionedEmoteTick == NoEmoteTick || frame.tick > _lastActionedEmoteTick))
        {
            _chef?.ChefAnimator?.PlayEmote();
            _lastActionedEmoteTick = frame.tick;
        }
    }

    private static bool IsEventBearing(InputFrame f) => f.jump || f.emote;

    private void PushHistory(InputFrame frame)
    {
        _history[_historyHead] = frame;
        _historyHead = (_historyHead + 1) % _history.Length;
        if (_historyCount < _history.Length) _historyCount++;
    }

    private bool HistoryContainsTick(ulong tick)
    {
        for (int i = 0; i < _historyCount; i++)
        {
            int idx = (_historyHead - 1 - i + _history.Length) % _history.Length;
            if (_history[idx].tick == tick) return true;
        }
        return false;
    }
}
