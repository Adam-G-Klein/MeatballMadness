#ifndef MEATBALL_OKLAB_INCLUDED
#define MEATBALL_OKLAB_INCLUDED

// OkLab color space, Björn Ottosson 2020 (https://bottosson.github.io/posts/oklab/).
// A perceptually uniform space inspired by CIELAB but built for exactly the thing
// we do here: Euclidean distance between two OkLab points is a good proxy for how
// different the two colors *look*. Input must be LINEAR sRGB (which is what URP
// render targets hold in a linear color-space project).

float3 LinearSrgbToOkLab(float3 c)
{
    // Guard against tiny negative values from filtering/HDR before the cube root.
    c = max(c, 0.0);

    // Linear sRGB -> LMS (long/medium/short cone response).
    float l = dot(c, float3(0.4122214708, 0.5363325363, 0.0514459929));
    float m = dot(c, float3(0.2119034982, 0.6806995451, 0.1073969566));
    float s = dot(c, float3(0.0883024619, 0.2817188376, 0.6299787005));

    // Nonlinearity: cube root (cheap stand-in for CIELAB's f(t) curve).
    float3 lms = pow(float3(l, m, s), 1.0 / 3.0);

    // LMS' -> OkLab. x = L (lightness), y = a (green-red), z = b (blue-yellow).
    return float3(
        dot(lms, float3(0.2104542553,  0.7936177850, -0.0040720468)),
        dot(lms, float3(1.9779984951, -2.4285922050,  0.4505937099)),
        dot(lms, float3(0.0259040371,  0.7827717662, -0.8086757660)));
}

// Squared distance is enough for "which palette entry is closest" comparisons,
// so we skip the sqrt.
float OkLabDistanceSq(float3 labA, float3 labB)
{
    float3 d = labA - labB;
    return dot(d, d);
}

#endif // MEATBALL_OKLAB_INCLUDED
