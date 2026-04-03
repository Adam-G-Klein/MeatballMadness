using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Adds a hold-to-reel ability to a meatball player without modifying any existing scripts.
///
/// Intended for 2-player play:
/// - Hold the reel key to pull the other player toward you
/// - If the other player is below you, a vertical assist helps lift them upward
/// - While reeling, a looping SFX plays and stops immediately when reeling ends
/// - Plays a splat SFX whenever this player hits any surface
///
/// This runs server-authoritatively:
/// - owner reads input locally
/// - owner sends reeling state to server
/// - server applies physics forces to both rigidbodies
/// - server replicates reel state so all clients can play/stop the reel loop
/// - server detects collisions and tells all clients to play the splat sound
/// </summary>
[RequireComponent(typeof(NetworkObject))]
[RequireComponent(typeof(MeatballPhysicsController))]
public class SpaghettiReelAbility : NetworkBehaviour
{
    [Header("References")]
    [SerializeField] private SpaghettiReelSettings reelSettings;

    [Tooltip("Optional explicit Rigidbody reference. If left empty, uses MeatballPhysicsController.Rigidbody.")]
    [SerializeField] private Rigidbody playerRigidbody;

    [Header("Input")]
    [SerializeField] private Key reelKey = Key.E;

    [Header("Reel Audio")]
    [Tooltip("AudioSource used for the looping reel sound. If left empty, the script will try to use an AudioSource on this object.")]
    [SerializeField] private AudioSource reelLoopAudioSource;

    [Tooltip("Optional clip to assign automatically to the loop AudioSource.")]
    [SerializeField] private AudioClip reelLoopClip;

    [Tooltip("Volume to force on the reel loop AudioSource when assigned automatically.")]
    [Range(0f, 1f)]
    [SerializeField] private float reelLoopVolume = 1f;

    [Header("Impact Audio")]
    [Tooltip("AudioSource used for splat impact sounds. If left empty, the script will try to use a second AudioSource on this object. If none is found, it falls back to the reel AudioSource.")]
    [SerializeField] private AudioSource impactAudioSource;

    [Tooltip("Clip played whenever this player hits any surface.")]
    [SerializeField] private AudioClip impactSplatClip;

    [Tooltip("Volume of the splat one-shot.")]
    [Range(0f, 1f)]
    [SerializeField] private float impactSplatVolume = 1f;

    [Tooltip("Small cooldown to prevent one collision from firing the sound many times in rapid succession.")]
    [SerializeField] private float impactSfxCooldown = 0.08f;

    [Header("Debug")]
    [SerializeField] private bool debugLogs;

    private MeatballPhysicsController _controller;
    private bool _localHeldLastFrame;
    private float _nextResendTime;
    private bool _audioWasPlaying;

    private bool _serverIsReeling;
    private float _lastImpactSfxTime = -999f;

    private readonly NetworkVariable<bool> _replicatedIsReeling = new NetworkVariable<bool>(
        false,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);

    private void Awake()
    {
        _controller = GetComponent<MeatballPhysicsController>();

        if (playerRigidbody == null && _controller != null)
        {
            playerRigidbody = _controller.Rigidbody;
        }

        if (reelLoopAudioSource == null)
        {
            reelLoopAudioSource = GetComponent<AudioSource>();
        }

        if (impactAudioSource == null)
        {
            AudioSource[] foundSources = GetComponents<AudioSource>();

            if (foundSources.Length >= 2)
            {
                if (foundSources[0] == reelLoopAudioSource)
                    impactAudioSource = foundSources[1];
                else
                    impactAudioSource = foundSources[0];
            }
            else
            {
                impactAudioSource = reelLoopAudioSource;
            }
        }

        ConfigureLoopAudioSource();
    }

    public override void OnNetworkSpawn()
    {
        _localHeldLastFrame = false;
        _nextResendTime = 0f;
        _serverIsReeling = false;
        _lastImpactSfxTime = -999f;

        _replicatedIsReeling.OnValueChanged += OnReplicatedReelStateChanged;

        RefreshLoopAudio(_replicatedIsReeling.Value);
    }

    public override void OnNetworkDespawn()
    {
        _replicatedIsReeling.OnValueChanged -= OnReplicatedReelStateChanged;

        if (IsServer)
        {
            _serverIsReeling = false;
            _replicatedIsReeling.Value = false;
        }

        StopLoopAudioImmediate();
    }

    private void Update()
    {
        if (!IsOwner)
            return;

        if (Keyboard.current == null)
            return;

        bool held = Keyboard.current[reelKey].isPressed;

        bool shouldSend =
            held != _localHeldLastFrame ||
            Time.unscaledTime >= _nextResendTime;

        if (shouldSend)
        {
            SetReelingServerRpc(held);
            _localHeldLastFrame = held;
            _nextResendTime = Time.unscaledTime + GetResendInterval();
        }
    }

    private void FixedUpdate()
    {
        if (!IsServer)
            return;

        if (!_serverIsReeling)
            return;

        if (_controller == null || playerRigidbody == null || reelSettings == null)
            return;

        SpaghettiReelAbility partner = FindPartner();
        if (partner == null)
            return;

        Rigidbody partnerRb = partner.GetPartnerRigidbody();
        if (partnerRb == null)
            return;

        ApplyReelForces(playerRigidbody, partnerRb);
    }

