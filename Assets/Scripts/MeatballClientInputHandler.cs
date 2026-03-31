using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Reads local player input and forwards it to the host each FixedUpdate via ServerRpc.
/// Disabled on non-owning clients — only the owner reads input for their meatball.
/// </summary>
[RequireComponent(typeof(NetworkObject), typeof(MeatballPhysicsController))]
public class MeatballClientInputHandler : NetworkBehaviour, InputSystem_Actions.IPlayerActions
{
    private InputSystem_Actions _actions;
    private MeatballPhysicsController _controller;
    private bool _jumpQueued;

    private void Awake()
    {
        _controller = GetComponent<MeatballPhysicsController>();
        _actions = new InputSystem_Actions();
        //enabled = false; // stay off until OnNetworkSpawn confirms ownership
    }

    public override void OnNetworkSpawn()
    {
        if (!IsOwner)
            return;

        _actions.Player.AddCallbacks(this);
        _actions.Player.Enable();
        enabled = true;
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
        Vector2 rawMove = _actions.Player.Move.ReadValue<Vector2>();
        Vector2 move = ToCameraRelativeInput(rawMove);

        // Consume the queued jump — latch is set by the callback, cleared here.
        bool jump = _jumpQueued;
        _jumpQueued = false;

        // RPC work
        //SubmitInputServerRpc(move, jump);
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

// RPC work
/*
    [ServerRpc]
    private void SubmitInputServerRpc(Vector2 move, bool jump)
    {
        _controller.ReceiveInput(move, jump);
    }
    */

    #region InputSystem_Actions.IPlayerActions

    public void OnMove(InputAction.CallbackContext context)
    {
        if (context.performed)
            Debug.Log($"[Input] Move: {context.ReadValue<Vector2>()}");
    }

    public void OnLook(InputAction.CallbackContext context)
    {
        if (context.performed)
            Debug.Log($"[Input] Look: {context.ReadValue<Vector2>()}");
    }

    public void OnJump(InputAction.CallbackContext context)
    {
        if (context.performed)
        {
            Debug.Log("[Input] Jump");
            _jumpQueued = true;
        }
    }

    public void OnSprint(InputAction.CallbackContext context)
    {
        if (context.performed)
            Debug.Log("[Input] Sprint");
    }

    public void OnCrouch(InputAction.CallbackContext context)
    {
        if (context.performed)
            Debug.Log("[Input] Crouch");
    }

    public void OnAttack(InputAction.CallbackContext context)
    {
        if (context.performed)
            Debug.Log("[Input] Attack");
    }

    public void OnInteract(InputAction.CallbackContext context)
    {
        if (context.performed)
            Debug.Log("[Input] Interact");
    }

    public void OnPrevious(InputAction.CallbackContext context)
    {
        if (context.performed)
            Debug.Log("[Input] Previous");
    }

    public void OnNext(InputAction.CallbackContext context)
    {
        if (context.performed)
            Debug.Log("[Input] Next");
    }
    public void OnToggleLooking(InputAction.CallbackContext context) {}

    #endregion
}
