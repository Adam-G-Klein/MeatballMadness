using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

/// <summary>
/// Screen-space outline renderer feature. Depth deltas produce exterior
/// (silhouette) outlines, normal deltas produce interior (crease) outlines.
/// Each outline type has an OutlinePalette of color + width entries; the entry
/// whose color is OkLab-closest to the outlined object's color is used.
/// Requests the depth and normals prepasses itself — nothing to enable on the
/// URP asset. Add to the Universal Renderer's Renderer Features list.
/// </summary>
public class ScreenOutlineFeature : ScriptableRendererFeature
{
    /// <summary>
    /// Overrides the outline width for every object on the given layers. Drives a
    /// mask sub-pass that paints <see cref="widthMultiplier"/> over those objects;
    /// the outline shader multiplies its palette width by that value. 0 = no
    /// outline, 1 = normal, 2 = twice as thick, etc.
    /// </summary>
    [System.Serializable]
    public class ObjectOutlineOverride
    {
        [Tooltip("Optional label, for your own reference in the inspector.")]
        public string label;

        [Tooltip("Objects on these layers get the width multiplier below. Put your players on their " +
                 "own layer and select it here for thicker outlines; make a 'NoOutline' layer for meshes " +
                 "that should not be outlined and set the multiplier to 0.")]
        public LayerMask layers;

        [Tooltip("Multiplies the outline width for objects on these layers. 0 = no outline at all, " +
                 "1 = the palette's normal width, 2 = twice as thick. Capped so total width stays <= 8 px.")]
        [Range(0f, 4f)] public float widthMultiplier = 2f;
    }

    [Header("Palettes")]
    [Tooltip("Color + width entries for depth (exterior/silhouette) outlines, matched against the color " +
             "of the closest nearby fragment on the outlined object. None/empty disables depth outlines.")]
    [SerializeField] private OutlinePalette _depthOutlinePalette;

    [Tooltip("Color + width entries for normal (interior crease) outlines, matched against the current " +
             "fragment's own color. None/empty disables normal outlines. Can be the same asset as above.")]
    [SerializeField] private OutlinePalette _normalOutlinePalette;

    [Header("Behavior")]
    [Tooltip("Draw depth outlines around object silhouettes against the skybox. " +
             "Edge-detection thresholds are edited on the OutlinePalette assets.")]
    [SerializeField] private bool _outlineAgainstSky = true;

    [Header("Per-Object Overrides (by layer)")]
    [Tooltip("Give specific layers a thicker outline (multiplier > 1) or none (multiplier 0). " +
             "Objects not matched by any entry use the palette's normal width. Empty = every object " +
             "outlines the same, and the mask sub-pass is skipped entirely.")]
    [SerializeField] private List<ObjectOutlineOverride> _objectOverrides = new List<ObjectOutlineOverride>();

    [Tooltip("Mask-writing shader. Assign Assets/Shaders/OutlineMaskWrite.shader manually, or leave " +
             "empty and it is auto-found (Hidden/MeatballMadness/OutlineMaskWrite) when the feature loads.")]
    [SerializeField] private Shader _maskShader;

    [Header("Setup")]
    [Tooltip("Where in the frame the effect runs. Keep at the same event as PosterizeFeature and order " +
             "the two in the Renderer Features list.")]
    [SerializeField] private RenderPassEvent _injectionPoint = RenderPassEvent.AfterRenderingPostProcessing;

    [Tooltip("Fullscreen outline shader. Assign Assets/Shaders/ScreenOutlineOkLab.shader manually, or leave " +
             "empty and it is auto-found (Hidden/MeatballMadness/ScreenOutlineOkLab) when the feature loads.")]
    [SerializeField] private Shader _shader;

    private Material _material;
    private Material[] _maskMaterials;   // one per live override entry, _MaskValue baked in
    private OutlinePass _pass;
    private bool _warnedNotConfigured;

    public override void Create()
    {
        // Fallback auto-wire when no shader was assigned by hand.
        if (_shader == null)
            _shader = Shader.Find("Hidden/MeatballMadness/ScreenOutlineOkLab");
        if (_maskShader == null)
            _maskShader = Shader.Find("Hidden/MeatballMadness/OutlineMaskWrite");
        _pass = new OutlinePass();
    }

