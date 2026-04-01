using System.Threading.Tasks;
using Blocks.Sessions;
using UnityEngine;
using UnityEngine.UIElements;

[RequireComponent(typeof(UIDocument))]
public class MainMenuController : MonoBehaviour
{
    QuickJoinViewModel m_QuickJoinViewModel;
    Button m_QuickJoinBtn;
    Label m_QuickJoinStatusLabel;

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
        m_QuickJoinBtn = root.Q<Button>("quick-join-session-button");
        m_QuickJoinStatusLabel = root.Q<Label>("quick-join-status-label");

        if (hostBtn == null) Debug.LogError("MainMenuController: 'host-session-button' not found.");
        else hostBtn.clicked += OnHostSession;

        if (joinBtn == null) Debug.LogError("MainMenuController: 'join-session-button' not found.");
        else joinBtn.clicked += OnJoinSession;

        if (m_QuickJoinBtn == null) Debug.LogError("MainMenuController: 'quick-join-session-button' not found.");
        else m_QuickJoinBtn.clicked += OnQuickJoinSession;

        m_QuickJoinViewModel = new QuickJoinViewModel(null);
    }

    void OnDisable()
    {
        var doc = GetComponent<UIDocument>();
        var root = doc != null ? doc.rootVisualElement : null;
        if (root != null)
        {
            var hostBtn = root.Q<Button>("host-session-button");
            var joinBtn = root.Q<Button>("join-session-button");

            if (hostBtn != null) hostBtn.clicked -= OnHostSession;
            if (joinBtn != null) joinBtn.clicked -= OnJoinSession;
            if (m_QuickJoinBtn != null) m_QuickJoinBtn.clicked -= OnQuickJoinSession;
        }

        m_QuickJoinViewModel?.Dispose();
        m_QuickJoinViewModel = null;
        m_QuickJoinBtn = null;
        m_QuickJoinStatusLabel = null;
    }

    void OnHostSession()
    {
        MeatballMultiplayerSessionManager.Instance.StartHostFlow();
    }

    void OnJoinSession()
    {
        UnityEngine.SceneManagement.SceneManager.LoadScene("JoinMenu");
    }

    void OnQuickJoinSession()
    {
        _ = QuickJoinAsync();
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
}
