// Fullscreen posterization: snaps every pixel to the closest color in a palette,
// where "closest" is measured in the OkLab color space (see OkLab.hlsl), then
// applies any value snaps that override whole lightness bands (PosterizeCommon.hlsl).
// Driven by PosterizeFeature.cs, which uploads the PosterizeSettings (palette both
// as linear RGB and pre-converted OkLab, plus the value snaps), so the per-pixel
// cost is one color-space conversion plus a couple of small loops.
Shader "Hidden/MeatballMadness/PosterizeOkLab"
{
    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" }
        ZWrite Off Cull Off ZTest Always

        Pass
        {
            Name "PosterizeOkLab"

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex Vert
            #pragma fragment Frag

            // Core.hlsl for URP globals, Blit.hlsl for the fullscreen-triangle
            // Vert/Varyings and the _BlitTexture the renderer feature blits from.
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"
            // Palette + value-snap uniforms and the PosterizeOkLab() helper, shared
            // with DitherOkLab.shader so both honour identical PosterizeSettings.
            #include "PosterizeCommon.hlsl"

            float4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                float4 src = SAMPLE_TEXTURE2D_X_LOD(_BlitTexture, sampler_PointClamp, input.texcoord, 0);
                float3 lab = LinearSrgbToOkLab(src.rgb);
                return float4(PosterizeOkLab(lab), src.a);
            }
            ENDHLSL
        }
    }
}
