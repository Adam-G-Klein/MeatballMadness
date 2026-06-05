using System.Threading.Tasks;
using Blocks.Sessions;
using UnityEngine;
using UnityEngine.UIElements;

[RequireComponent(typeof(UIDocument))]
public class MainMenuController : MonoBehaviour
{
    QuickJoinViewModel m_QuickJoinViewModel;
    VisualElement m_MainMenuView;
    Button m_HostBtn;
    Button m_JoinBtn;
    Button m_QuickJoinBtn;
    Button m_OptionsBtn;
    Button m_CreditsBtn;
    Button m_ExitBtn;
    Label m_QuickJoinStatusLabel;
    JoinCodeView m_JoinCodeView;

    void OnEnable()
    {
        var doc = GetComponent<UIDocument>();
        var root = doc != null ? doc.rootVisualElement : null;
        if (root == null)
        {
            Debug.LogError("MainMenuController: UIDocument or rootVisualElement is null in OnEnable.");
            return;
        }

        m_MainMenuView = root.Q<VisualElement>("main-menu-view");
        m_HostBtn = root.Q<Button>("host-session-button");
        m_JoinBtn = root.Q<Button>("join-session-button");
        m_QuickJoinBtn = root.Q<Button>("quick-join-session-button");
        m_OptionsBtn = root.Q<Button>("options-button");
        m_CreditsBtn = root.Q<Button>("credits-button");
        m_ExitBtn = root.Q<Button>("exit-button");
        m_QuickJoinStatusLabel = root.Q<Label>("quick-join-status-label");

        if (m_MainMenuView == null) Debug.LogError("MainMenuController: 'main-menu-view' not found.");

        if (m_HostBtn == null) Debug.LogError("MainMenuController: 'host-session-button' not found.");
        else m_HostBtn.clicked += OnHostSession;

        if (m_JoinBtn == null) Debug.LogError("MainMenuController: 'join-session-button' not found.");
        else m_JoinBtn.clicked += OnJoinSession;

        if (m_QuickJoinBtn == null) Debug.LogError("MainMenuController: 'quick-join-session-button' not found.");
        else m_QuickJoinBtn.clicked += OnQuickJoinSession;

        if (m_OptionsBtn == null) Debug.LogError("MainMenuController: 'options-button' not found.");
        else m_OptionsBtn.clicked += OnOptions;

        if (m_CreditsBtn == null) Debug.LogError("MainMenuController: 'credits-button' not found.");
        else m_CreditsBtn.clicked += OnCredits;

        if (m_ExitBtn == null) Debug.LogError("MainMenuController: 'exit-button' not found.");
        else m_ExitBtn.clicked += OnExit;

        m_JoinCodeView = new JoinCodeView(root);
        m_JoinCodeView.Submitted += OnJoinCodeSubmitted;
        m_JoinCodeView.BackPressed += OnJoinCodeBack;

        UnityEngine.Cursor.visible = true;
        UnityEngine.Cursor.lockState = CursorLockMode.None;

        ShowMainMenu();

        m_QuickJoinViewModel = new QuickJoinViewModel(null);
    }

    void OnDisable()
    {
        if (m_HostBtn != null) m_HostBtn.clicked -= OnHostSession;
        if (m_JoinBtn != null) m_JoinBtn.clicked -= OnJoinSession;
        if (m_QuickJoinBtn != null) m_QuickJoinBtn.clicked -= OnQuickJoinSession;
        if (m_OptionsBtn != null) m_OptionsBtn.clicked -= OnOptions;
        if (m_CreditsBtn != null) m_CreditsBtn.clicked -= OnCredits;
        if (m_ExitBtn != null) m_ExitBtn.clicked -= OnExit;

        if (m_JoinCodeView != null)
        {
            m_JoinCodeView.Submitted -= OnJoinCodeSubmitted;
            m_JoinCodeView.BackPressed -= OnJoinCodeBack;
            m_JoinCodeView.Dispose();
            m_JoinCodeView = null;
        }

        m_QuickJoinViewModel?.Dispose();
        m_QuickJoinViewModel = null;
        m_MainMenuView = null;
        m_HostBtn = null;
        m_JoinBtn = null;
        m_QuickJoinBtn = null;
        m_OptionsBtn = null;
        m_CreditsBtn = null;
        m_ExitBtn = null;
        m_QuickJoinStatusLabel = null;
    }

    void ShowMainMenu()
    {
        if (m_MainMenuView != null) m_MainMenuView.style.display = DisplayStyle.Flex;
        m_JoinCodeView?.Hide();
    }

    void ShowJoinCodeMenu()
    {
        if (m_MainMenuView != null) m_MainMenuView.style.display = DisplayStyle.None;
        m_JoinCodeView?.Show();
    }

    void OnHostSession()
    {
        MeatballMultiplayerSessionManager.Instance.StartHostFlow();
    }

    void OnJoinSession()
    {
        ShowJoinCodeMenu();
    }

    void OnQuickJoinSession()
    {
        _ = QuickJoinAsync();
    }

    void OnOptions()
    {
        Debug.Log("MainMenuController: Options pressed (not wired up yet).");
    }

    void OnCredits()
    {
        Debug.Log("MainMenuController: Credits pressed (not wired up yet).");
    }

    void OnExit()
    {
#if UNITY_EDITOR
        UnityEditor.EditorApplication.isPlaying = false;
#else
        Application.Quit();
#endif
    }

    void OnJoinCodeSubmitted(string code)
    {
        _ = JoinByCodeAsync(code);
    }

    void OnJoinCodeBack()
    {
        ShowMainMenu();
    }

    async Task QuickJoinAsync()
    {
        m_QuickJoinBtn.SetEnabled(false);
        m_QuickJoinStatusLabel.text = "Looking for session...";

        try
        {
            await MeatballMultiplayerSessionManager.Instance.QuickJoinAsync();
            m_QuickJoinStatusLabel.text = "Joined!";
        }
        catch (System.Exception e)
        {
            m_QuickJoinStatusLabel.text = $"Failed: {e.Message}";
            m_QuickJoinBtn.SetEnabled(true);
        }
    }

    async Task JoinByCodeAsync(string code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            m_JoinCodeView?.SetStatus("Enter a join code.");
            return;
        }

        m_JoinCodeView?.SetSubmitEnabled(false);
        m_JoinCodeView?.SetStatus("Joining session...");

        try
        {
            await MeatballMultiplayerSessionManager.Instance.JoinByCodeAsync(code.Trim());
            m_JoinCodeView?.SetStatus("Joined!");
        }
        catch (System.Exception e)
        {
            m_JoinCodeView?.SetStatus($"Failed: {e.Message}");
            m_JoinCodeView?.SetSubmitEnabled(true);
        }
    }
}
