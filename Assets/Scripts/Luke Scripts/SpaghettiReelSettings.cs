using UnityEngine;

/// <summary>
/// Shared tuning data for the spaghetti reel-in assist.
/// Keeps the feature separate from MeatballMovementSettings so no existing scripts need editing.
/// </summary>
[CreateAssetMenu(fileName = "SpaghettiReelSettings", menuName = "Meatball Madness/Spaghetti Reel Settings")]
public class SpaghettiReelSettings : ScriptableObject
{
    [Header("Pivot Pull Target")]
    [Tooltip("When the tether is wrapped around a pivot, the pull target is offset this many units away "
           + "from the contact surface along the outward collision normal. "
           + "A positive value moves the target off the obstacle surface; 0 uses the pivot point exactly.")]
    [Min(0f)]
    public float pivotNormalOffset = 0.5f;

    [Header("Pull")]
    [Tooltip("Base force used to pull the partner toward the player holding the reel key.")]
    public float reelForce = 55f;

    [Tooltip("Extra force added per meter once the tether is longer than the target distance.")]
    public float reelStretchForce = 18f;

    [Tooltip("When the pair is closer than this, reel force stops so players do not fully overlap.")]
    public float targetDistance = 2.5f;

    [Tooltip("Hard cap on how far the reel assist should care about. Prevents excessive force spikes.")]
    public float maxConsideredDistance = 16f;

    [Header("Damping")]
    [Tooltip("Damps partner velocity moving away from the reeling player along the tether axis.")]
    public float reelDamping = 8f;

    [Header("Vertical Lift")]
    [Tooltip("Extra upward lift applied when the partner is below the reeling player.")]
    public float upwardAssistForce = 35f;

    [Tooltip("Maximum vertical gap considered for upward assist.")]
    public float maxVerticalAssistHeight = 6f;

    [Header("Counter Pull On Self")]
    [Tooltip("Optional smaller opposite force applied to the player doing the reeling. "
           + "Set to 0 for no counter-pull.")]
    [Range(0f, 1f)]
    public float selfCounterForceFraction = 0.2f;

    [Header("Input")]
    [Tooltip("How often the owner resends hold-state to the server if unchanged.")]
    public float resendInterval = 0.15f;
}