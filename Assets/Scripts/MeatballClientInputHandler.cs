using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Owner-side input pipeline. Each FixedUpdate this component:
///   1. Reads local input (move/jump/sprint via InputSystem callbacks; reel via direct
///      Keyboard polling — the Reel action isn't in the .inputactions asset yet, so we
///      keep the existing polling approach used by SpaghettiReelAbility).
///   2. Tags the frame with the current <see cref="NetworkTick.Current"/> and appends it
///      to a ring buffer of recent frames.
///   3. Sends the last N frames via <see cref="SubmitInputsServerRpc"/>. Redundancy means
///      a dropped UDP packet doesn't cause the host to miss inputs — the next packet will
///      include the gap.
///
/// Disabled on non-owning clients.
/// </summary>
[DefaultExecutionOrder(-100)]
[RequireComponent(typeof(NetworkObject), typeof(MeatballPhysicsController))]
public class MeatballClientInputHandler : NetworkBehaviour, InputSystem_Actions.IPlayerActions
{
    [Header("Input pipeline")]
    [Tooltip("Number of recent input frames included in each redundant SubmitInputsServerRpc " +
             "call. Higher = more tolerant of packet loss, but more bandwidth per tick.")]
    [SerializeField] private int _redundancy = 4;

    [Tooltip("Size of the client-side input history ring buffer. Needs to cover at least " +
             "a second of FixedUpdate ticks so reconciliation (rollout step 8) can replay.")]
    [SerializeField] private int _historySize = 64;

    [Tooltip("Key polled each FixedUpdate for the reel (pull-partner) input.")]
    [SerializeField] private Key _reelKey = Key.E;

    [Tooltip("Log every SubmitInputsServerRpc call with the tick range it carries.")]
    [SerializeField] private bool _logInputDispatch;

    [Tooltip("Log every time the host receives an input packet.")]
    [SerializeField] private bool _logInputReceipt;

    private InputSystem_Actions _actions;
    private MeatballPhysicsController _controller;

    // Raw state written by callbacks / polled each FixedUpdate.
    private Vector2 _rawMove;
    private bool _jumpLatched;
    private bool _sprintHeld;

    // Tagged ring buffer of recent owner inputs. Preserved for reconciliation replay (step 8).
    private InputFrame[] _history;
    private int _historyHead;
    private int _historyCount;

    // Reusable send buffer so we don't allocate an array per FixedUpdate.
    private InputFrame[] _sendBuffer;

    private readonly NetworkVariable<Vector2> _networkCameraRelativeInput = new(
        default,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Owner
    );

    /// <summary>
    /// Current movement input rotated into world space relative to the camera.
    /// Owners compute it locally; non-owners read the synced network variable.
    /// </summary>
    public Vector2 CameraRelativeInput =>
        IsOwner ? ToCameraRelativeInput(_rawMove) : _networkCameraRelativeInput.Value;

    public bool SprintHeld => _sprintHeld;

