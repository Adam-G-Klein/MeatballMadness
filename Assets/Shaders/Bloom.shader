// Fullscreen bloom pyramid, driven by BloomFeature.cs.
//
// Classic "dual filtering" bloom: bright fragments are isolated by a soft-knee
// threshold on their VALUE (max(r,g,b) — how close to white they read in a
// monochrome capture), then repeatedly downsampled/blurred into a mip pyramid
// and upsampled back with a tent filter that accumulates each level. The blur is
// what lets bright light bleed sideways past object silhouettes (and past the
// black outlines) instead of staying inside them.
//
// Textures:
//   _BlitTexture  - the source being sampled for the current pass (Blitter-bound).
//   _BloomHiTex   - the higher-resolution mip added back in during upsample, and
//                   the finished bloom pyramid during composite.
// Params (set from BloomFeature):
//   _BloomThreshold = (threshold, curve.x, curve.y, curve.z) soft-knee curve.
//   _BloomTexel     = (1/w, 1/h, w, h) of the texture being sampled this pass.
//   _BloomParams    = (intensity, scatter, 0, 0).
//   _BloomTint      = additive color applied at composite.
Shader "Hidden/MeatballMadness/Bloom"
{
    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" }
        ZWrite Off Cull Off ZTest Always

        HLSLINCLUDE
        #pragma target 3.5
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"

        float4 _BloomThreshold; // x = threshold, yzw = quadratic soft-knee curve
        float4 _BloomTexel;     // xy = 1/size, zw = size (of the sampled texture)
        float4 _BloomParams;    // x = intensity, y = scatter
        float4 _BloomTint;

        TEXTURE2D_X(_BloomHiTex);
        SAMPLER(sampler_BloomHiTex);

        half3 SampleSource(float2 uv)
        {
            return SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv).rgb;
        }

        half Max3(half3 c) { return max(c.r, max(c.g, c.b)); }

        // ------------------------------------------------------------------
        // Prefilter: keep only the bright part of each fragment, with a soft
        // knee so the transition into bloom isn't a hard clip. Uses VALUE
        // (max channel) as the brightness measure per the request.
        // ------------------------------------------------------------------
        half4 FragPrefilter(Varyings input) : SV_Target
        {
            UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

            // 4-tap box average across the half-res texel to tame fireflies.
            float2 uv = input.texcoord;
            float2 o = _BloomTexel.xy;
            half3 c  = SampleSource(uv + o * float2(-1, -1));
            c += SampleSource(uv + o * float2( 1, -1));
            c += SampleSource(uv + o * float2(-1,  1));
            c += SampleSource(uv + o * float2( 1,  1));
            c *= 0.25;

            half br = Max3(c);
            half threshold = _BloomThreshold.x;
            half3 curve = _BloomThreshold.yzw;
            // Quadratic knee: soft ramp between (threshold-knee) and (threshold+knee).
            half rq = clamp(br - curve.x, 0.0, curve.y);
            rq = curve.z * rq * rq;
            c *= max(rq, br - threshold) / max(br, 1e-4);
            return half4(c, 1.0);
        }

        // ------------------------------------------------------------------
        // Downsample: 13-tap partial-Karis box filter (COD "Next Gen Post").
        // Wider than a plain bilinear halving and much more stable.
        // ------------------------------------------------------------------
        half4 FragDownsample(Varyings input) : SV_Target
        {
            UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
            float2 uv = input.texcoord;
            float2 t = _BloomTexel.xy;

            half3 a = SampleSource(uv + t * float2(-2, -2));
            half3 b = SampleSource(uv + t * float2( 0, -2));
            half3 c = SampleSource(uv + t * float2( 2, -2));
            half3 d = SampleSource(uv + t * float2(-2,  0));
            half3 e = SampleSource(uv + t * float2( 0,  0));
            half3 f = SampleSource(uv + t * float2( 2,  0));
            half3 g = SampleSource(uv + t * float2(-2,  2));
            half3 h = SampleSource(uv + t * float2( 0,  2));
            half3 i = SampleSource(uv + t * float2( 2,  2));
            half3 j = SampleSource(uv + t * float2(-1, -1));
            half3 k = SampleSource(uv + t * float2( 1, -1));
            half3 l = SampleSource(uv + t * float2(-1,  1));
            half3 m = SampleSource(uv + t * float2( 1,  1));

            half3 col = e * 0.125;
            col += (a + c + g + i) * 0.03125;
            col += (b + d + f + h) * 0.0625;
            col += (j + k + l + m) * 0.125;
            return half4(col, 1.0);
        }

        // ------------------------------------------------------------------
        // Upsample + combine: 9-tap tent blur of the smaller mip (_BlitTexture),
        // added to the next-larger mip already computed (_BloomHiTex). Scatter
        // widens the tent so bleed reaches further past silhouettes.
        // ------------------------------------------------------------------
        half4 FragUpsample(Varyings input) : SV_Target
        {
            UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
            float2 uv = input.texcoord;
            float2 t = _BloomTexel.xy * (1.0 + _BloomParams.y * 3.0);

            half3 c  = SampleSource(uv + t * float2(-1, -1));
            c += SampleSource(uv + t * float2( 0, -1)) * 2.0;
            c += SampleSource(uv + t * float2( 1, -1));
            c += SampleSource(uv + t * float2(-1,  0)) * 2.0;
            c += SampleSource(uv + t * float2( 0,  0)) * 4.0;
            c += SampleSource(uv + t * float2( 1,  0)) * 2.0;
            c += SampleSource(uv + t * float2(-1,  1));
            c += SampleSource(uv + t * float2( 0,  1)) * 2.0;
            c += SampleSource(uv + t * float2( 1,  1));
            c *= 0.0625;

            half3 hi = SAMPLE_TEXTURE2D_X(_BloomHiTex, sampler_BloomHiTex, uv).rgb;
            return half4(hi + c, 1.0);
        }

        // ------------------------------------------------------------------
        // Composite: original scene (_BlitTexture) + finished bloom (_BloomHiTex).
        // ------------------------------------------------------------------
        half4 FragComposite(Varyings input) : SV_Target
        {
            UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
            float2 uv = input.texcoord;
            half3 scene = SampleSource(uv);
            half3 bloom = SAMPLE_TEXTURE2D_X(_BloomHiTex, sampler_BloomHiTex, uv).rgb;
            bloom *= _BloomParams.x * _BloomTint.rgb;
            return half4(scene + bloom, 1.0);
        }
        ENDHLSL

        Pass
        {
            Name "BloomPrefilter"
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment FragPrefilter
            ENDHLSL
        }

        Pass
        {
            Name "BloomDownsample"
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment FragDownsample
            ENDHLSL
        }

        Pass
        {
            Name "BloomUpsample"
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment FragUpsample
            ENDHLSL
        }

        Pass
        {
            Name "BloomComposite"
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment FragComposite
            ENDHLSL
        }
    }
}
