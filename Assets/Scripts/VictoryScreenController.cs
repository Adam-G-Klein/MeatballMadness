using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;

/// <summary>
/// Full-screen "Victory!" overlay shown to every player when the host declares victory
/// (see <see cref="LevelVictoryZone"/>). Starts hidden and is revealed via <see cref="Show"/>.
///
/// Mirrors the <see cref="PauseMenuController"/> standard: a UIDocument MonoBehaviour that
/// owns its own UXML (VictoryScreen.uxml) + USS (VictoryScreen.uss), runs its Q&lt;&gt;
/// queries in OnEnable, and unregisters its callbacks in OnDisable.
///
/// Setup:
/// 1. Add a GameObject to the gameplay scene with a UIDocument component.
/// 2. Assign VictoryScreen.uxml as the Source Asset (use a high Sort Order so it draws on top).
/// 3. Add this component.
/// </summary>
[RequireComponent(typeof(UIDocument))]
public class VictoryScreenController : MonoBehaviour
{
    /// <summary>Scene-wide singleton so <see cref="LevelVictoryZone"/> can reveal the screen.</summary>
    public static VictoryScreenController Instance { get; private set; }

    [Header("Scene")]
    [SerializeField] private string mainMenuSceneName = "MainMenu";

    VisualElement _root;
    Button _mainMenuButton;

    void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(this);
            return;
        }

        Instance = this;
    }

    void OnEnable()
    {
        var doc = GetComponent<UIDocument>();
        var root = doc != null ? doc.rootVisualElement : null;
        if (root == null)
        {
            Debug.LogError("VictoryScreenController: UIDocument or rootVisualElement is null.");
            return;
        }

        _root = root.Q<VisualElement>("victory-root") ?? root;
        _mainMenuButton = _root.Q<Button>("victory-main-menu-button");

        if (_mainMenuButton == null) Debug.LogError("VictoryScreenController: 'victory-main-menu-button' not found.");
        else _mainMenuButton.clicked += OnReturnToMainMenu;

        Hide();
    }

    void OnDisable()
    {
        if (_mainMenuButton != null) _mainMenuButton.clicked -= OnReturnToMainMenu;
    }

    void OnDestroy()
    {
        if (Instance == this)
            Instance = null;
    }

    /// <summary>Reveal the victory overlay and free the cursor so the button is clickable.</summary>
    public void Show()
    {
        if (_root == null) return;
        _root.RemoveFromClassList("hidden");
        SetCursorVisible(true);
    }

    void Hide()
    {
        if (_root == null) return;
        _root.AddToClassList("hidden");
    }

    static void SetCursorVisible(bool visible)
    {
        UnityEngine.Cursor.visible = visible;
        UnityEngine.Cursor.lockState = visible ? CursorLockMode.None : CursorLockMode.Locked;
    }

    async void OnReturnToMainMenu()
    {
        if (MeatballMultiplayerSessionManager.Instance != null)
            await MeatballMultiplayerSessionManager.Instance.LeaveSessionAsync();

        SceneManager.LoadScene(mainMenuSceneName);
    }
}
