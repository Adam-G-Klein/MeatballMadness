using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;

public class NetworkManagerBootstrap : MonoBehaviour
{
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
            NetworkManager.Singleton.StartHost();
        else if (keyboard.digit2Key.wasPressedThisFrame)
            NetworkManager.Singleton.StartClient();
        else if (keyboard.digit3Key.wasPressedThisFrame)
            NetworkManager.Singleton.Shutdown();
    }
}