    /// <summary>
    /// Rebuilds the per-override mask materials and pushes their current width
    /// multipliers. Returns the number of usable overrides (layer mask non-empty).
    /// </summary>
    private int EnsureMaskMaterials(out float maxMultiplier)
    {
        maxMultiplier = 1f;
        int wanted = _objectOverrides != null ? _objectOverrides.Count : 0;
        if (wanted == 0 || _maskShader == null)
            return 0;

        if (_maskMaterials == null || _maskMaterials.Length != wanted)
        {
            if (_maskMaterials != null)
                foreach (Material m in _maskMaterials)
                    CoreUtils.Destroy(m);
            _maskMaterials = new Material[wanted];
        }

        for (int i = 0; i < wanted; i++)
        {
            if (_maskMaterials[i] == null)
                _maskMaterials[i] = CoreUtils.CreateEngineMaterial(_maskShader);
            float mul = Mathf.Max(0f, _objectOverrides[i].widthMultiplier);
            _maskMaterials[i].SetFloat(MaskValueId, mul);
            maxMultiplier = Mathf.Max(maxMultiplier, mul);
        }
        return wanted;
    }

    private static readonly int MaskValueId = Shader.PropertyToID("_MaskValue");

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        CameraType cameraType = renderingData.cameraData.cameraType;
        if (cameraType == CameraType.Preview || cameraType == CameraType.Reflection)
            return;

        bool hasDepthPalette = _depthOutlinePalette != null && _depthOutlinePalette.entries != null &&
                               _depthOutlinePalette.entries.Count > 0;
        bool hasNormalPalette = _normalOutlinePalette != null && _normalOutlinePalette.entries != null &&
                                _normalOutlinePalette.entries.Count > 0;
        if (_shader == null || (!hasDepthPalette && !hasNormalPalette))
        {
            if (!_warnedNotConfigured)
            {
                _warnedNotConfigured = true;
                Debug.LogWarning($"[{nameof(ScreenOutlineFeature)}] Effect is inactive: " +
                                 (_shader == null
                                     ? "no shader assigned and Hidden/MeatballMadness/ScreenOutlineOkLab was not found."
                                     : "no OutlinePalette assigned (or empty) in both the depth and normal slots."),
                                 this);
            }
            return;
        }
        _warnedNotConfigured = false;

        if (_material == null)
            _material = CoreUtils.CreateEngineMaterial(_shader);

        // Prepare the per-object mask materials (thicker/none by layer). Zero usable
        // overrides means the mask sub-pass is skipped and outlines are uniform.
        int overrideCount = EnsureMaskMaterials(out float maxMultiplier);

