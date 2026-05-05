using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Owner-side input sampler. Each FixedUpdate this component reads the current local
/// input state (move/jump/sprint/reel/emote via InputSystem callbacks), packages it
/// into an <see cref="InputFrame"/> tagged with the current <see cref="NetworkTick.Current"/>,
/// and hands it to the local <see cref="MeatballInputDispatcher"/> via
/// <see cref="MeatballInputDispatcher.EmitOwnerFrame"/>.
///
/// The dispatcher owns history, routing (host vs ServerRpc), and rebroadcasts; this
/// handler is just the input source. Disabled on non-owning clients.
/// </summary>
[DefaultExecutionOrder(-100)]
[RequireComponent(typeof(NetworkObject), typeof(MeatballInputDispatcher))]
public class MeatballClientInputHandler : NetworkBehaviour, InputSystem_Actions.IPlayerActions
{
    private InputSystem_Actions _actions;
    private MeatballInputDispatcher _dispatcher;

    // Raw state written by callbacks / read each FixedUpdate.
    private Vector2 _rawMove;
    private bool _jumpLatched;
    private bool _sprintHeld;
    private bool _reelHeld;
    private bool _emoteLatched;

    private void Awake()
    {
        _dispatcher = GetComponent<MeatballInputDispatcher>();
        _actions = new InputSystem_Actions();
        enabled = false; // stay off until OnNetworkSpawn confirms ownership
        EnableIfTimelineDriven();
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

        bool jump = _jumpLatched;
        _jumpLatched = false;

        bool emote = _emoteLatched;
        _emoteLatched = false;

        if (_rawMove.sqrMagnitude > 0.001f)
        {
            Debug.Log("[MeatballClientInputHandler] _rawMove is non-zero:", this);
        }
   

        var frame = new InputFrame
        {
            tick = tick,
            move = ToCameraRelativeInput(_rawMove),
            jump = jump,
            sprint = _sprintHeld,
            reel = _reelHeld,
            emote = emote,
        };

        _dispatcher.EmitOwnerFrame(frame);
    }

    /// <summary>
    /// Rotates the raw stick vector so "forward on stick" maps to "camera forward on XZ plane".
    /// The host has no knowledge of this client's camera, so we do the rotation here before
    /// the frame leaves this machine.
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

    /// <summary>
    /// Called by <see cref="TimelineDrivenMeatball"/> in its Awake. Verifies the component
    /// is actually present on this GameObject before bypassing the OnNetworkSpawn/IsOwner
    /// gate, so nothing else can flip the handler on by calling this. Doesn't subscribe to
    /// real InputSystem callbacks — synthetic input via Inject* only.
    /// </summary>
    public void EnableIfTimelineDriven()
    {
        if (GetComponent<TimelineDrivenMeatball>() == null) return;
        enabled = true;
    }

    public void InjectMove(Vector2 raw) => _rawMove = raw;
    public void InjectJump() => _jumpLatched = true;
    public void InjectSprint(bool held) => _sprintHeld = held;
    public void InjectReel(bool held) => _reelHeld = held;
    public void InjectEmote() => _emoteLatched = true;

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

    public void OnReel(InputAction.CallbackContext context)
    {
        _reelHeld = context.ReadValueAsButton();
    }

    public void OnEmote(InputAction.CallbackContext context)
    {
        if (!context.performed) return;
        _emoteLatched = true;
    }

    #endregion
}