    /// <summary>
    /// Returns the most recently pushed input frame, or a zeroed frame if no input has
    /// been produced yet. Used by <see cref="PredictedMeatball"/> to feed MeatballMotor
    /// each tick during owner-side prediction.
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
    /// Looks up the input frame that was produced on <paramref name="tick"/>. Returns
    /// false if the frame has already aged out of the ring buffer. Used during
    /// reconciliation to replay inputs from the authoritative tick forward.
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
        _actions = new InputSystem_Actions();
        _history = new InputFrame[Mathf.Max(8, _historySize)];
        _sendBuffer = new InputFrame[Mathf.Clamp(_redundancy, 1, _history.Length)];
        enabled = false; // stay off until OnNetworkSpawn confirms ownership
    }

    public override void OnNetworkSpawn()
    {
        if (!IsOwner)
            return;

        _actions.Player.AddCallbacks(this);
        _actions.Player.Enable();
        enabled = true;
        Debug.Log($"[MeatballInput] Owner input enabled (clientId={NetworkManager.LocalClientId}).");
    }

    public override void OnNetworkDespawn()
    {
        if (_actions == null) return;
        _actions.Player.RemoveCallbacks(this);
        _actions.Player.Disable();
        _actions.Dispose();
    }

    private void FixedUpdate()
    {
        ulong tick = NetworkTick.Instance != null ? NetworkTick.Instance.Current : 0;

        Vector2 worldMove = ToCameraRelativeInput(_rawMove);
        _networkCameraRelativeInput.Value = worldMove;

        bool jump = _jumpLatched;
        _jumpLatched = false;

        bool reel = PollReelKey();

        var frame = new InputFrame
        {
            tick = tick,
            move = worldMove,
            jump = jump,
            sprint = _sprintHeld,
            reel = reel,
        };

        PushHistory(frame);
        SendRedundantInputs();
    }

    private bool PollReelKey()
    {
        // Reel isn't in the InputSystem asset yet; poll the keyboard directly the same
        // way SpaghettiReelAbility has historically. Gamepad support can follow once the
        // action is added to the .inputactions asset.
        if (Keyboard.current == null) return false;
        return Keyboard.current[_reelKey].isPressed;
    }

    private void PushHistory(InputFrame frame)
    {
        _history[_historyHead] = frame;
        _historyHead = (_historyHead + 1) % _history.Length;
        if (_historyCount < _history.Length) _historyCount++;
    }

    private void SendRedundantInputs()
    {
        int count = Mathf.Min(_redundancy, _historyCount);
        if (count <= 0) return;

        // Fill _sendBuffer oldest-first so the server stores older entries first and the
        // newest stays intact if any frame is duplicated.
        for (int i = 0; i < count; i++)
        {
            int idx = (_historyHead - count + i + _history.Length) % _history.Length;
            _sendBuffer[i] = _history[idx];
        }

        // Trim to actual send size if redundancy > history.
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

        // On the host-owned meatball, NGO queues the local ServerRpc and dispatches it
        // between FixedUpdates. That would push the input into the controller AFTER the
        // tick it was tagged with has already been processed, so the host's own meatball
        // would never receive input. Call ReceiveInputs directly instead.
        if (IsServer)
        {
            // debug conditional to breakpoint when we hit a movement key
            if(payload[0].move.x != 0 || payload[0].move.y != 0)
            {
                Debug.Log($"Breakpoint: move={payload[0].move}");
            }
            _controller.ReceiveInputs(payload);
            if (_logInputDispatch)
                Debug.Log($"[MeatballInput] HOST-LOCAL ticks=[{payload[0].tick}..{payload[count - 1].tick}] (count={count})");
        }
        else
        {
            SubmitInputsServerRpc(payload);
            if (_logInputDispatch)
                Debug.Log($"[MeatballInput] Send ticks=[{payload[0].tick}..{payload[count - 1].tick}] (count={count})");
        }
    }

    /// <summary>
    /// Rotates the raw stick vector so "forward on stick" maps to "camera forward on XZ plane".
    /// The host has no knowledge of this client's camera, so we do the rotation here before sending.
    /// </summary>
    private static Vector2 ToCameraRelativeInput(Vector2 raw)
    {
        Camera cam = Camera.main;
        if (cam == null) return raw;

        Vector3 camForward = cam.transform.forward; camForward.y = 0f; camForward.Normalize();
        Vector3 camRight   = cam.transform.right;   camRight.y   = 0f; camRight.Normalize();

        Vector3 worldDir = camForward * raw.y + camRight * raw.x;
        return new Vector2(worldDir.x, worldDir.z);
    }

    [ServerRpc]
    private void SubmitInputsServerRpc(InputFrame[] frames)
    {
        _controller.ReceiveInputs(frames);
        if (_logInputReceipt && frames != null && frames.Length > 0)
            Debug.Log($"[MeatballInput] Host received ticks=[{frames[0].tick}..{frames[frames.Length - 1].tick}] " +
                      $"from client {OwnerClientId} (count={frames.Length})");
    }

    #region InputSystem_Actions.IPlayerActions

    public void OnMove(InputAction.CallbackContext context)
    {
        _rawMove = context.ReadValue<Vector2>();
    }

    public void OnLook(InputAction.CallbackContext context) { }

    public void OnJump(InputAction.CallbackContext context)
    {
        if (context.performed)
        {
            _jumpLatched = true;
        }
    }

    public void OnSprint(InputAction.CallbackContext context)
    {
        _sprintHeld = context.ReadValueAsButton();
    }

    public void OnToggleLooking(InputAction.CallbackContext context) { }

    public void OnMenu(InputAction.CallbackContext context) { }

    #endregion
}
