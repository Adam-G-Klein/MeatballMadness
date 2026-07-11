// Per-object outline mask, drawn by ScreenOutlineFeature's mask sub-pass.
//
// Renders selected objects (by layer) into a single-channel float target whose
// R value is that object's outline WIDTH MULTIPLIER (see ObjectOutlineOverride
// on the feature). The target is cleared to 1.0 (default width) beforehand, so
// anything not drawn keeps the normal outline; objects drawn with _MaskValue = 2
// get twice-as-thick outlines, _MaskValue = 0 get none.
//
// There is no depth buffer bound for this pass. We reject occluded fragments
// manually by comparing against the camera depth texture, so a player hidden
// behind ordinary (non-overridden) geometry does not paint its multiplier onto
// that geometry. This makes the pass independent of where it is injected.
Shader "Hidden/MeatballMadness/OutlineMaskWrite"
{
    Properties
    {
        _MaskValue ("Outline Width Multiplier", Float) = 1
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" }

        Pass
        {
            Name "OutlineMaskWrite"
            ZWrite Off
            ZTest Always
            Cull Back

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            // Bound as a global by the mask pass (same name the outline pass uses).
            TEXTURE2D_X_FLOAT(_SceneDepth);

            float _MaskValue;

            struct Attributes
            {
                float4 positionOS : POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings Vert(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                output.positionHCS = TransformObjectToHClip(input.positionOS.xyz);
                return output;
            }

            float Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                // Manual depth test: discard fragments that sit behind whatever the
                // camera already rendered here. positionHCS.xy are pixel coordinates
                // in the fragment stage; positionHCS.z is this fragment's raw depth.
                float2 uv = input.positionHCS.xy * (_ScreenParams.zw - 1.0);
                float sceneRaw = SAMPLE_TEXTURE2D_X_LOD(_SceneDepth, sampler_PointClamp, uv, 0).r;
                float sceneEye = LinearEyeDepth(sceneRaw, _ZBufferParams);
                float fragEye = LinearEyeDepth(input.positionHCS.z, _ZBufferParams);

                // Relative bias so co-planar (visible) fragments survive despite
                // precision, while genuinely occluded fragments are dropped.
                if (fragEye > sceneEye + 0.02 + 0.01 * fragEye)
                    discard;

                return _MaskValue;
            }
            ENDHLSL
        }
    }
}
