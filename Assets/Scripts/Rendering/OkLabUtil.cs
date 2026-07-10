using UnityEngine;

/// <summary>
/// CPU-side OkLab conversion (Björn Ottosson, 2020 — https://bottosson.github.io/posts/oklab/).
/// Must stay numerically identical to LinearSrgbToOkLab in Assets/Shaders/OkLab.hlsl:
/// palettes are converted here once per frame so the shaders only convert the
/// fragment color, never the palette.
/// </summary>
public static class OkLabUtil
{
    /// <summary>
    /// Converts a LINEAR sRGB color to OkLab. Pass Color.linear when starting
    /// from an inspector color (inspector colors are authored in sRGB).
    /// </summary>
    public static Vector3 LinearSrgbToOkLab(Color linearColor)
    {
        float r = Mathf.Max(0f, linearColor.r);
        float g = Mathf.Max(0f, linearColor.g);
        float b = Mathf.Max(0f, linearColor.b);

        // Linear sRGB -> LMS cone response.
        float l = 0.4122214708f * r + 0.5363325363f * g + 0.0514459929f * b;
        float m = 0.2119034982f * r + 0.6806995451f * g + 0.1073969566f * b;
        float s = 0.0883024619f * r + 0.2817188376f * g + 0.6299787005f * b;

        // Cube-root nonlinearity.
        float lp = Mathf.Pow(l, 1f / 3f);
        float mp = Mathf.Pow(m, 1f / 3f);
        float sp = Mathf.Pow(s, 1f / 3f);

        // LMS' -> OkLab (L, a, b).
        return new Vector3(
            0.2104542553f * lp + 0.7936177850f * mp - 0.0040720468f * sp,
            1.9779984951f * lp - 2.4285922050f * mp + 0.4505937099f * sp,
            0.0259040371f * lp + 0.7827717662f * mp - 0.8086757660f * sp);
    }
}
