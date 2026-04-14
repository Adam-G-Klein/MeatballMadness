using UnityEngine;

/// <summary>
/// Tuning data for meatball-vs-meatball bounce impulses.
/// Applied host-side only by MeatballPhysicsController on collision between two meatballs.
/// </summary>
[CreateAssetMenu(fileName = "MeatballBounceSettings", menuName = "Meatball Madness/Meatball Bounce Settings")]
public class MeatballBounceSettings : ScriptableObject
{
    [Tooltip("Base impulse (N·s) applied to each meatball along the separation axis on contact, "
           + "before the velocity-scaled contribution.")]
    [Min(0f)]
    public float baseBounceImpulse = 5f;

    [Tooltip("Multiplier applied to the relative approach speed (m/s) between the two meatballs, "
           + "added on top of baseBounceImpulse.")]
    [Min(0f)]
    public float velocityScale = 1f;

    [Tooltip("Upper cap (N·s) on the total bounce impulse applied to each meatball.")]
    [Min(0f)]
    public float maxBounceImpulse = 40f;
}
