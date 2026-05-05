using UnityEngine;

/// <summary>
/// Replaces the real-player input path on a PlayerMeatball variant used in the main-menu
/// animatic. Exposes one serialized field per player input; values are pushed into the
/// local <see cref="MeatballClientInputHandler"/> via its Inject* methods, the same fields
/// the real-input callbacks would write to. Continuous inputs (move/sprint/reel) are
/// forwarded every Update so a Timeline keyframe back to default also propagates;
/// latched inputs (jump/emote) are edge-triggered on false→true so a sustained Timeline
/// value doesn't refire them every physics tick.
///
/// Also flips <see cref="MeatballInputDispatcher.IsTimelineDriven"/> so other scripts
/// gated on IsServer/IsOwner can opt-in to running for a meatball that never goes through
/// OnNetworkSpawn in this scene.
/// </summary>
[DefaultExecutionOrder(-200)] // before MeatballClientInputHandler (-100) and dispatcher (-90)
[RequireComponent(typeof(MeatballClientInputHandler), typeof(MeatballInputDispatcher))]
public class TimelineDrivenMeatball : MonoBehaviour
{
    [Header("Simulated inputs (drive these from Timeline)")]
    public Vector2 simulatedMove;
    public bool simulatedJump;
    public bool simulatedSprint;
    public bool simulatedReel;
    public bool simulatedEmote;

    [Header("Skin Assignment")]
    [SerializeField, Tooltip("Stand-in client ID used by ChefAnimator to pick a skin when " +
        "no real NGO owner exists (e.g. main menu). Set this to a different value on each " +
        "TimelineDrivenMeatball in the scene so the skin assignment produces distinct chefs.")]
    private ulong _dummyClientId;

    public ulong DummyClientId => _dummyClientId;

    [Header("Local NetworkTick")]
    [SerializeField, Tooltip("Spawned locally when NetworkTick.Instance is null (e.g. main menu, " +
        "no NetworkManager). Without this the handler / dispatcher / controller would all see " +
        "tick=0 every FixedUpdate and inputs would never advance past the first stored frame.")]
    private NetworkTick _networkTickPrefab;

    private MeatballClientInputHandler _handler;
    private MeatballInputDispatcher _dispatcher;

    // Previous-frame values for edge-detecting latched inputs.
    private bool _prevSimulatedJump;
    private bool _prevSimulatedEmote;

    void Awake()
    {
        _handler = GetComponent<MeatballClientInputHandler>();
        _dispatcher = GetComponent<MeatballInputDispatcher>();
        _dispatcher.IsTimelineDriven = true;

        EnsureLocalNetworkTick();
    }

    /// <summary>
    /// If no NetworkTick.Instance exists yet (no NGO host in this scene), instantiate the
    /// configured prefab and call ActivateAsLocalTick so its FixedUpdate ticks normally via
    /// the local-tick code path instead of the IsSpawned gate.
    /// </summary>
    private void EnsureLocalNetworkTick()
    {
        if (NetworkTick.Instance != null) return;
        if (_networkTickPrefab == null)
        {
            Debug.LogError("[TimelineDrivenMeatball] NetworkTick.Instance is null and no NetworkTick " +
                           "prefab is assigned — input ticks will be stuck at 0.", this);
            return;
        }

        NetworkTick tickInstance = Instantiate(_networkTickPrefab);
        tickInstance.ActivateAsLocalTick();
    }

    void Update()
    {
        _handler.InjectMove(simulatedMove);
        _handler.InjectSprint(simulatedSprint);
        _handler.InjectReel(simulatedReel);

        if (simulatedJump && !_prevSimulatedJump) _handler.InjectJump();
        if (simulatedEmote && !_prevSimulatedEmote) _handler.InjectEmote();

        _prevSimulatedJump = simulatedJump;
        _prevSimulatedEmote = simulatedEmote;
    }
}
