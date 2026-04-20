using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Shared tick clock for netcode. Host increments a tick counter every FixedUpdate
/// (50 Hz per DynamicsManager.asset) and periodically broadcasts it to clients.
/// Clients run their own counter and snap to host + estimated RTT offset on each
/// sync so that inputs produced at clientTick=T arrive at the host in time to be
/// applied on hostTick=T.
///
/// Attach to a singleton NetworkObject that the host spawns (e.g. put it in the
/// list of network prefabs and NetworkManager.SpawnAsPlayerObject=false, then
/// instantiate on server startup), or drop it into the always-loaded scene and
/// let NetworkManager auto-spawn scene-placed NetworkObjects.
/// </summary>
[DefaultExecutionOrder(-200)]
[RequireComponent(typeof(NetworkObject))]
public class NetworkTick : NetworkBehaviour
{

    public static NetworkTick Instance { get; private set; }

    [SerializeField, Tooltip("Host broadcasts its tick to clients every N ticks.")]
    private int _syncIntervalTicks = 25; // ~0.5s at 50 Hz

    [SerializeField, Tooltip("Extra ticks added beyond the RTT estimate so client inputs " +
                             "reliably reach the host before it simulates the matching tick.")]
    private int _bufferTicks = 3;

    [SerializeField, Tooltip("Ignore per-sync drift within +/- this many ticks. Prevents constant " +
                             "one-tick churn when the client clock is already well aligned.")]
    private int _deadbandTicks = 1;

    [SerializeField, Tooltip("When drift is outside the deadband but smaller than the hard-snap " +
                             "threshold, move the client clock this many ticks toward the target " +
                             "per sync. Converges smoothly without re-keying input streams.")]
    private int _nudgeTicksPerSync = 1;

    [SerializeField, Tooltip("If per-sync drift is at or beyond this many ticks, stop nudging and " +
                             "hard-snap instead. Signals a frame hitch, RTT spike, or lost syncs.")]
    private int _hardSnapThresholdTicks = 8;

    [SerializeField, Tooltip("How often (seconds) both ends log their tick for verification.")]
    private float _logIntervalSeconds = 1f;

    private ulong _currentTick;
    private int _ticksSinceBroadcast;
    private float _nextLogTime;
    private bool _clientHasSynced;
    private ulong _lastSyncedHostTick;

    /// <summary>
    /// Authoritative tick on the host. On clients, an estimate of the host tick that
    /// will be current when this client's tick-T input arrives at the host.
    /// </summary>
    public ulong Current => _currentTick;

    public override void OnNetworkSpawn()
    {
        if (Instance != null && Instance != this)
        {
            Debug.LogWarning("[NetworkTick] Duplicate instance detected; destroying extra.");
            Destroy(gameObject);
            return;
        }

        Instance = this;
        _currentTick = 0;
        _ticksSinceBroadcast = 0;
        _nextLogTime = Time.time + _logIntervalSeconds;
        _clientHasSynced = false;

        Debug.Log($"[NetworkTick] Spawned (IsServer={IsServer}, IsClient={IsClient}, LocalClientId={NetworkManager.LocalClientId}).");
    }

    public override void OnNetworkDespawn()
    {
        if (Instance == this) Instance = null;
    }

    private void FixedUpdate()
    {
        if (!IsSpawned) return;

        _currentTick++;

        if (IsServer)
        {
            _ticksSinceBroadcast++;
            if (_ticksSinceBroadcast >= _syncIntervalTicks)
            {
                _ticksSinceBroadcast = 0;
                BroadcastTickClientRpc(_currentTick);
            }
        }

        if (Time.time >= _nextLogTime)
        {
            _nextLogTime = Time.time + _logIntervalSeconds;
            if (IsServer)
            {
                Debug.Log($"[NetworkTick] HOST tick={_currentTick}");
            }
            else if (_clientHasSynced)
            {
                int ahead = (int)((long)_currentTick - (long)_lastSyncedHostTick);
                Debug.Log($"[NetworkTick] CLIENT tick={_currentTick} (lastHostSync={_lastSyncedHostTick}, ahead={ahead}, rtt={GetRttMs():F0}ms)");
            }
            else
            {
                Debug.Log($"[NetworkTick] CLIENT tick={_currentTick} (no host sync yet)");
            }
        }
    }

    [ClientRpc]
    // every 25 ticks, align client ticks to prevent clock drift
    private void BroadcastTickClientRpc(ulong hostTick)
    {
        if (IsServer) return; // host already owns the canonical tick

        float rttSec = GetRttMs() / 1000f;
        float fdt = Time.fixedDeltaTime;
        int rttTicks = Mathf.RoundToInt(rttSec / fdt); // ticks that occurred during the round trip

        ulong target = hostTick + (ulong)(rttTicks + _bufferTicks);
        ulong previous = _currentTick;
        long targetDelta = (long)target - (long)previous;
        long absDelta = targetDelta < 0 ? -targetDelta : targetDelta;

        string action;
        if (!_clientHasSynced)
        {
            _currentTick = target;
            action = "INIT";
        }
        else if (absDelta <= _deadbandTicks)
        {
            action = "HOLD";
        }
        else if (absDelta >= _hardSnapThresholdTicks)
        {
            _currentTick = target;
            action = "SNAP";
            Debug.LogWarning($"[NetworkTick] Tick drift {targetDelta} exceeds hard-snap threshold " +
                             $"{_hardSnapThresholdTicks}; snapping. Check RTT stability and frame pacing.");
        }
        else
        {
            int step = Mathf.Min(Mathf.Max(1, _nudgeTicksPerSync), (int)absDelta);
            _currentTick = targetDelta > 0
                ? previous + (ulong)step
                : previous - (ulong)step;
            action = targetDelta > 0 ? $"NUDGE+{step}" : $"NUDGE-{step}";
        }

        _lastSyncedHostTick = hostTick;
        _clientHasSynced = true;

        long applied = (long)_currentTick - (long)previous;
        Debug.Log($"[NetworkTick] CLIENT sync: host={hostTick} rttTicks={rttTicks} buffer={_bufferTicks} " +
                  $"target={target} -> local={_currentTick} (targetDelta={targetDelta}, applied={applied}, action={action})");
    }

    private float GetRttMs()
    {
        var nm = NetworkManager.Singleton;
        if (nm == null || nm.NetworkConfig == null || nm.NetworkConfig.NetworkTransport == null)
            return 0f;
        return nm.NetworkConfig.NetworkTransport.GetCurrentRtt(NetworkManager.ServerClientId);
    }
}
