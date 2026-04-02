using UnityEngine;

/// <summary>
/// Place on any collider in the "Ramp" layer.
/// MeatballPhysicsController reads these values when a meatball lands on or moves
/// across this surface to augment the jump height proportionally to approach speed.
/// </summary>
public class RampSettings : MonoBehaviour
{
    [Tooltip("Multiplier applied to the dot product of meatball velocity and ramp direction "
           + "to produce extra jump impulse. Higher values = bigger boost.")]
    public float jumpHeightAugment = 5f;

    [Tooltip("XZ direction of the ramp (e.g. (0,1) = forward along Z). "
           + "The dot product of the meatball's horizontal velocity and this vector "
           + "determines how much of the boost is applied.")]
    public Vector2 rampDirection = Vector2.up;
}
