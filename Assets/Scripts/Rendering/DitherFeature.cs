using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.RenderGraphModule.Util;
using UnityEngine.Rendering.Universal;

/// <summary>
/// Screen-space ordered (Bayer) dithering renderer feature. All configuration
/// lives on a DitherSettings asset, which nests a PosterizeSettings (palette +
/// value snaps) for the output colors. Because the dither quantizes through
/// those settings, this replaces PosterizeFeature — disable that feature while
/// this one is active. Add to the URP Universal Renderer asset's Renderer
/// Features list.
/// </summary>
public class DitherFeature : ScriptableRendererFeature
{
    [Tooltip("Settings asset (Assets > Create > Meatball Madness > Dither Settings). Colors come from the " +
             "PosterizeSettings it nests. Missing settings, posterize settings, or palette disables the effect.")]
    [SerializeField] private DitherSettings _settings;

    [Tooltip("Where in the frame the effect runs. After post-processing (default) dithers the final " +
             "tonemapped image. Keep at the same event as ScreenOutlineFeature and order them in the list.")]
    [SerializeField] private RenderPassEvent _injectionPoint = RenderPassEvent.AfterRenderingPostProcessing;

    [Tooltip("Fullscreen dither shader. Assign Assets/Shaders/DitherOkLab.shader manually, or leave " +
             "empty and it is auto-found (Hidden/MeatballMadness/DitherOkLab) when the feature loads.")]
    [SerializeField] private Shader _shader;

    private Material _material;
    private DitherPass _pass;
    private bool _warnedNotConfigured;

    public override void Create()
    {
        // Fallback auto-wire when no shader was assigned by hand; once serialized
        // here it is referenced by the renderer asset and included in builds.
        if (_shader == null)
            _shader = Shader.Find("Hidden/MeatballMadness/DitherOkLab");
        _pass = new DitherPass();
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        CameraType cameraType = renderingData.cameraData.cameraType;
        if (cameraType == CameraType.Preview || cameraType == CameraType.Reflection)
            return;

        bool hasPalette = _settings != null && _settings.posterize != null && _settings.posterize.HasPalette;
        if (!hasPalette || _shader == null)
        {
            if (!_warnedNotConfigured)
            {
                _warnedNotConfigured = true;
                Debug.LogWarning($"[{nameof(DitherFeature)}] Effect is inactive: " +
                                 (_shader == null
                                     ? "no shader assigned and Hidden/MeatballMadness/DitherOkLab was not found."
                                     : _settings == null
                                         ? "no DitherSettings asset assigned on the renderer feature."
                                         : _settings.posterize == null
                                             ? "the DitherSettings asset has no PosterizeSettings assigned."
                                             : "the nested PosterizeSettings has no PosterizePalette (or it has no colors)."),
                                 this);
            }
            return;
        }
        _warnedNotConfigured = false;

        if (_material == null)
            _material = CoreUtils.CreateEngineMaterial(_shader);

        _pass.renderPassEvent = _injectionPoint;
        _pass.Setup(_material, _settings);
        renderer.EnqueuePass(_pass);
    }

    protected override void Dispose(bool disposing)
    {
        CoreUtils.Destroy(_material);
        _material = null;
    }

    private class DitherPass : ScriptableRenderPass
    {
        private static readonly int SpreadId = Shader.PropertyToID("_Spread");
        private static readonly int BayerLevelsId = Shader.PropertyToID("_BayerLevels");
        private static readonly int PatternScaleId = Shader.PropertyToID("_PatternScale");

        private Material _material;

        public DitherPass()
        {
            profilingSampler = new ProfilingSampler("Dither (OkLab)");
            requiresIntermediateTexture = true;
        }

        public void Setup(Material material, DitherSettings settings)
        {
            _material = material;

            // Palette + value snaps come from the shared PosterizeSettings so the
            // dither quantizes to exactly what posterize would produce; the dither
            // then adds only its own Bayer perturbation on top.
            settings.posterize.Apply(_material);
            _material.SetFloat(SpreadId, settings.spread);
            _material.SetInteger(BayerLevelsId, (int)settings.bayerSize);
            _material.SetInteger(PatternScaleId, Mathf.Max(1, settings.patternScale));
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            var resourceData = frameData.Get<UniversalResourceData>();
            if (resourceData.isActiveTargetBackBuffer)
                return;

            // Can't read and write the same texture in one pass: blit through a
            // fresh target with the dither material, then make that target the
            // camera color for the rest of the frame.
            TextureHandle source = resourceData.activeColorTexture;
            TextureDesc desc = renderGraph.GetTextureDesc(source);
            desc.name = "DitherOutput";
            desc.clearBuffer = false;
            TextureHandle destination = renderGraph.CreateTexture(desc);

            var blitParams = new RenderGraphUtils.BlitMaterialParameters(source, destination, _material, 0);
            renderGraph.AddBlitPass(blitParams, passName: "Dither (OkLab)");

            resourceData.cameraColor = destination;
        }
    }
}
