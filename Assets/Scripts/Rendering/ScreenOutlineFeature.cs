using UnityEngine;
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

    [Header("Setup")]
    [Tooltip("Where in the frame the effect runs. Keep at the same event as PosterizeFeature and order " +
             "the two in the Renderer Features list.")]
    [SerializeField] private RenderPassEvent _injectionPoint = RenderPassEvent.AfterRenderingPostProcessing;

    [Tooltip("Fullscreen outline shader. Assign Assets/Shaders/ScreenOutlineOkLab.shader manually, or leave " +
             "empty and it is auto-found (Hidden/MeatballMadness/ScreenOutlineOkLab) when the feature loads.")]
    [SerializeField] private Shader _shader;

    private Material _material;
    private OutlinePass _pass;
    private bool _warnedNotConfigured;

    public override void Create()
    {
        // Fallback auto-wire when no shader was assigned by hand.
        if (_shader == null)
            _shader = Shader.Find("Hidden/MeatballMadness/ScreenOutlineOkLab");
        _pass = new OutlinePass();
    }

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

        _pass.renderPassEvent = _injectionPoint;
        // Ask URP for the depth texture and the depth-normals prepass.
        _pass.ConfigureInput(ScriptableRenderPassInput.Depth | ScriptableRenderPassInput.Normal);
        _pass.Setup(_material, this, ref renderingData);
        renderer.EnqueuePass(_pass);
    }

    protected override void Dispose(bool disposing)
    {
        CoreUtils.Destroy(_material);
        _material = null;
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

        // Full-length buffers: SetVectorArray locks the GPU array size on first upload.
        private readonly Vector4[] _depthLab = new Vector4[OutlinePalette.MaxEntries];
        private readonly Vector4[] _depthRgb = new Vector4[OutlinePalette.MaxEntries];
        private readonly Vector4[] _normalLab = new Vector4[OutlinePalette.MaxEntries];
        private readonly Vector4[] _normalRgb = new Vector4[OutlinePalette.MaxEntries];

        private Material _material;

        private class PassData
        {
            public Material material;
            public TextureHandle source;
            public TextureHandle depth;
            public TextureHandle normals;
        }

        public OutlinePass()
        {
            profilingSampler = new ProfilingSampler("Screen Outline (OkLab)");
            requiresIntermediateTexture = true;
        }

        public void Setup(Material material, ScreenOutlineFeature feature, ref RenderingData renderingData)
        {
            _material = material;

            int depthCount = FillPaletteBuffers(feature._depthOutlinePalette, _depthLab, _depthRgb,
                out int depthRadius);
            _material.SetVectorArray(DepthPaletteLabId, _depthLab);
            _material.SetVectorArray(DepthPaletteRgbId, _depthRgb);
            _material.SetInteger(DepthPaletteCountId, depthCount);
            _material.SetInteger(DepthSearchRadiusId, depthRadius);

            int normalCount = FillPaletteBuffers(feature._normalOutlinePalette, _normalLab, _normalRgb,
                out int normalRadius);
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
            out int searchRadius)
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

            searchRadius = Mathf.Clamp(Mathf.CeilToInt(maxWidth), 1, 8);
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

                builder.UseTexture(passData.source);
                builder.UseTexture(passData.depth);
                builder.UseTexture(passData.normals);
                builder.SetRenderAttachment(destination, 0);
                builder.SetRenderFunc<PassData>(static (data, context) => ExecutePass(data, context));
            }

            resourceData.cameraColor = destination;
        }

        private static void ExecutePass(PassData data, RasterGraphContext context)
        {
            // TextureHandle -> RTHandle is only valid inside pass execution.
            RTHandle depth = data.depth;
            RTHandle normals = data.normals;
            data.material.SetTexture(SceneDepthId, depth);
            data.material.SetTexture(SceneNormalsId, normals);

            RTHandle source = data.source;
            Blitter.BlitTexture(context.cmd, source, new Vector4(1f, 1f, 0f, 0f), data.material, 0);
        }
    }
}
