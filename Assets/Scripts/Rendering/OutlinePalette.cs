using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// A set of color + width entries for the screen outline effect
/// (ScreenOutlineFeature). For each outlined fragment the effect OkLab-matches
/// the color of the thing being outlined against these entries and draws the
/// winning entry's color at the winning entry's width. The same asset can be
/// used for both depth (exterior) and normal (interior) outlines, or make two.
/// </summary>
[CreateAssetMenu(menuName = "Meatball Madness/Outline Palette", fileName = "OutlinePalette")]
public class OutlinePalette : ScriptableObject
{
    /// <summary>Hard cap baked into the shader (MAX_OUTLINE_ENTRIES in ScreenOutlineOkLab.shader).</summary>
    public const int MaxEntries = 8;

    [Serializable]
    public class Entry
    {
        public Color color = Color.black;

        [Tooltip("Outline thickness in pixels. Widths below 1 effectively disable the entry.")]
        [Range(0f, 8f)]
        public float width = 2f;
    }

    [Tooltip("Outline color/width pairs. Entries beyond " + nameof(MaxEntries) + " (8) are ignored. " +
             "The entry whose color is OkLab-closest to the outlined object's color wins.")]
    public List<Entry> entries = new List<Entry>
    {
        // Two defaults so the color-matching mechanic is visible immediately:
        // dark objects get a thick near-black line, light objects a thin cream one.
        new Entry { color = new Color(0.07f, 0.05f, 0.08f), width = 2.5f },
        new Entry { color = new Color(0.96f, 0.86f, 0.65f), width = 1.5f },
    };

    // Edge-detection thresholds live here so the whole look is edited in one
    // asset. Which group applies depends on the slot this palette occupies on
    // ScreenOutlineFeature; with the same asset in both slots, everything below
    // is active.

    [Header("Depth Edges (read from the palette in the Depth slot)")]
    [Tooltip("Eye-space depth difference in meters that counts as an exterior edge. Lower = more outlines.")]
    [Min(0.001f)] public float depthThreshold = 0.2f;

    [Tooltip("Extra depth threshold added per meter of camera distance, so distant geometry " +
             "doesn't dissolve into solid outline. 0 disables.")]
    [Min(0f)] public float depthDistanceScale = 0.01f;

    [Tooltip("Raises the depth threshold on surfaces viewed edge-on (floors receding toward the horizon) " +
             "to suppress false outlines there. 0 disables.")]
    [Range(0f, 50f)] public float grazingAngleCompensation = 6f;

    [Header("Normal Edges (read from the palette in the Normal slot)")]
    [Tooltip("Normal difference that counts as an interior edge, measured as 1 - dot(normalA, normalB): " +
             "0 = identical normals, 1 = perpendicular, 2 = opposite. Lower = more crease outlines.")]
    [Range(0.01f, 2f)] public float normalThreshold = 0.4f;
}
