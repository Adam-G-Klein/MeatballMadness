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
[RequireComponent(typeof(NetworkObject))]
public class NetworkTick : NetworkBehaviour
{

    public static NetworkTick Instance { get; private set; }

    [SerializeField, Tooltip("Host broadcasts its tick to clients every N ticks.")]
    private int _syncIntervalTicks = 25; // ~0.5s at 50 Hz

    [SerializeField, Tooltip("Extra ticks added beyond the RTT estimate so client inputs " +
                             "reliably reach the host before it simulates the matching tick.")]
    private int _bufferTicks = 3;

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
    private void BroadcastTickClientRpc(ulong hostTick)
    {
        if (IsServer) return; // host already owns the canonical tick

        float rttSec = GetRttMs() / 1000f;
        float fdt = Time.fixedDeltaTime;
        int rttTicks = Mathf.RoundToInt(rttSec / fdt);

        ulong previous = _currentTick;
        _currentTick = hostTick + (ulong)(rttTicks + _bufferTicks);
        _lastSyncedHostTick = hostTick;
        _clientHasSynced = true;

        long delta = (long)_currentTick - (long)previous;
        Debug.Log($"[NetworkTick] CLIENT sync: host={hostTick} rttTicks={rttTicks} buffer={_bufferTicks} -> local={_currentTick} (snap delta={delta})");
    }

    private float GetRttMs()
    {
        var nm = NetworkManager.Singleton;
        if (nm == null || nm.NetworkConfig == null || nm.NetworkConfig.NetworkTransport == null)
            return 0f;
        return nm.NetworkConfig.NetworkTransport.GetCurrentRtt(nm.ServerClientId);
    }
}
