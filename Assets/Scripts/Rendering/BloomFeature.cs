using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

/// <summary>
/// Screen-space bloom renderer feature. Bright fragments — measured by VALUE
/// (max channel, i.e. how close to white they read in a monochrome capture) —
/// are isolated with a soft-knee threshold, blurred through a downsample /
/// upsample mip pyramid, and added back over the scene. The blur is what makes
/// the light bleed sideways past object silhouettes and outlines.
///
/// Add it to the URP Universal Renderer asset's Renderer Features list. To get
/// the bleed to spill OVER the black outlines, order this BELOW (after)
/// ScreenOutlineFeature so bloom composites on top of the already-outlined image.
/// </summary>
public class BloomFeature : ScriptableRendererFeature
{
    [Tooltip("Value cutoff (max of r,g,b). Fragments brighter than this bloom. " +
             "In a non-HDR scene, ~0.7–0.85 catches near-white highlights only.")]
    [Range(0f, 2f)]
    [SerializeField] private float _threshold = 0.75f;

    [Tooltip("Softness of the threshold. 0 = hard cutoff, 1 = a wide ramp so " +
             "mid-bright areas fade into the bloom instead of popping in.")]
    [Range(0f, 1f)]
    [SerializeField] private float _softKnee = 0.5f;

    [Tooltip("How strongly the bloom is added back over the scene.")]
    [Min(0f)]
    [SerializeField] private float _intensity = 1f;

    [Tooltip("Spread of the bleed. Higher widens the upsample tent so light " +
             "reaches further outside object outlines.")]
    [Range(0f, 1f)]
    [SerializeField] private float _scatter = 0.7f;

    [Tooltip("Color the bloom is tinted with as it's added back.")]
    [SerializeField] private Color _tint = Color.white;

    [Tooltip("Number of pyramid levels. More = softer, wider bloom (and slightly " +
             "more cost). Clamped down automatically on small targets.")]
    [Range(1, 8)]
    [SerializeField] private int _iterations = 6;

    [Tooltip("Where in the frame the effect runs. Keep after outlines so the " +
             "bleed spills over them.")]
    [SerializeField] private RenderPassEvent _injectionPoint = RenderPassEvent.AfterRenderingPostProcessing;

    [Tooltip("Fullscreen bloom shader. Assign Assets/Shaders/Bloom.shader, or leave " +
             "empty and it is auto-found (Hidden/MeatballMadness/Bloom) when the feature loads.")]
    [SerializeField] private Shader _shader;

    private Material _material;
    private BloomPass _pass;
    private bool _warnedNotConfigured;

    public override void Create()
    {
        if (_shader == null)
            _shader = Shader.Find("Hidden/MeatballMadness/Bloom");
        _pass = new BloomPass();
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        CameraType cameraType = renderingData.cameraData.cameraType;
        if (cameraType == CameraType.Preview || cameraType == CameraType.Reflection)
            return;

        if (_shader == null || _intensity <= 0f)
        {
            if (_shader == null && !_warnedNotConfigured)
            {
                _warnedNotConfigured = true;
                Debug.LogWarning($"[{nameof(BloomFeature)}] Effect is inactive: no shader assigned and " +
                                 "Hidden/MeatballMadness/Bloom was not found.", this);
            }
            return;
        }
        _warnedNotConfigured = false;

        if (_material == null)
            _material = CoreUtils.CreateEngineMaterial(_shader);

        _pass.renderPassEvent = _injectionPoint;
        _pass.Setup(_material, _threshold, _softKnee, _intensity, _scatter, _tint, _iterations);
        renderer.EnqueuePass(_pass);
    }

    protected override void Dispose(bool disposing)
    {
        CoreUtils.Destroy(_material);
        _material = null;
    }

    private class BloomPass : ScriptableRenderPass
    {
        private const int PassPrefilter = 0;
        private const int PassDownsample = 1;
        private const int PassUpsample = 2;
        private const int PassComposite = 3;

        private static readonly int ThresholdId = Shader.PropertyToID("_BloomThreshold");
        private static readonly int TexelId = Shader.PropertyToID("_BloomTexel");
        private static readonly int ParamsId = Shader.PropertyToID("_BloomParams");
        private static readonly int TintId = Shader.PropertyToID("_BloomTint");
        private static readonly int HiTexId = Shader.PropertyToID("_BloomHiTex");

        private Material _material;
        private int _maxIterations;

        // Scratch lists reused each frame so recording allocates nothing.
        private readonly List<TextureHandle> _down = new List<TextureHandle>();
        private readonly List<Vector2Int> _sizes = new List<Vector2Int>();

        public BloomPass()
        {
            profilingSampler = new ProfilingSampler("Bloom");
            requiresIntermediateTexture = true;
        }

        public void Setup(Material material, float threshold, float softKnee, float intensity,
                          float scatter, Color tint, int iterations)
        {
            _material = material;
            _maxIterations = iterations;

            // Quadratic soft-knee curve (matches Unity's bloom prefilter).
            float knee = threshold * softKnee + 1e-5f;
            var curve = new Vector4(threshold, threshold - knee, knee * 2f, 0.25f / knee);
            _material.SetVector(ThresholdId, curve);
            _material.SetVector(ParamsId, new Vector4(intensity, scatter, 0f, 0f));
            _material.SetColor(TintId, tint);
        }

