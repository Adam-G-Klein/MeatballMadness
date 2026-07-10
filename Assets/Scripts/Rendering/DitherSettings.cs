using UnityEngine;

/// <summary>
/// All configuration for the ordered-dithering screen effect (DitherFeature).
/// The output colors — palette plus value snaps — come from a nested
/// PosterizeSettings, so the dither and posterize effects share one source of
/// truth and dithering honours every posterize setting.
///
/// Note: dithering quantizes through those same settings (it is posterization
/// plus a threshold pattern), so disable PosterizeFeature while DitherFeature is
/// active — running both is redundant.
/// </summary>
[CreateAssetMenu(menuName = "Meatball Madness/Dither Settings", fileName = "DitherSettings")]
public class DitherSettings : ScriptableObject
{
    /// <summary>Bayer threshold-matrix size. Bigger = more distinct intensity steps, finer gradients.</summary>
    public enum BayerSize
    {
        Bayer2x2 = 1, // enum value = recursion levels used by the shader
        Bayer4x4 = 2,
        Bayer8x8 = 3,
    }

    [Tooltip("Posterize settings supplying the output colors — palette and value snaps. Assign the same " +
             "asset PosterizeFeature uses so both effects match.")]
    public PosterizeSettings posterize;

    [Tooltip("Size of the Bayer threshold matrix. 8x8 gives the smoothest gradients, 2x2 the coarsest/boldest pattern.")]
    public BayerSize bayerSize = BayerSize.Bayer8x8;

    [Tooltip("Amplitude of the dither perturbation added before palette quantization. 0 = plain posterize " +
             "(no dithering); higher values blend between palette colors over larger areas but add texture " +
             "to flat surfaces. Start around 0.1–0.2.")]
    [Range(0f, 1f)]
    public float spread = 0.15f;

    [Tooltip("Screen pixels per dither-pattern cell. 1 = per-pixel pattern; higher values make the pattern " +
             "chunkier/retro without lowering the actual render resolution.")]
    [Range(1, 8)]
    public int patternScale = 1;
}
