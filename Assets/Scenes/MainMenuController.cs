using Blocks.Sessions;
using Blocks.Sessions.Common;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;

[RequireComponent(typeof(UIDocument))]
public class MainMenuController : MonoBehaviour
{
    [SerializeField] SessionSettings sessionSettings;
    [SerializeField] QuickJoinSettings quickJoinSettings;

    QuickJoinViewModel m_QuickJoinViewModel;

    void OnEnable()
    {
        var doc = GetComponent<UIDocument>();
        var root = doc != null ? doc.rootVisualElement : null;
        if (root == null)
        {
            Debug.LogError("MainMenuController: UIDocument or rootVisualElement is null in OnEnable.");
            return;
        }

        var hostBtn = root.Q<Button>("host-session-button");
        var joinBtn = root.Q<Button>("join-session-button");
        var quickJoinBtn = root.Q<Button>("quick-join-session-button");

        if (hostBtn == null) Debug.LogError("MainMenuController: 'host-session-button' not found.");
        else hostBtn.clicked += OnHostSession;

        if (joinBtn == null) Debug.LogError("MainMenuController: 'join-session-button' not found.");
        else joinBtn.clicked += OnJoinSession;

        if (quickJoinBtn == null) Debug.LogError("MainMenuController: 'quick-join-session-button' not found.");
        else quickJoinBtn.clicked += OnQuickJoinSession;

        m_QuickJoinViewModel = new QuickJoinViewModel(sessionSettings != null ? sessionSettings.sessionType : null);
    }

    void OnDisable()
    {
        var doc = GetComponent<UIDocument>();
        var root = doc != null ? doc.rootVisualElement : null;
        if (root != null)
        {
            var hostBtn = root.Q<Button>("host-session-button");
            var joinBtn = root.Q<Button>("join-session-button");
            var quickJoinBtn = root.Q<Button>("quick-join-session-button");

            if (hostBtn != null) hostBtn.clicked -= OnHostSession;
            if (joinBtn != null) joinBtn.clicked -= OnJoinSession;
            if (quickJoinBtn != null) quickJoinBtn.clicked -= OnQuickJoinSession;
        }

        m_QuickJoinViewModel?.Dispose();
        m_QuickJoinViewModel = null;
    }

    void OnHostSession()
    {
        SceneManager.LoadScene("LukeScene");
    }

    void OnJoinSession()
    {
        SceneManager.LoadScene("JoinMenu");
    }

    void OnQuickJoinSession()
    {
        if (sessionSettings == null)
        {
            Debug.LogError("SessionSettings is not assigned on MainMenuController.");
            return;
        }
        if (!m_QuickJoinViewModel.AreMultiplayerServicesInitialized())
        {
            Debug.LogError("Multiplayer Services are not initialized. Add ServicesInitialization and PlayerAuthentication components to the scene.");
            return;
        }

        _ = m_QuickJoinViewModel.MatchmakeSessionAsync(
            quickJoinSettings != null ? quickJoinSettings.ToQuickJoinOptions() : new Unity.Services.Multiplayer.QuickJoinOptions(),
            sessionSettings.ToSessionOptions()
        );
    }
}
