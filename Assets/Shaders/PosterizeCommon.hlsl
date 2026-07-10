#ifndef MEATBALL_POSTERIZE_COMMON_INCLUDED
#define MEATBALL_POSTERIZE_COMMON_INCLUDED

// Shared quantization logic for the posterize (PosterizeOkLab) and dither
// (DitherOkLab) screen effects, so both honour exactly the same PosterizeSettings
// (palette + value snaps). Uploaded from C# by PosterizeSettings.Apply.
#include "OkLab.hlsl"

// Must match PosterizePalette.MaxColors on the C# side. SetVectorArray fixes the
// GPU array size on first upload, so C# always sends the full MAX_PALETTE_COLORS
// entries and _PaletteCount says how many are live.
#define MAX_PALETTE_COLORS 32

// Must match PosterizeSettings.MaxValueSnaps on the C# side.
#define MAX_VALUE_SNAPS 8

float4 _PaletteOkLab[MAX_PALETTE_COLORS]; // xyz = palette color in OkLab
float4 _PaletteRgb[MAX_PALETTE_COLORS];   // rgb = palette color in linear sRGB
int _PaletteCount;

// Value snaps: override the palette match for fragments in a lightness band.
// _ValueSnapRange[i].xy = (minL, maxL) in OkLab lightness (0 = black, 1 = white);
// _ValueSnapRgb[i].rgb  = linear sRGB color to force for that band.
float4 _ValueSnapRgb[MAX_VALUE_SNAPS];
float4 _ValueSnapRange[MAX_VALUE_SNAPS];
int _ValueSnapCount;

// Posterize a fragment already converted to OkLab: snap to the nearest palette
// entry (OkLab distance), then let the first value-snap band whose lightness
// range contains the fragment's L override that color. Value snaps sample ONLY
// how light/dark the fragment is (lab.x), never its hue, so they can push whole
// tonal ranges — e.g. highlights — toward a chosen color.
float3 PosterizeOkLab(float3 lab)
{
    int best = 0;
    float bestDistSq = 1e30;
    [loop]
    for (int i = 0; i < _PaletteCount; i++)
    {
        float distSq = OkLabDistanceSq(lab, _PaletteOkLab[i].xyz);
        if (distSq < bestDistSq)
        {
            bestDistSq = distSq;
            best = i;
        }
    }

    float3 outColor = _PaletteRgb[best].rgb;

    // First matching band wins, so earlier list entries take priority where
    // ranges overlap.
    [loop]
    for (int j = 0; j < _ValueSnapCount; j++)
    {
        if (lab.x >= _ValueSnapRange[j].x && lab.x <= _ValueSnapRange[j].y)
        {
            outColor = _ValueSnapRgb[j].rgb;
            break;
        }
    }

    return outColor;
}

#endif // MEATBALL_POSTERIZE_COMMON_INCLUDED