    private void OnCollisionEnter(Collision collision)
    {
        if (!IsServer)
            return;

        if (!IsSpawned)
            return;

        if (collision == null || collision.contactCount <= 0)
            return;

        if (Time.time - _lastImpactSfxTime < impactSfxCooldown)
            return;

        _lastImpactSfxTime = Time.time;
        PlayImpactSfxClientRpc();
    }

    [ServerRpc]
    private void SetReelingServerRpc(bool isHeld)
    {
        _serverIsReeling = isHeld;
        _replicatedIsReeling.Value = isHeld;

        if (debugLogs)
        {
            Debug.Log($"[SpaghettiReelAbility] Client {OwnerClientId} reeling: {_serverIsReeling}");
        }
    }

    private float GetResendInterval()
    {
        if (reelSettings == null)
            return 0.15f;

        return Mathf.Max(0.05f, reelSettings.resendInterval);
    }

    private Rigidbody GetPartnerRigidbody()
    {
        if (playerRigidbody != null)
            return playerRigidbody;

        if (_controller != null)
            return _controller.Rigidbody;

        return null;
    }

    private SpaghettiReelAbility FindPartner()
    {
        SpaghettiReelAbility nearest = null;
        float nearestSqrDistance = float.MaxValue;

        for (int i = 0; i < MeatballPhysicsController.ServerInstances.Count; i++)
        {
            MeatballPhysicsController otherController = MeatballPhysicsController.ServerInstances[i];

            if (otherController == null)
                continue;

            if (otherController == _controller)
                continue;

            if (!otherController.TryGetComponent(out SpaghettiReelAbility otherAbility))
                continue;

            float sqrDistance = (otherController.transform.position - transform.position).sqrMagnitude;
            if (sqrDistance < nearestSqrDistance)
            {
                nearestSqrDistance = sqrDistance;
                nearest = otherAbility;
            }
        }

        return nearest;
    }

    private void ApplyReelForces(Rigidbody selfRb, Rigidbody partnerRb)
    {
        Vector3 fromPartnerToSelf = selfRb.position - partnerRb.position;
        float distance = fromPartnerToSelf.magnitude;

        if (distance <= 0.0001f)
            return;

        float clampedDistance = Mathf.Min(distance, reelSettings.maxConsideredDistance);
        Vector3 axis = fromPartnerToSelf / distance;

        float extraStretch = Mathf.Max(0f, clampedDistance - reelSettings.targetDistance);
        if (extraStretch <= 0f)
            return;

        float pullForce = reelSettings.reelForce + (extraStretch * reelSettings.reelStretchForce);

        float separatingSpeed = Vector3.Dot(partnerRb.linearVelocity - selfRb.linearVelocity, -axis);
        float dampingForce = Mathf.Max(0f, separatingSpeed) * reelSettings.reelDamping;

        Vector3 totalPull = axis * (pullForce + dampingForce);

        float verticalGap = selfRb.position.y - partnerRb.position.y;
        if (verticalGap > 0f)
        {
            float assist01 = Mathf.Clamp01(verticalGap / Mathf.Max(0.01f, reelSettings.maxVerticalAssistHeight));
            totalPull += Vector3.up * (reelSettings.upwardAssistForce * assist01);
        }

        partnerRb.AddForce(totalPull, ForceMode.Force);

        float selfCounterFraction = Mathf.Clamp01(reelSettings.selfCounterForceFraction);
        if (selfCounterFraction > 0f)
        {
            selfRb.AddForce(-totalPull * selfCounterFraction, ForceMode.Force);
        }
    }

    private void ConfigureLoopAudioSource()
    {
        if (reelLoopAudioSource == null)
            return;

        if (reelLoopClip != null)
        {
            reelLoopAudioSource.clip = reelLoopClip;
        }

        reelLoopAudioSource.loop = true;
        reelLoopAudioSource.playOnAwake = false;
        reelLoopAudioSource.volume = reelLoopVolume;
    }

    private void OnReplicatedReelStateChanged(bool previousValue, bool newValue)
    {
        RefreshLoopAudio(newValue);
    }

    private void RefreshLoopAudio(bool shouldBePlaying)
    {
        if (reelLoopAudioSource == null)
            return;

        if (shouldBePlaying)
        {
            if (!_audioWasPlaying)
            {
                if (reelLoopAudioSource.clip != null)
                {
                    reelLoopAudioSource.Play();
                    _audioWasPlaying = true;
                }
            }
        }
        else
        {
            if (_audioWasPlaying)
            {
                reelLoopAudioSource.Stop();
                _audioWasPlaying = false;
            }
        }
    }

    private void StopLoopAudioImmediate()
    {
        if (reelLoopAudioSource != null && reelLoopAudioSource.isPlaying)
        {
            reelLoopAudioSource.Stop();
        }

        _audioWasPlaying = false;
    }

    [ClientRpc]
    private void PlayImpactSfxClientRpc()
    {
        if (impactAudioSource == null)
            return;

        if (impactSplatClip == null)
            return;

        impactAudioSource.PlayOneShot(impactSplatClip, impactSplatVolume);
    }

#if UNITY_EDITOR
    private void OnDrawGizmosSelected()
    {
        if (!Application.isPlaying)
            return;

        if (_controller == null)
            return;

        if (!IsServer)
            return;

        SpaghettiReelAbility partner = FindPartner();
        if (partner == null)
            return;

        Gizmos.color = Color.yellow;
        Gizmos.DrawLine(transform.position, partner.transform.position);
    }
#endif
}