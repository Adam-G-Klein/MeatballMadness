using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Adds a hold-to-reel ability to a meatball player without modifying any existing scripts.
///
/// Intended for 2-player play:
/// - Hold the reel key to pull the other player toward you
/// - Only grounded players are allowed to reel
/// - If the other player is below you, a vertical assist helps lift them upward
/// - While reeling, a looping SFX plays and stops immediately when reeling ends
/// - Plays a splat SFX whenever this player hits any surface
///
/// This runs server-authoritatively:
/// - owner reads input locally
/// - owner sends reeling state to server
/// - server decides whether reeling is allowed
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

    [Header("Grounded Reel Restriction")]
    [Tooltip("Layers treated as ground for deciding whether this player is allowed to reel.")]
    [SerializeField] private LayerMask reelGroundMask = ~0;

    [Tooltip("Radius of the ground-check sphere used for reel eligibility.")]
    [SerializeField] private float reelGroundCheckRadius = 0.6f;

    [Tooltip("Vertical offset downward from transform.position for the reel ground check.")]
    [SerializeField] private float reelGroundCheckDownOffset = 0.5f;

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
    private TetherForce _tether;
    private bool _audioWasPlaying;

    private bool _serverIsReeling;
    private float _lastImpactSfxTime = -999f;

    private static readonly Collider[] _groundHits = new Collider[8];

    private readonly NetworkVariable<bool> _replicatedIsReeling = new NetworkVariable<bool>(
        false,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);

    private void Awake()
    {
        _controller = GetComponent<MeatballPhysicsController>();
        _tether = GetComponent<TetherForce>();

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
            SetServerReelingState(false);
        }

        StopLoopAudioImmediate();
    }

    private void FixedUpdate()
    {
        if (!IsServer)
            return;

        UpdateServerReelStateFromGrounding();

        if (!_serverIsReeling)
            return;

        if (_controller == null || playerRigidbody == null || reelSettings == null)
            return;

        for (int i = 0; i < MeatballPhysicsController.ServerInstances.Count; i++)
        {
            MeatballPhysicsController otherController = MeatballPhysicsController.ServerInstances[i];

            if (otherController == null)
                continue;

            if (otherController == _controller)
                continue;

            Rigidbody partnerRb = otherController.Rigidbody;
            if (partnerRb == null)
                continue;

            TetherForce partnerTether = otherController.GetComponent<TetherForce>();
            ApplyReelForces(playerRigidbody, partnerRb, partnerTether);
        }
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

    private void UpdateServerReelStateFromGrounding()
    {
        bool requested = _controller != null && _controller.ReelHeld;
        bool grounded = IsGroundedForReeling();
        bool shouldBeReeling = requested && grounded;
        SetServerReelingState(shouldBeReeling);

        if (debugLogs && shouldBeReeling != _serverIsReeling)
        {
            Debug.Log($"[SpaghettiReelAbility] Client {OwnerClientId} reel state requested={requested} grounded={grounded} active={shouldBeReeling}");
        }
    }

    private void SetServerReelingState(bool shouldBeReeling)
    {
        if (_serverIsReeling == shouldBeReeling)
            return;

        _serverIsReeling = shouldBeReeling;
        _replicatedIsReeling.Value = shouldBeReeling;

        if (debugLogs)
        {
            Debug.Log($"[SpaghettiReelAbility] Client {OwnerClientId} reel active changed to: {_serverIsReeling}");
        }
    }

    private bool IsGroundedForReeling()
    {
        Vector3 origin = transform.position + Vector3.down * reelGroundCheckDownOffset;

        int hitCount = Physics.OverlapSphereNonAlloc(
            origin,
            reelGroundCheckRadius,
            _groundHits,
            reelGroundMask,
            QueryTriggerInteraction.Ignore);

        return hitCount > 0;
    }

    private void ApplyReelForces(Rigidbody selfRb, Rigidbody partnerRb, TetherForce partnerTether)
    {
        // Pull the partner toward the nearest tether pivot (e.g. a ledge contact point) rather
        // than straight toward this meatball. This matches TetherForce's own force direction:
        // the partner is pulled along the first segment of its wrapped path. Falls back to
        // pulling directly toward this meatball when the rope hasn't wrapped around anything.
        Vector3 pullTarget;
        if (partnerTether != null && _tether != null)
            pullTarget = partnerTether.GetFirstPathTargetWithNormalOffset(_tether, reelSettings.pivotNormalOffset);
        else
            pullTarget = selfRb.position;

        Vector3 fromPartnerToTarget = pullTarget - partnerRb.position;
        float distance = fromPartnerToTarget.magnitude;

        if (distance <= 0.0001f)
            return;

        float clampedDistance = Mathf.Min(distance, reelSettings.maxConsideredDistance);
        Vector3 axis = fromPartnerToTarget / distance;

        float extraStretch = Mathf.Max(0f, clampedDistance - reelSettings.targetDistance);
        if (extraStretch <= 0f)
            return;

        float pullForce = reelSettings.reelForce + (extraStretch * reelSettings.reelStretchForce);

        // Damp partner velocity moving away from the pull target. The pivot is on static
        // geometry so we measure the partner's absolute velocity rather than relative.
        float separatingSpeed = Vector3.Dot(partnerRb.linearVelocity, -axis);
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
        Vector3 reelGroundCheckPos = transform.position + Vector3.down * reelGroundCheckDownOffset;
        Gizmos.color = Color.cyan;
        Gizmos.DrawWireSphere(reelGroundCheckPos, reelGroundCheckRadius);

        if (!Application.isPlaying)
            return;

        if (_controller == null)
            return;

        if (!IsServer)
            return;

        Gizmos.color = Color.yellow;
        for (int i = 0; i < MeatballPhysicsController.ServerInstances.Count; i++)
        {
            MeatballPhysicsController otherController = MeatballPhysicsController.ServerInstances[i];
            if (otherController == null || otherController == _controller)
                continue;
            Gizmos.DrawLine(transform.position, otherController.transform.position);
        }
    }
#endif
}