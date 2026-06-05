// VictoryZone.shader
// URP unlit transparent shader for the LevelVictoryZone volume.
//
// The zone renders as a flat, single-colour translucent box. The colour is
// driven from C# (LevelVictoryZone) per victory state — red / yellow / green —
// by setting the "_ZoneColor" property on the material. The alpha is held at a
// constant 0.25 so the zone always reads as a soft overlay regardless of state.
//
// Assign the generated VictoryZone material to the zone's MeshRenderer.

Shader "Meatball/VictoryZone"
{
    Properties
    {
        _ZoneColor ("Zone Color", Color)     = (1, 0, 0, 1)
        _Alpha     ("Alpha",      Range(0,1)) = 0.25
    }

    SubShader
    {
        Tags
        {
            "RenderType"     = "Transparent"
            "Queue"          = "Transparent"
            "RenderPipeline" = "UniversalPipeline"
        }
        LOD 100

        Pass
        {
            Name "Unlit"
            Tags { "LightMode" = "UniversalForward" }

            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            Cull Off   // visible from inside the box too

            HLSLPROGRAM
            #pragma vertex   Vert
            #pragma fragment Frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half4 _ZoneColor;
                half  _Alpha;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings Vert(Attributes IN)
            {
                UNITY_SETUP_INSTANCE_ID(IN);
                Varyings OUT;
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(OUT);

                OUT.positionHCS = GetVertexPositionInputs(IN.positionOS.xyz).positionCS;
                return OUT;
            }

            half4 Frag(Varyings IN) : SV_Target
            {
                // Colour comes from C# per state; alpha is fixed at 0.25 (via _Alpha).
                return half4(_ZoneColor.rgb, _Alpha);
            }
            ENDHLSL
        }
    }

    FallBack Off
}
