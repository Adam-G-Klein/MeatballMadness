using System.Collections;
using System.Threading.Tasks;
using Blocks.Sessions;
using Blocks.Sessions.Common;
using Unity.Netcode;
using Unity.Services.Multiplayer;
using UnityEngine;
using UnityEngine.SceneManagement;

public class MeatballMultiplayerSessionManager : MonoBehaviour
{
    public static MeatballMultiplayerSessionManager Instance { get; private set; }
    [SerializeField]
    private GameObject _networkTickPrefab;

    [SerializeField]
    private GameObject _playerSkinAssignmentPrefab;

    [SerializeField]
    private GameObject _playerNameAssignmentPrefab;
    [SerializeField] SessionSettings sessionSettings;
    [SerializeField] QuickJoinSettings quickJoinSettings;

    IHostSession m_HostSession;
    ISession m_ClientSession;

    /// <summary>
    /// Current lobby join code. Returns the host's session code when hosting,
    /// or the joined session's code when a client. Null/empty if not in a session.
    /// </summary>
    public string JoinCode => m_HostSession?.Code ?? m_ClientSession?.Code;

    void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;
        DontDestroyOnLoad(gameObject);
    }

    void Start()
    {

    }
    void OnDestroy()
    {
        if (Instance == this)
            Instance = null;
    }

    /// <summary>
    /// Loads LukeScene, then creates a host session once it finishes loading.
    /// </summary>
    public void StartHostFlow()
    {
        SceneManager.sceneLoaded += OnLukeSceneLoaded;
        SceneManager.LoadScene("LukeScene");
    }

    async void OnLukeSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        if (scene.name != "LukeScene") return;
        SceneManager.sceneLoaded -= OnLukeSceneLoaded;
        await CreateSessionAsync();

        var skinObj = Instantiate(_playerSkinAssignmentPrefab);
        skinObj.GetComponent<NetworkObject>().Spawn(destroyWithScene: true);

        var nameObj = Instantiate(_playerNameAssignmentPrefab);
        nameObj.GetComponent<NetworkObject>().Spawn(destroyWithScene: true);

        StartCoroutine(HostPostInitCoroutine());
    }

    private IEnumerator HostPostInitCoroutine()
    {
        yield return PlayerSkinAssignment.Instance.Initialize();
        yield return PlayerNameAssignment.Instance.Initialize();

        GameObject networkTick = Instantiate(_networkTickPrefab);
        networkTick.GetComponent<NetworkObject>().Spawn(destroyWithScene: true);
    }

    public async Task<IHostSession> CreateSessionAsync()
    {
        var sessionOptions = sessionSettings != null
            ? sessionSettings.ToSessionOptions()
            : new SessionOptions();

        if (sessionSettings != null)
            sessionOptions.Name = sessionSettings.sessionName;

        m_HostSession = await MultiplayerService.Instance.CreateSessionAsync(sessionOptions);
        Debug.Log($"Session created — id: {m_HostSession.Id}, join code: {m_HostSession.Code}");
        return m_HostSession;
    }

    public async Task QuickJoinAsync()
    {
        var quickJoinOptions = quickJoinSettings != null
            ? quickJoinSettings.ToQuickJoinOptions()
            : new QuickJoinOptions();

        var sessionOptions = sessionSettings != null
            ? sessionSettings.ToSessionOptions()
            : new SessionOptions();

        m_ClientSession = await MultiplayerService.Instance.MatchmakeSessionAsync(quickJoinOptions, sessionOptions);
        Debug.Log($"Joined session — id: {m_ClientSession.Id}, join code: {m_ClientSession.Code}");

        StartCoroutine(ClientPostJoinCoroutine());
    }

    public async Task LeaveSessionAsync()
    {
        try
        {
            if (m_HostSession != null)
            {
                await m_HostSession.LeaveAsync();
                m_HostSession = null;
            }
            else if (m_ClientSession != null)
            {
                await m_ClientSession.LeaveAsync();
                m_ClientSession = null;
            }
        }
        catch (System.Exception e)
        {
            Debug.LogWarning($"LeaveSessionAsync failed: {e.Message}");
        }

        if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsListening)
            NetworkManager.Singleton.Shutdown();
    }

    private IEnumerator ClientPostJoinCoroutine()
    {
        yield return new WaitUntil(() => PlayerSkinAssignment.Instance != null);
        yield return PlayerSkinAssignment.Instance.Initialize();
        yield return new WaitUntil(() => PlayerNameAssignment.Instance != null);
        yield return PlayerNameAssignment.Instance.Initialize();
    }
}
