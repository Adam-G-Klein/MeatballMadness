using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// A set of output colors for the posterize screen effect (PosterizeFeature).
/// Every pixel on screen is snapped to whichever of these colors is closest in
/// OkLab space. Make several assets to swap looks per level.
/// </summary>
[CreateAssetMenu(menuName = "Meatball Madness/Posterize Palette", fileName = "PosterizePalette")]
public class PosterizePalette : ScriptableObject
{
    /// <summary>Hard cap baked into the shader (MAX_PALETTE_COLORS in PosterizeOkLab.shader).</summary>
    public const int MaxColors = 32;

    [Tooltip("Colors the screen is quantized to. Entries beyond " + nameof(MaxColors) + " (32) are ignored.")]
    public List<Color> colors = new List<Color>
    {
        // Default "trattoria" palette so the effect does something out of the box.
        new Color32( 43,  33,  48, 255), // dark plum
        new Color32( 94,  54,  67, 255), // maroon
        new Color32(158,  66,  62, 255), // tomato dark
        new Color32(214, 108,  74, 255), // tomato light
        new Color32(238, 169, 108, 255), // pasta
        new Color32(246, 219, 165, 255), // cream
        new Color32(106, 130,  90, 255), // basil
        new Color32( 64,  84,  98, 255), // slate
    };
}