        _pass.renderPassEvent = _injectionPoint;
        // Ask URP for the depth texture and the depth-normals prepass.
        _pass.ConfigureInput(ScriptableRenderPassInput.Depth | ScriptableRenderPassInput.Normal);
        _pass.Setup(_material, this, overrideCount, maxMultiplier, ref renderingData);
        renderer.EnqueuePass(_pass);
    }

    protected override void Dispose(bool disposing)
    {
        CoreUtils.Destroy(_material);
        _material = null;
        if (_maskMaterials != null)
        {
            foreach (Material m in _maskMaterials)
                CoreUtils.Destroy(m);
            _maskMaterials = null;
        }
    }

    private class OutlinePass : ScriptableRenderPass
    {
        private static readonly int SceneDepthId = Shader.PropertyToID("_SceneDepth");
        private static readonly int SceneNormalsId = Shader.PropertyToID("_SceneNormals");
        private static readonly int TexelSizeId = Shader.PropertyToID("_TexelSize");
        private static readonly int DepthPaletteLabId = Shader.PropertyToID("_DepthPaletteLab");
        private static readonly int DepthPaletteRgbId = Shader.PropertyToID("_DepthPaletteRgb");
        private static readonly int DepthPaletteCountId = Shader.PropertyToID("_DepthPaletteCount");
        private static readonly int DepthSearchRadiusId = Shader.PropertyToID("_DepthSearchRadius");
        private static readonly int NormalPaletteLabId = Shader.PropertyToID("_NormalPaletteLab");
        private static readonly int NormalPaletteRgbId = Shader.PropertyToID("_NormalPaletteRgb");
        private static readonly int NormalPaletteCountId = Shader.PropertyToID("_NormalPaletteCount");
        private static readonly int NormalSearchRadiusId = Shader.PropertyToID("_NormalSearchRadius");
        private static readonly int DepthThresholdId = Shader.PropertyToID("_DepthThreshold");
        private static readonly int DepthDistanceScaleId = Shader.PropertyToID("_DepthDistanceScale");
        private static readonly int GrazingCompensationId = Shader.PropertyToID("_GrazingCompensation");
        private static readonly int NormalThresholdId = Shader.PropertyToID("_NormalThreshold");
        private static readonly int OutlineAgainstSkyId = Shader.PropertyToID("_OutlineAgainstSky");
        private static readonly int OutlineMaskId = Shader.PropertyToID("_OutlineMask");
        private static readonly int UseOutlineMaskId = Shader.PropertyToID("_UseOutlineMask");

        // URP opaque forward tags — which renderers the mask sub-pass enumerates.
        // The mask material overrides their shader, so this only picks the objects.
        private static readonly List<ShaderTagId> MaskShaderTags = new List<ShaderTagId>
        {
            new ShaderTagId("UniversalForward"),
            new ShaderTagId("UniversalForwardOnly"),
            new ShaderTagId("SRPDefaultUnlit"),
            new ShaderTagId("LightweightForward"),
        };

        // Full-length buffers: SetVectorArray locks the GPU array size on first upload.
        private readonly Vector4[] _depthLab = new Vector4[OutlinePalette.MaxEntries];
        private readonly Vector4[] _depthRgb = new Vector4[OutlinePalette.MaxEntries];
        private readonly Vector4[] _normalLab = new Vector4[OutlinePalette.MaxEntries];
        private readonly Vector4[] _normalRgb = new Vector4[OutlinePalette.MaxEntries];

        private Material _material;
        private ScreenOutlineFeature _feature;   // read at record time for overrides + mask materials
        private int _overrideCount;

        private class PassData
        {
            public Material material;
            public TextureHandle source;
            public TextureHandle depth;
            public TextureHandle normals;
            public TextureHandle mask;
            public bool useMask;
        }

        private class MaskPassData
        {
            public TextureHandle depth;
            public Material[] materials;   // for binding _SceneDepth before each draw
            public RendererListHandle[] lists;
            public int listCount;
        }

        // Reused across frames so recording allocates nothing per group.
        private RendererListHandle[] _maskLists;

        public OutlinePass()
        {
            profilingSampler = new ProfilingSampler("Screen Outline (OkLab)");
            requiresIntermediateTexture = true;
        }

        public void Setup(Material material, ScreenOutlineFeature feature, int overrideCount,
            float maxMultiplier, ref RenderingData renderingData)
        {
            _material = material;
            _feature = feature;
            _overrideCount = overrideCount;

            int depthCount = FillPaletteBuffers(feature._depthOutlinePalette, _depthLab, _depthRgb,
                maxMultiplier, out int depthRadius);
            _material.SetVectorArray(DepthPaletteLabId, _depthLab);
            _material.SetVectorArray(DepthPaletteRgbId, _depthRgb);
            _material.SetInteger(DepthPaletteCountId, depthCount);
            _material.SetInteger(DepthSearchRadiusId, depthRadius);

            int normalCount = FillPaletteBuffers(feature._normalOutlinePalette, _normalLab, _normalRgb,
                maxMultiplier, out int normalRadius);
            _material.SetVectorArray(NormalPaletteLabId, _normalLab);
            _material.SetVectorArray(NormalPaletteRgbId, _normalRgb);
            _material.SetInteger(NormalPaletteCountId, normalCount);
            _material.SetInteger(NormalSearchRadiusId, normalRadius);

            // Thresholds live on the palette assets. Depth-edge parameters come from
            // the Depth-slot palette; if none is assigned, fall back to the Normal-slot
            // palette, because the normal pass still needs a depth threshold to avoid
            // reading creases across silhouette edges. AddRenderPasses guarantees at
            // least one palette is assigned before Setup runs.
            OutlinePalette depthParams = feature._depthOutlinePalette != null
                ? feature._depthOutlinePalette
                : feature._normalOutlinePalette;
            OutlinePalette normalParams = feature._normalOutlinePalette != null
                ? feature._normalOutlinePalette
                : feature._depthOutlinePalette;

            _material.SetFloat(DepthThresholdId, depthParams.depthThreshold);
            _material.SetFloat(DepthDistanceScaleId, depthParams.depthDistanceScale);
            _material.SetFloat(GrazingCompensationId, depthParams.grazingAngleCompensation);
            _material.SetFloat(NormalThresholdId, normalParams.normalThreshold);
            _material.SetInteger(OutlineAgainstSkyId, feature._outlineAgainstSky ? 1 : 0);

            // Pixel step for the edge-search kernels. Uses the camera target size,
            // which matches the depth/normals textures (note: assumes Render Scale 1
            // when injected after post-processing; a mismatch only skews widths).
            RenderTextureDescriptor camDesc = renderingData.cameraData.cameraTargetDescriptor;
            _material.SetVector(TexelSizeId, new Vector4(
                1f / camDesc.width, 1f / camDesc.height, camDesc.width, camDesc.height));
        }

        /// <summary>Uploads palette entries as OkLab (xyz) + width (w) and linear RGB. Returns live entry count.</summary>
        private static int FillPaletteBuffers(OutlinePalette palette, Vector4[] lab, Vector4[] rgb,
            float maxMultiplier, out int searchRadius)
        {
            searchRadius = 1;
            if (palette == null || palette.entries == null)
                return 0;

            int count = Mathf.Min(palette.entries.Count, OutlinePalette.MaxEntries);
            float maxWidth = 0f;
            for (int i = 0; i < count; i++)
            {
                OutlinePalette.Entry entry = palette.entries[i];
                Color linear = entry.color.linear;
                Vector3 okLab = OkLabUtil.LinearSrgbToOkLab(linear);
                lab[i] = new Vector4(okLab.x, okLab.y, okLab.z, entry.width);
                rgb[i] = new Vector4(linear.r, linear.g, linear.b, 1f);
                maxWidth = Mathf.Max(maxWidth, entry.width);
            }

            // Thick per-object overrides need a wider search window so the band can
            // actually reach its multiplied width; capped at 8 px for performance.
            searchRadius = Mathf.Clamp(Mathf.CeilToInt(maxWidth * Mathf.Max(1f, maxMultiplier)), 1, 8);
            return count;
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            var resourceData = frameData.Get<UniversalResourceData>();
            if (resourceData.isActiveTargetBackBuffer)
                return;
            if (!resourceData.cameraDepthTexture.IsValid() || !resourceData.cameraNormalsTexture.IsValid())
                return;

            TextureHandle source = resourceData.activeColorTexture;

            // ---------------- Per-object mask sub-pass (optional) ----------------
            // Paints a per-object outline width multiplier so specific layers can be
            // outlined thicker (or not at all). Skipped when no overrides are set.
            TextureHandle mask = TextureHandle.nullHandle;
            bool useMask = _overrideCount > 0 && _feature != null && _feature._maskMaterials != null;
            if (useMask)
                useMask = RecordMaskPass(renderGraph, frameData, resourceData, source, out mask);

            TextureDesc desc = renderGraph.GetTextureDesc(source);
            desc.name = "ScreenOutlineOutput";
            desc.clearBuffer = false;
            TextureHandle destination = renderGraph.CreateTexture(desc);

            using (var builder = renderGraph.AddRasterRenderPass<PassData>(
                       "Screen Outline (OkLab)", out PassData passData, profilingSampler))
            {
                passData.material = _material;
                passData.source = source;
                passData.depth = resourceData.cameraDepthTexture;
                passData.normals = resourceData.cameraNormalsTexture;
                passData.mask = mask;
                passData.useMask = useMask;

                builder.UseTexture(passData.source);
                builder.UseTexture(passData.depth);
                builder.UseTexture(passData.normals);
                if (useMask)
                    builder.UseTexture(passData.mask);
                builder.SetRenderAttachment(destination, 0);
                builder.SetRenderFunc<PassData>(static (data, context) => ExecutePass(data, context));
            }

            resourceData.cameraColor = destination;
        }

        /// <summary>
        /// Renders the configured override layers into a single-channel width-multiplier
        /// mask (default 1.0). Returns false — and leaves outlines uniform — if there is
        /// nothing valid to draw. Occlusion is resolved in the mask shader against the
        /// camera depth texture, so no depth attachment is needed here.
        /// </summary>
        private bool RecordMaskPass(RenderGraph renderGraph, ContextContainer frameData,
            UniversalResourceData resourceData, TextureHandle source, out TextureHandle mask)
        {
            mask = TextureHandle.nullHandle;

            var renderingData = frameData.Get<UniversalRenderingData>();
            var cameraData = frameData.Get<UniversalCameraData>();
            var lightData = frameData.Get<UniversalLightData>();

            List<ObjectOutlineOverride> overrides = _feature._objectOverrides;
            Material[] materials = _feature._maskMaterials;
            int count = Mathf.Min(overrides.Count, materials.Length);

            if (_maskLists == null || _maskLists.Length < count)
                _maskLists = new RendererListHandle[count];

            int live = 0;
            for (int i = 0; i < count; i++)
            {
                if (materials[i] == null || overrides[i].layers == 0)
                    continue;

                DrawingSettings drawSettings = RenderingUtils.CreateDrawingSettings(
                    MaskShaderTags, renderingData, cameraData, lightData, SortingCriteria.CommonOpaque);
                drawSettings.overrideMaterial = materials[i];
                drawSettings.overrideMaterialPassIndex = 0;

                FilteringSettings filter = new FilteringSettings(RenderQueueRange.opaque, overrides[i].layers);
                _maskLists[live++] = renderGraph.CreateRendererList(
                    new RendererListParams(renderingData.cullResults, drawSettings, filter));
            }

            if (live == 0)
                return false;

            TextureDesc maskDesc = renderGraph.GetTextureDesc(source);
            maskDesc.name = "OutlineObjectMask";
            maskDesc.format = GraphicsFormat.R16_SFloat;   // float so multipliers > 1 aren't clamped
            maskDesc.depthBufferBits = DepthBits.None;
            maskDesc.msaaSamples = MSAASamples.None;
            maskDesc.clearBuffer = true;
            maskDesc.clearColor = new Color(1f, 0f, 0f, 0f); // R = 1.0 => default outline width
            mask = renderGraph.CreateTexture(maskDesc);

            using (var builder = renderGraph.AddRasterRenderPass<MaskPassData>(
                       "Outline Object Mask", out MaskPassData passData, maskProfilingSampler))
            {
                passData.depth = resourceData.cameraDepthTexture;
                passData.materials = materials;
                passData.lists = _maskLists;
                passData.listCount = live;

                builder.UseTexture(passData.depth);
                for (int i = 0; i < live; i++)
                    builder.UseRendererList(_maskLists[i]);
                builder.SetRenderAttachment(mask, 0);
                builder.SetRenderFunc<MaskPassData>(static (data, context) => ExecuteMaskPass(data, context));
            }
            return true;
        }

        private static readonly ProfilingSampler maskProfilingSampler = new ProfilingSampler("Outline Object Mask");

        private static void ExecuteMaskPass(MaskPassData data, RasterGraphContext context)
        {
            // The mask shader samples _SceneDepth to reject occluded fragments; bind it
            // on the override materials (raster command buffers have no SetGlobalTexture).
            RTHandle depth = data.depth;
            if (data.materials != null)
                foreach (Material m in data.materials)
                    if (m != null)
                        m.SetTexture(SceneDepthId, depth);

            for (int i = 0; i < data.listCount; i++)
                context.cmd.DrawRendererList(data.lists[i]);
        }

        private static void ExecutePass(PassData data, RasterGraphContext context)
        {
            // TextureHandle -> RTHandle is only valid inside pass execution.
            RTHandle depth = data.depth;
            RTHandle normals = data.normals;
            data.material.SetTexture(SceneDepthId, depth);
            data.material.SetTexture(SceneNormalsId, normals);

            if (data.useMask)
            {
                RTHandle maskTex = data.mask;
                data.material.SetTexture(OutlineMaskId, maskTex);
                data.material.SetInteger(UseOutlineMaskId, 1);
            }
            else
            {
                data.material.SetInteger(UseOutlineMaskId, 0);
            }

            RTHandle source = data.source;
            Blitter.BlitTexture(context.cmd, source, new Vector4(1f, 1f, 0f, 0f), data.material, 0);
        }
    }
}
