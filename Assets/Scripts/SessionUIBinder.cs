using Blocks.Sessions;
using Blocks.Sessions.Common;
using Unity.Netcode;
using Unity.Services.Multiplayer;
using UnityEngine;
using UnityEngine.UIElements;

/// <summary>
/// Bridges the CreateSession building block to NGO.
///
/// Assign SessionSettings in the Inspector, then add this to the same
/// GameObject as UIDocument. On session creation the observer fires
/// SessionAdded: if we're the host it calls StartHost(), otherwise StartClient().
///
/// Requires a ServicesInitialization + PlayerAuthentication component (or the
/// UnityServicesWithName prefab) somewhere in the scene so that
/// MultiplayerService.Instance is ready before the player clicks CREATE.
/// </summary>
[RequireComponent(typeof(UIDocument))]
public class SessionUIBinder : MonoBehaviour
{
    [SerializeField] SessionSettings _sessionSettings;

    SessionObserver _sessionObserver;

    void Start()
    {
        // Wire SessionSettings into the UI element so the CREATE button works.
        var root = GetComponent<UIDocument>().rootVisualElement;
        root.Q<CreateSessionElement>().SessionSettings = _sessionSettings;

        // Watch for session state changes and start NGO accordingly.
        _sessionObserver = new SessionObserver(_sessionSettings.sessionType);
        _sessionObserver.SessionAdded += OnSessionAdded;
    }

    void OnDestroy()
    {
        if (_sessionObserver == null) return;
        _sessionObserver.SessionAdded -= OnSessionAdded;
        _sessionObserver.Dispose();
    }

    void OnSessionAdded(ISession session)
    {
        if (session is IHostSession)
            NetworkManager.Singleton.StartHost();
        else
            NetworkManager.Singleton.StartClient();
    }
}
