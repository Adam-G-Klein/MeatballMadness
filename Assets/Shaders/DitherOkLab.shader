// Fullscreen ordered (Bayer) dithering against a fixed palette, driven by
// DitherFeature.cs with all settings on a DitherSettings asset.
//
// Method: each pixel's color is nudged up or down by a threshold value from a
// Bayer matrix tiled across the screen, then run through the exact same
// PosterizeOkLab() quantization (nearest palette color + value snaps) the
// posterize effect uses, from the shared PosterizeSettings. The position-
// dependent nudge makes smooth gradients resolve into patterns of alternating
// palette colors instead of hard bands, and because the nudge shifts lightness
// it also dithers the value-snap band edges. With _Spread = 0 this degenerates
// to exactly the posterize effect.
Shader "Hidden/MeatballMadness/DitherOkLab"
{
    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" }
        ZWrite Off Cull Off ZTest Always

        Pass
        {
            Name "DitherOkLab"

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex Vert
            #pragma fragment Frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"
            // Palette + value-snap uniforms and the PosterizeOkLab() helper, shared
            // with PosterizeOkLab.shader so both honour identical PosterizeSettings.
            #include "PosterizeCommon.hlsl"

            float _Spread;      // dither amplitude added before quantization
            int _BayerLevels;   // 1 = 2x2, 2 = 4x4, 3 = 8x8 (DitherSettings.BayerSize)
            int _PatternScale;  // screen pixels per Bayer cell

            // Threshold of the (2^levels x 2^levels) Bayer matrix at pixel p,
            // remapped to a zero-centered range (-0.5 .. 0.5).
            //
            // Uses the recursive construction M_2n(x, y) = 4 * M_n(x % n, y % n)
            //                                            + M_2(x / n, y / n),
            // with M_2 = [[0, 2], [3, 1]], which per bit i of (x, y) reduces to
            // (2*bx) XOR (3*by) weighted by 4^(levels-1-i) — finest bits weigh most.
            float BayerThreshold(uint2 p, int levels)
            {
                uint value = 0u;
                uint weight = 1u << uint(2 * (levels - 1)); // 4^(levels-1)
                [loop]
                for (int i = 0; i < levels; i++)
                {
                    uint bx = (p.x >> uint(i)) & 1u;
                    uint by = (p.y >> uint(i)) & 1u;
                    value += ((2u * bx) ^ (3u * by)) * weight;
                    weight >>= 2u;
                }
                uint cellCount = 1u << uint(2 * levels); // (2^levels)^2
                // +0.5 centers each step inside its bucket so the pattern is
                // symmetric around zero after the -0.5 shift.
                return (float(value) + 0.5) / float(cellCount) - 0.5;
            }

            float4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                float4 src = SAMPLE_TEXTURE2D_X_LOD(_BlitTexture, sampler_PointClamp, input.texcoord, 0);

                // SV_POSITION xy in the fragment stage is pixel coordinates.
                // Integer-dividing by _PatternScale makes blocks of pixels share
                // one Bayer cell for a chunkier pattern at full resolution.
                uint2 pixel = uint2(input.positionCS.xy) / uint(max(_PatternScale, 1));
                float dither = BayerThreshold(pixel, _BayerLevels) * _Spread;

                // Perturb all channels equally (intensity dithering), then run the
                // shared posterize quantization exactly like PosterizeOkLab.shader.
                // Because the perturbation shifts the fragment's lightness, the
                // value snaps are evaluated against the dithered L too, so their
                // band edges dither instead of forming hard lines.
                float3 lab = LinearSrgbToOkLab(src.rgb + dither);
                return float4(PosterizeOkLab(lab), src.a);
            }
            ENDHLSL
        }
    }
}
