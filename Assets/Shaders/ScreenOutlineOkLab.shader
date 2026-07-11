// Fullscreen edge-detection outlines, driven by ScreenOutlineFeature.cs.
//
// Two independent outline types:
//   * Depth outlines  — drawn where a nearby pixel is sufficiently CLOSER to the
//     camera than this one (so the outline hugs the outside of the nearer object,
//     including silhouettes against the sky). Colored by OkLab-matching the color
//     of the closest nearby fragment (the object being outlined) against the
//     depth outline palette; the winning entry supplies both color and width.
//   * Normal outlines — drawn where nearby world normals diverge on the SAME
//     surface (interior creases). Colored by OkLab-matching the current
//     fragment's own scene color against the normal outline palette.
//
// Depth outlines take priority where both would draw.
//
// Widths are per-palette-entry, in pixels. Because width can differ per entry we
// can't know the kernel size up front, so we search a square window of radius
// max(width) (capped at 8 by the C# side), record the distance to the nearest
// edge, then only draw if that distance fits within the *chosen* entry's width.
Shader "Hidden/MeatballMadness/ScreenOutlineOkLab"
{
    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" }
        ZWrite Off Cull Off ZTest Always

        Pass
        {
            Name "ScreenOutlineOkLab"

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex Vert
            #pragma fragment Frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"
            #include "OkLab.hlsl"

            // Must match OutlinePalette.MaxEntries on the C# side.
            #define MAX_OUTLINE_ENTRIES 8

            // Camera depth + world-space normals, bound explicitly by the renderer
            // feature (custom names so we never fight URP's own globals).
            TEXTURE2D_X_FLOAT(_SceneDepth);
            TEXTURE2D_X(_SceneNormals);

            // Per-object outline width multiplier, R channel, default 1.0. Written by
            // the feature's mask sub-pass (OutlineMaskWrite.shader) from the layer
            // overrides. _UseOutlineMask is 0 when no overrides are configured.
            TEXTURE2D_X_FLOAT(_OutlineMask);
            int _UseOutlineMask;

            float4 _DepthPaletteLab[MAX_OUTLINE_ENTRIES];  // xyz = OkLab, w = width (px)
            float4 _DepthPaletteRgb[MAX_OUTLINE_ENTRIES];  // rgb = linear output color
            int _DepthPaletteCount;
            int _DepthSearchRadius;                        // ceil(max depth-palette width)

            float4 _NormalPaletteLab[MAX_OUTLINE_ENTRIES];
            float4 _NormalPaletteRgb[MAX_OUTLINE_ENTRIES];
            int _NormalPaletteCount;
            int _NormalSearchRadius;

            float _DepthThreshold;       // eye-space meters of depth delta that count as an edge
            float _DepthDistanceScale;   // extra threshold per meter of view distance
            float _GrazingCompensation;  // threshold boost for surfaces seen edge-on (kills floor false-positives)
            float _NormalThreshold;      // edge when (1 - dot(n1, n2)) exceeds this; range 0..2
            int _OutlineAgainstSky;      // 1 = silhouettes against the skybox get depth outlines
            float4 _TexelSize;           // xy = 1/resolution, zw = resolution

            // Set globally by DerezFeature when the derez (pixelation) pass is active.
            // xy = block count across the screen; x < 1 means derez is off. Depth and
            // normals aren't derez'd, so we snap every depth/normal sample to this grid
            // to detect edges at block boundaries instead of the true (sub-block)
            // silhouette. Without it the crisp outline hugs real geometry while the fill
            // has snapped to block edges, leaving a gap of background between the two.
            float4 _DerezParams;

            // Raw depth value that means "nothing was rendered here" (skybox).
            #if UNITY_REVERSED_Z
                #define IS_SKY_DEPTH(raw) ((raw) <= 1.0e-7)
            #else
                #define IS_SKY_DEPTH(raw) ((raw) >= 1.0 - 1.0e-7)
            #endif

            // Snap a UV to the center of its derez block (no-op when derez is off), so
            // depth/normal edge detection runs on the same quantized grid as the fill.
            // Snapping after adding the kernel offset means a sample only jumps to a
            // neighbor block once the offset crosses a block boundary — edges then land
            // exactly on block edges, while the kernel offset itself stays in pixels so
            // the outline band keeps its per-palette pixel width.
            float2 SnapToDerezGrid(float2 uv)
            {
                if (_DerezParams.x < 1.0)
                    return uv;
                return (floor(uv * _DerezParams.xy) + 0.5) / _DerezParams.xy;
            }

            // All sampling uses explicit LOD 0: several samples happen inside
            // divergent branches/loops where implicit-derivative sampling is undefined.
            float SampleRawDepth(float2 uv)
            {
                return SAMPLE_TEXTURE2D_X_LOD(_SceneDepth, sampler_PointClamp, uv, 0).r;
            }

            float3 SampleNormalWS(float2 uv)
            {
                return SafeNormalize(SAMPLE_TEXTURE2D_X_LOD(_SceneNormals, sampler_PointClamp, uv, 0).xyz);
            }

            float4 SampleSceneColor(float2 uv)
            {
                return SAMPLE_TEXTURE2D_X_LOD(_BlitTexture, sampler_PointClamp, uv, 0);
            }

            // Outline width multiplier for the object at uv. 1.0 (no change) when the
            // mask is disabled or the pixel wasn't drawn into it; 0.0 suppresses.
            float SampleOutlineMultiplier(float2 uv)
            {
                if (_UseOutlineMask == 0)
                    return 1.0;
                return SAMPLE_TEXTURE2D_X_LOD(_OutlineMask, sampler_PointClamp, uv, 0).r;
            }

            int ClosestPaletteEntry(float3 lab, float4 paletteLab[MAX_OUTLINE_ENTRIES], int count)
            {
                int best = 0;
                float bestDistSq = 1e30;
                [loop]
                for (int i = 0; i < count; i++)
                {
                    float distSq = OkLabDistanceSq(lab, paletteLab[i].xyz);
                    if (distSq < bestDistSq)
                    {
                        bestDistSq = distSq;
                        best = i;
                    }
                }
                return best;
            }

            float4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                float2 uv = input.texcoord;

                float4 sceneColor = SampleSceneColor(uv);

                // Run all depth/normal reads on the derez grid (a no-op when derez is
                // off) so detected edges align with the pixelated fill, not real geometry.
                float2 duv = SnapToDerezGrid(uv);

                float rawCenter = SampleRawDepth(duv);
                bool centerIsSky = IS_SKY_DEPTH(rawCenter);
                float eyeCenter = LinearEyeDepth(rawCenter, _ZBufferParams);
                float3 normalCenter = centerIsSky ? float3(0.0, 0.0, 1.0) : SampleNormalWS(duv);

                // Effective depth threshold for this fragment. Two compensations:
                //  * distance: depth precision and per-pixel depth deltas both grow
                //    with distance, so scale the threshold up to avoid far geometry
                //    dissolving into outline.
                //  * grazing angle: a floor seen nearly edge-on has huge depth deltas
                //    between adjacent pixels without being an edge. Boost the
                //    threshold as the surface turns away from the camera.
                float depthEdgeThreshold = _DepthThreshold * (1.0 + _DepthDistanceScale * eyeCenter);
                if (!centerIsSky)
                {
                    float3 positionWS = ComputeWorldSpacePosition(duv, rawCenter, UNITY_MATRIX_I_VP);
                    float3 viewDir = normalize(_WorldSpaceCameraPos - positionWS);
                    float NdotV = saturate(dot(normalCenter, viewDir));
                    depthEdgeThreshold *= 1.0 + _GrazingCompensation * pow(1.0 - NdotV, 4.0);
                }

                // ---------------- Depth (exterior) outlines ----------------
                bool runDepth = (_DepthPaletteCount > 0) && !(centerIsSky && _OutlineAgainstSky == 0);
                if (runDepth)
                {
                    float edgeDist = 1e8;      // px distance to the nearest depth edge
                    float minEye = eyeCenter;  // closest depth found in the window...
                    float2 minUV = duv;        // ...and where — that's the object we're outlining

                    [loop]
                    for (int dy = -_DepthSearchRadius; dy <= _DepthSearchRadius; dy++)
                    {
                        [loop]
                        for (int dx = -_DepthSearchRadius; dx <= _DepthSearchRadius; dx++)
                        {
                            if (dx == 0 && dy == 0)
                                continue;

                            float2 offset = float2(dx, dy);
                            float2 uvSample = SnapToDerezGrid(uv + offset * _TexelSize.xy);
                            float eyeSample = LinearEyeDepth(SampleRawDepth(uvSample), _ZBufferParams);

                            if (eyeSample < minEye)
                            {
                                minEye = eyeSample;
                                minUV = uvSample;
                            }

                            // Only a CLOSER neighbor makes an edge: the outline is drawn
                            // on the far side of the discontinuity, around the near object.
                            if (eyeCenter - eyeSample > depthEdgeThreshold)
                                edgeDist = min(edgeDist, length(offset));
                        }
                    }

                    if (edgeDist < 1e7)
                    {
                        // Color of the object being outlined = scene color at the
                        // closest (lowest-depth) sample in the window.
                        float3 objectLab = LinearSrgbToOkLab(SampleSceneColor(minUV).rgb);
                        int entry = ClosestPaletteEntry(objectLab, _DepthPaletteLab, _DepthPaletteCount);
                        // Width scaled by the outlined (near) object's per-object
                        // multiplier. A multiplier of 0 collapses the band to nothing.
                        float widthMul = SampleOutlineMultiplier(minUV);
                        if (edgeDist <= _DepthPaletteLab[entry].w * widthMul)
                            return float4(_DepthPaletteRgb[entry].rgb, sceneColor.a);
                    }
                }

                // ---------------- Normal (interior) outlines ----------------
                if (!centerIsSky && _NormalPaletteCount > 0)
                {
                    float edgeDist = 1e8;

                    [loop]
                    for (int dy = -_NormalSearchRadius; dy <= _NormalSearchRadius; dy++)
                    {
                        [loop]
                        for (int dx = -_NormalSearchRadius; dx <= _NormalSearchRadius; dx++)
                        {
                            if (dx == 0 && dy == 0)
                                continue;

                            float2 offset = float2(dx, dy);
                            float2 uvSample = SnapToDerezGrid(uv + offset * _TexelSize.xy);
                            float rawSample = SampleRawDepth(uvSample);
                            if (IS_SKY_DEPTH(rawSample))
                                continue;

                            // Ignore normal deltas across depth discontinuities —
                            // those are exterior edges, the depth pass's job.
                            float eyeSample = LinearEyeDepth(rawSample, _ZBufferParams);
                            if (abs(eyeCenter - eyeSample) > depthEdgeThreshold)
                                continue;

                            float3 normalSample = SampleNormalWS(uvSample);
                            if (1.0 - dot(normalCenter, normalSample) > _NormalThreshold)
                                edgeDist = min(edgeDist, length(offset));
                        }
                    }

                    if (edgeDist < 1e7)
                    {
                        // Interior outlines match against the fragment's own color.
                        float3 lab = LinearSrgbToOkLab(sceneColor.rgb);
                        int entry = ClosestPaletteEntry(lab, _NormalPaletteLab, _NormalPaletteCount);
                        // Interior creases scale by this fragment's own object multiplier.
                        float widthMul = SampleOutlineMultiplier(uv);
                        if (edgeDist <= _NormalPaletteLab[entry].w * widthMul)
                            return float4(_NormalPaletteRgb[entry].rgb, sceneColor.a);
                    }
                }

                return sceneColor;
            }
            ENDHLSL
        }
    }
}
