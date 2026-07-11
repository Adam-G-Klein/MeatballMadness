// Fullscreen "derez" pixelation, driven by DerezFeature.cs.
//
// Visually equivalent to rendering the camera image into a small render texture
// and stretching it back over the screen with point (nearest) filtering: we snap
// the sample UV to the center of a block grid and point-sample the source, so a
// whole block of screen pixels reads a single source texel. No intermediate
// texture is allocated, and the point sampler is guaranteed here (a plain blit's
// linear sampler would smear the blocks).
//
// _DerezParams: xy = block count across the screen (width/blockPx, height/blockPx),
//               zw = unused.
Shader "Hidden/MeatballMadness/DerezSnap"
{
    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" }
        ZWrite Off Cull Off ZTest Always

        Pass
        {
            Name "DerezSnap"

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex Vert
            #pragma fragment Frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"

            float4 _DerezParams; // xy = blocks across (x, y)

            half4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                float2 blocks = max(_DerezParams.xy, 1.0);
                // Snap to block center so every fragment in a block reads the same texel.
                float2 uv = (floor(input.texcoord * blocks) + 0.5) / blocks;

                return SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_PointClamp, uv);
            }
            ENDHLSL
        }
    }
}
