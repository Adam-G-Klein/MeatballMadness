using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;
using Unity.Services.Multiplayer;
using System.Collections.Generic;
using Blocks.Common;
using Blocks.Sessions.Common;
using System.Threading.Tasks;
using System;

public class NetworkManagerBootstrap : MonoBehaviour
{
    [SerializeField] SessionSettings sessionSettings;
    public async Task<IHostSession> CreateSessionAsync(SessionOptions sessionOptions)
        {
            sessionOptions.Name = sessionSettings.sessionName;
            return await MultiplayerService.Instance.CreateSessionAsync(sessionOptions);
        }
    void Update()
    {
        // DEBUG ONLY
        var keyboard = Keyboard.current;
        if (keyboard == null) 
        {
            Debug.LogError("Couldn't find the keyboard!");
            return;
        }
        if (keyboard.digit1Key.wasPressedThisFrame)
            _ = CreateSessionAsync(sessionSettings.ToSessionOptions());
        else if (keyboard.digit2Key.wasPressedThisFrame)
            NetworkManager.Singleton.StartClient();
        else if (keyboard.digit3Key.wasPressedThisFrame)
            NetworkManager.Singleton.Shutdown();
    }
}
