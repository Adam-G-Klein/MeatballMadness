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
    IHostSession hostSession;

    public async Task<IHostSession> CreateSessionAsync(SessionOptions sessionOptions)
        {
            sessionOptions.Name = sessionSettings.sessionName;
            hostSession = await MultiplayerService.Instance.CreateSessionAsync(sessionOptions);
            Debug.Log($"Session created with id {hostSession.Id}, join code: {hostSession.Code}");
            return hostSession;
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