        private class PassData
        {
            public TextureHandle source;
            public TextureHandle hiTex;
            public bool hasHi;
            public Material material;
            public int pass;
            public Vector4 texel;
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            var resourceData = frameData.Get<UniversalResourceData>();
            if (resourceData.isActiveTargetBackBuffer)
                return;

            TextureHandle source = resourceData.activeColorTexture;
            TextureDesc srcDesc = renderGraph.GetTextureDesc(source);

            int fullW = srcDesc.width;
            int fullH = srcDesc.height;
            if (fullW < 4 || fullH < 4)
                return;

            _down.Clear();
            _sizes.Clear();

            // Pyramid starts at half resolution and halves each level.
            int w = Mathf.Max(1, fullW >> 1);
            int h = Mathf.Max(1, fullH >> 1);

            // Level 0: prefilter (threshold + isolate bright value) from full-res scene.
            TextureHandle mip0 = CreateMip(renderGraph, srcDesc, w, h, "BloomMip0");
            AddPass(renderGraph, "Bloom.Prefilter", source, default, false, mip0,
                    PassPrefilter, TexelOf(fullW, fullH));
            _down.Add(mip0);
            _sizes.Add(new Vector2Int(w, h));

            // Downsample chain.
            for (int i = 1; i < _maxIterations; i++)
            {
                int nw = Mathf.Max(1, w >> 1);
                int nh = Mathf.Max(1, h >> 1);
                if (nw < 2 || nh < 2)
                    break;

                TextureHandle mip = CreateMip(renderGraph, srcDesc, nw, nh, $"BloomMip{i}");
                AddPass(renderGraph, "Bloom.Downsample", _down[i - 1], default, false, mip,
                        PassDownsample, TexelOf(w, h));
                _down.Add(mip);
                _sizes.Add(new Vector2Int(nw, nh));
                w = nw;
                h = nh;
            }

            // Upsample chain: fold each small mip back onto the next-larger one.
            int last = _down.Count - 1;
            TextureHandle current = _down[last];
            for (int i = last - 1; i >= 0; i--)
            {
                Vector2Int size = _sizes[i];
                Vector2Int lowSize = _sizes[i + 1];
                TextureHandle up = CreateMip(renderGraph, srcDesc, size.x, size.y, $"BloomUp{i}");
                // source = smaller mip to blur/upscale, hiTex = this level's downsampled mip.
                AddPass(renderGraph, "Bloom.Upsample", current, _down[i], true, up,
                        PassUpsample, TexelOf(lowSize.x, lowSize.y));
                current = up;
            }

            // Composite the finished bloom over the scene into a fresh full-res target.
            TextureDesc outDesc = srcDesc;
            outDesc.name = "BloomOutput";
            outDesc.clearBuffer = false;
            TextureHandle destination = renderGraph.CreateTexture(outDesc);
            AddPass(renderGraph, "Bloom.Composite", source, current, true, destination,
                    PassComposite, TexelOf(fullW, fullH));

            resourceData.cameraColor = destination;
        }

        private TextureHandle CreateMip(RenderGraph rg, TextureDesc baseDesc, int width, int height, string name)
        {
            TextureDesc desc = baseDesc;
            desc.width = width;
            desc.height = height;
            desc.name = name;
            desc.clearBuffer = false;
            desc.depthBufferBits = DepthBits.None;
            desc.msaaSamples = MSAASamples.None;
            desc.filterMode = FilterMode.Bilinear;
            desc.wrapMode = TextureWrapMode.Clamp;
            return rg.CreateTexture(desc);
        }

        private static Vector4 TexelOf(int width, int height)
        {
            return new Vector4(1f / width, 1f / height, width, height);
        }

        private void AddPass(RenderGraph renderGraph, string name, TextureHandle source,
                             TextureHandle hiTex, bool hasHi, TextureHandle dest, int pass, Vector4 texel)
        {
            using var builder = renderGraph.AddRasterRenderPass<PassData>(name, out var data);

            data.source = source;
            data.hiTex = hiTex;
            data.hasHi = hasHi;
            data.material = _material;
            data.pass = pass;
            data.texel = texel;

            builder.UseTexture(source);
            if (hasHi)
                builder.UseTexture(hiTex);
            builder.SetRenderAttachment(dest, 0);
            // Execute() binds per-pass texel size and the hi-mip via SetGlobal*,
            // which raster passes reject unless global-state changes are opted in.
            builder.AllowGlobalStateModification(true);
            builder.SetRenderFunc((PassData d, RasterGraphContext ctx) => Execute(d, ctx));
        }

        private static void Execute(PassData data, RasterGraphContext ctx)
        {
            var cmd = ctx.cmd;
            cmd.SetGlobalVector(TexelId, data.texel);
            if (data.hasHi)
                cmd.SetGlobalTexture(HiTexId, data.hiTex);
            Blitter.BlitTexture(cmd, data.source, new Vector4(1f, 1f, 0f, 0f), data.material, data.pass);
        }
    }
}
