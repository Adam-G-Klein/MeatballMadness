// SpaghettiNoodle.shader
// URP unlit-with-lighting shader for the spaghetti tether LineRenderer.
//
// The LineRenderer emits flat quad strips; this shader fakes a cylindrical
// silhouette by reconstructing a half-cylinder normal from UV.y (which runs
// 0→1 across the noodle width).  The result: specular highlights roll across
// the noodle as if it were round, not flat.
//
// Assign to a new Material and drop it on the LineRenderer's Material slot.
// Tweak exposed properties in the Inspector; good starting defaults are baked in.

Shader "Meatball/SpaghettiNoodle"
{
    Properties
    {
        [Header(Base)]
        _BaseColor      ("Base Color",          Color)        = (0.96, 0.89, 0.48, 1.0)
        _AmbientBoost   ("Ambient Boost",       Range(0, 1))  = 0.25

        [Header(Specular)]
        _SpecColor      ("Specular Color",      Color)        = (1.0, 0.97, 0.88, 1.0)
        _SpecPower      ("Specular Power",      Range(8, 512))= 240.0
        _SpecStrength   ("Specular Strength",   Range(0, 4))  = 2.2

        [Header(Rim)]
        _RimColor       ("Rim Color",           Color)        = (0.90, 0.82, 0.30, 1.0)
        _RimPower       ("Rim Falloff",         Range(0.5, 8))= 3.0
        _RimStrength    ("Rim Strength",        Range(0, 1))  = 0.18

        [Header(Cylinder Fake)]
        _CylinderBlend  ("Cylinder Normal Blend", Range(0, 1)) = 1.0
    }

    SubShader
    {
        Tags
        {
            "RenderType"     = "Opaque"
            "Queue"          = "Geometry"
            "RenderPipeline" = "UniversalPipeline"
        }
        LOD 200

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            Cull Off   // LineRenderers are one-sided; cull off so it reads from both angles

            HLSLPROGRAM
            #pragma vertex   Vert
            #pragma fragment Frag

            // Shadow and additional-light keywords
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile_fog

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            // ── Per-material constants ────────────────────────────────────────
            CBUFFER_START(UnityPerMaterial)
                half4  _BaseColor;
                half   _AmbientBoost;
                half4  _SpecColor;
                half   _SpecPower;
                half   _SpecStrength;
                half4  _RimColor;
                half   _RimPower;
                half   _RimStrength;
                half   _CylinderBlend;
            CBUFFER_END

            // ── Vertex I/O ────────────────────────────────────────────────────
            struct Attributes
            {
                float4 positionOS  : POSITION;
                float3 normalOS    : NORMAL;
                float4 tangentOS   : TANGENT;
                float2 uv          : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionHCS  : SV_POSITION;
                float2 uv           : TEXCOORD0;
                float3 positionWS   : TEXCOORD1;
                float3 normalWS     : TEXCOORD2;
                float3 tangentWS    : TEXCOORD3;  // along-noodle axis in world space
                float  fogFactor    : TEXCOORD4;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            // ── Vertex shader ─────────────────────────────────────────────────
            Varyings Vert(Attributes IN)
            {
                UNITY_SETUP_INSTANCE_ID(IN);
                Varyings OUT;
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(OUT);

                VertexPositionInputs vpi = GetVertexPositionInputs(IN.positionOS.xyz);
                VertexNormalInputs   vni = GetVertexNormalInputs(IN.normalOS, IN.tangentOS);

                OUT.positionHCS = vpi.positionCS;
                OUT.positionWS  = vpi.positionWS;
                OUT.uv          = IN.uv;
                OUT.normalWS    = vni.normalWS;
                OUT.tangentWS   = vni.tangentWS;
                OUT.fogFactor   = ComputeFogFactor(vpi.positionCS.z);
                return OUT;
            }

            // ── Fragment shader ───────────────────────────────────────────────
            half4 Frag(Varyings IN) : SV_Target
            {
                // ── Fake cylindrical normal ───────────────────────────────────
                // UV.y runs 0→1 across the quad width (left edge → right edge).
                // We reconstruct the cylinder cross-section axes from world-up and
                // the noodle tangent — this is view-INDEPENDENT, so the highlight
                // stays on top of the noodle regardless of camera angle.
                //
                //   cylUp    = worldUp projected perpendicular to tangent ("top of noodle")
                //   cylRight = tangent × cylUp                             ("side of noodle")
                //
                // nx maps UV.y to −1..+1 across the width; nz is the outward arc height.
                float3 T        = normalize(IN.tangentWS);
                float3 worldUp  = float3(0, 1, 0);
                float3 cylUp    = normalize(worldUp - T * dot(T, worldUp));
                float3 cylRight = normalize(cross(T, cylUp));

                float  nx  = IN.uv.y * 2.0 - 1.0;
                float  nz  = sqrt(max(0.0001, 1.0 - nx * nx));

                float3 cylinderN = normalize(cylUp * nz + cylRight * nx);
                float3 N = normalize(lerp(IN.normalWS, cylinderN, _CylinderBlend));

                // View and half-vector
                float3 V = normalize(GetCameraPositionWS() - IN.positionWS);

                // ── Main directional light ────────────────────────────────────
                Light  main   = GetMainLight(TransformWorldToShadowCoord(IN.positionWS));
                float3 L      = normalize(main.direction);
                float3 H      = normalize(L + V);

                half   NdotL  = saturate(dot(N, L));
                half   NdotH  = saturate(dot(N, H));
                half   NdotV  = saturate(dot(N, V));

                // Diffuse
                half3  diffuse = _BaseColor.rgb * NdotL * half3(main.color) * main.shadowAttenuation;

                // Blinn-Phong specular — high power for wet glossy pasta
                half   specTerm = pow(NdotH, _SpecPower) * NdotL * _SpecStrength;
                half3  specular = _SpecColor.rgb * specTerm * half3(main.color) * main.shadowAttenuation;

                // ── Additional lights (point/spot) ────────────────────────────
#ifdef _ADDITIONAL_LIGHTS
                uint   lightCount = GetAdditionalLightsCount();
                for (uint li = 0u; li < lightCount; li++)
                {
                    Light  addLight  = GetAdditionalLight(li, IN.positionWS);
                    float3 La        = normalize(addLight.direction);
                    float3 Ha        = normalize(La + V);
                    half   aNdotL    = saturate(dot(N, La));
                    half   aNdotH    = saturate(dot(N, Ha));
                    half   atten     = addLight.distanceAttenuation * addLight.shadowAttenuation;

                    diffuse  += _BaseColor.rgb * aNdotL * half3(addLight.color) * atten;
                    specular += _SpecColor.rgb * pow(aNdotH, _SpecPower) * aNdotL * _SpecStrength
                                * half3(addLight.color) * atten;
                }
#endif

                // ── Ambient ───────────────────────────────────────────────────
                half3 ambient = _BaseColor.rgb * (half3(unity_AmbientSky.rgb) + _AmbientBoost);

                // ── Rim / Fresnel edge glow ───────────────────────────────────
                // Brightens the noodle silhouette edges for a slightly translucent look
                half  rimFactor = pow(1.0 - NdotV, _RimPower) * _RimStrength;
                half3 rim       = _RimColor.rgb * rimFactor;

                // ── Composite ─────────────────────────────────────────────────
                half3 color = ambient + diffuse + specular + rim;

                color = MixFog(color, IN.fogFactor);
                return half4(color, _BaseColor.a);
            }
            ENDHLSL
        }

        // DepthNormals prepass — REQUIRED for the screen-space outline feature.
        // ScreenOutlineFeature requests Depth|Normal, which URP fulfills with a
        // single DepthNormals prepass. Objects lacking this pass are absent from
        // BOTH the depth and normals textures, so the outline can't see them.
        // We reconstruct the same faked cylinder normal as ForwardLit so that
        // normal-crease outlines follow the noodle's apparent round silhouette
        // rather than the flat ribbon geometry.
        Pass
        {
            Name "DepthNormals"
            Tags { "LightMode" = "DepthNormals" }

            ZWrite On
            Cull Off

            HLSLPROGRAM
            #pragma vertex   DepthNormalsVert
            #pragma fragment DepthNormalsFrag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half4  _BaseColor;
                half   _AmbientBoost;
                half4  _SpecColor;
                half   _SpecPower;
                half   _SpecStrength;
                half4  _RimColor;
                half   _RimPower;
                half   _RimStrength;
                half   _CylinderBlend;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float4 tangentOS  : TANGENT;
                float2 uv         : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float2 uv          : TEXCOORD0;
                float3 normalWS    : TEXCOORD1;
                float3 tangentWS   : TEXCOORD2;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings DepthNormalsVert(Attributes IN)
            {
                UNITY_SETUP_INSTANCE_ID(IN);
                Varyings OUT;
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(OUT);

                VertexPositionInputs vpi = GetVertexPositionInputs(IN.positionOS.xyz);
                VertexNormalInputs   vni = GetVertexNormalInputs(IN.normalOS, IN.tangentOS);

                OUT.positionHCS = vpi.positionCS;
                OUT.uv          = IN.uv;
                OUT.normalWS    = vni.normalWS;
                OUT.tangentWS   = vni.tangentWS;
                return OUT;
            }

            half4 DepthNormalsFrag(Varyings IN) : SV_Target
            {
                // Same view-independent cylinder normal as the ForwardLit pass.
                float3 T        = normalize(IN.tangentWS);
                float3 worldUp  = float3(0, 1, 0);
                float3 cylUp    = normalize(worldUp - T * dot(T, worldUp));
                float3 cylRight = normalize(cross(T, cylUp));

                float  nx = IN.uv.y * 2.0 - 1.0;
                float  nz = sqrt(max(0.0001, 1.0 - nx * nx));

                float3 cylinderN = normalize(cylUp * nz + cylRight * nx);
                float3 N = normalize(lerp(IN.normalWS, cylinderN, _CylinderBlend));

                // URP stores world-space normals in _CameraNormalsTexture.
                return half4(N, 0.0);
            }
            ENDHLSL
        }

        // DepthOnly prepass — used when a pass requests depth without normals.
        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }

            ZWrite On
            ColorMask R
            Cull Off

            HLSLPROGRAM
            #pragma vertex   DepthOnlyVert
            #pragma fragment DepthOnlyFrag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            // Identical UnityPerMaterial layout across all passes keeps the
            // shader SRP Batcher-compatible, even though DepthOnly reads none of it.
            CBUFFER_START(UnityPerMaterial)
                half4  _BaseColor;
                half   _AmbientBoost;
                half4  _SpecColor;
                half   _SpecPower;
                half   _SpecStrength;
                half4  _RimColor;
                half   _RimPower;
                half   _RimStrength;
                half   _CylinderBlend;
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

            Varyings DepthOnlyVert(Attributes IN)
            {
                UNITY_SETUP_INSTANCE_ID(IN);
                Varyings OUT;
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(OUT);
                OUT.positionHCS = GetVertexPositionInputs(IN.positionOS.xyz).positionCS;
                return OUT;
            }

            half4 DepthOnlyFrag(Varyings IN) : SV_Target
            {
                return 0;
            }
            ENDHLSL
        }

        // Shadow caster pass so the noodle casts shadows
        UsePass "Universal Render Pipeline/Lit/ShadowCaster"
    }

    FallBack "Universal Render Pipeline/Lit"
}
