using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// All configuration the posterize quantization needs: the output <see cref="PosterizePalette"/>
/// plus optional "value snaps" that override whole lightness bands with a chosen color.
///
/// This is the single source of truth shared by both screen effects. PosterizeFeature
/// reads it directly; DitherSettings nests it (DitherFeature dithers, then quantizes
/// through the same settings), so the two effects always agree on the output colors.
///
/// Value snaps sample ONLY how light or dark a fragment is (its OkLab lightness),
/// never its hue — so a band can push an entire tonal range toward one color, e.g.
/// snapping the brightest highlights to green.
/// </summary>
[CreateAssetMenu(menuName = "Meatball Madness/Posterize Settings", fileName = "PosterizeSettings")]
public class PosterizeSettings : ScriptableObject
{
    /// <summary>Hard cap baked into the shaders (MAX_VALUE_SNAPS in PosterizeCommon.hlsl).</summary>
    public const int MaxValueSnaps = 8;

    /// <summary>
    /// One lightness band snapped to a fixed color. A fragment is snapped when its
    /// OkLab lightness falls within [<see cref="minValue"/>, <see cref="maxValue"/>].
    /// </summary>
    [System.Serializable]
    public struct ValueSnap
    {
        [Tooltip("Lower edge of the lightness band this snap covers. 0 = black, 1 = white " +
                 "(OkLab lightness). e.g. highlights ~0.8, shadows ~0.0–0.2.")]
        [Range(0f, 1f)] public float minValue;

        [Tooltip("Upper edge of the lightness band this snap covers. 0 = black, 1 = white.")]
        [Range(0f, 1f)] public float maxValue;

        [Tooltip("Color forced for fragments in this lightness band, overriding the palette match.")]
        public Color color;
    }

    [Tooltip("Palette supplying the output colors every fragment is quantized to (OkLab-nearest).")]
    public PosterizePalette palette;

    [Tooltip("Optional overrides that snap fragments in a lightness band to a fixed color, sampling " +
             "only how light/dark the fragment is. Evaluated after the palette match, so a snap wins " +
             "over the palette; earlier entries win where bands overlap. Entries beyond " + nameof(MaxValueSnaps) +
             " (8) are ignored. Add a high band (e.g. 0.8–1.0) in green to tint lighting highlights.")]
    public List<ValueSnap> valueSnaps = new List<ValueSnap>();

    private static readonly int PaletteOkLabId = Shader.PropertyToID("_PaletteOkLab");
    private static readonly int PaletteRgbId = Shader.PropertyToID("_PaletteRgb");
    private static readonly int PaletteCountId = Shader.PropertyToID("_PaletteCount");
    private static readonly int ValueSnapRgbId = Shader.PropertyToID("_ValueSnapRgb");
    private static readonly int ValueSnapRangeId = Shader.PropertyToID("_ValueSnapRange");
    private static readonly int ValueSnapCountId = Shader.PropertyToID("_ValueSnapCount");

    // SetVectorArray locks the GPU-side array length on first upload, so we always
    // send full-length buffers and let the count uniforms gate the shader loops.
    private static readonly Vector4[] LabBuffer = new Vector4[PosterizePalette.MaxColors];
    private static readonly Vector4[] RgbBuffer = new Vector4[PosterizePalette.MaxColors];
    private static readonly Vector4[] SnapRgbBuffer = new Vector4[MaxValueSnaps];
    private static readonly Vector4[] SnapRangeBuffer = new Vector4[MaxValueSnaps];

    /// <summary>True if there is a palette with at least one color to quantize to.</summary>
    public bool HasPalette => palette != null && palette.colors != null && palette.colors.Count > 0;

    /// <summary>
    /// Uploads the palette (as linear RGB and pre-converted OkLab) and the value snaps
    /// to <paramref name="material"/>. Both effects call this, so any PosterizeSettings
    /// change is honoured identically by posterize and dither.
    /// </summary>
    public void Apply(Material material)
    {
        int count = HasPalette ? Mathf.Min(palette.colors.Count, PosterizePalette.MaxColors) : 0;
        for (int i = 0; i < count; i++)
        {
            // Inspector colors are sRGB; the render target is linear, so both the
            // OkLab match and the output color use the linear value.
            Color linear = palette.colors[i].linear;
            LabBuffer[i] = OkLabUtil.LinearSrgbToOkLab(linear);
            RgbBuffer[i] = new Vector4(linear.r, linear.g, linear.b, 1f);
        }
        material.SetVectorArray(PaletteOkLabId, LabBuffer);
        material.SetVectorArray(PaletteRgbId, RgbBuffer);
        material.SetInteger(PaletteCountId, count);

        int snapCount = valueSnaps != null ? Mathf.Min(valueSnaps.Count, MaxValueSnaps) : 0;
        for (int i = 0; i < snapCount; i++)
        {
            ValueSnap snap = valueSnaps[i];
            Color linear = snap.color.linear;
            SnapRgbBuffer[i] = new Vector4(linear.r, linear.g, linear.b, 1f);
            // Tolerate an inverted band (min > max) by ordering the edges here so the
            // shader's inclusive range test still matches.
            float lo = Mathf.Min(snap.minValue, snap.maxValue);
            float hi = Mathf.Max(snap.minValue, snap.maxValue);
            SnapRangeBuffer[i] = new Vector4(lo, hi, 0f, 0f);
        }
        material.SetVectorArray(ValueSnapRgbId, SnapRgbBuffer);
        material.SetVectorArray(ValueSnapRangeId, SnapRangeBuffer);
        material.SetInteger(ValueSnapCountId, snapCount);
    }
}
