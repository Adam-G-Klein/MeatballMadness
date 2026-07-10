using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.RenderGraphModule.Util;
using UnityEngine.Rendering.Universal;

/// <summary>
/// Screen-space posterization renderer feature. Quantizes the camera image to
/// the closest color of the settings' PosterizePalette (OkLab distance for
/// "closest"), then applies the settings' value snaps. Add it to the URP
/// Universal Renderer asset's Renderer Features list; its position in that list
/// relative to ScreenOutlineFeature decides which effect sees the other's output
/// (both default to the same injection point).
/// </summary>
public class PosterizeFeature : ScriptableRendererFeature
{
    [Tooltip("Settings asset with the palette and value snaps (Assets > Create > Meatball Madness > " +
             "Posterize Settings). No settings, no palette, or an empty palette disables the effect.")]
    [SerializeField] private PosterizeSettings _settings;

    [Tooltip("Where in the frame the effect runs. After post-processing (default) posterizes the final " +
             "tonemapped image, so palette matching sees exactly what ends up on screen.")]
    [SerializeField] private RenderPassEvent _injectionPoint = RenderPassEvent.AfterRenderingPostProcessing;

    [Tooltip("Fullscreen posterize shader. Assign Assets/Shaders/PosterizeOkLab.shader manually, or leave " +
             "empty and it is auto-found (Hidden/MeatballMadness/PosterizeOkLab) when the feature loads.")]
    [SerializeField] private Shader _shader;

    private Material _material;
    private PosterizePass _pass;
    private bool _warnedNotConfigured;

    public override void Create()
    {
        // Fallback auto-wire when no shader was assigned by hand; once serialized
        // here it is referenced by the renderer asset and included in builds.
        if (_shader == null)
            _shader = Shader.Find("Hidden/MeatballMadness/PosterizeOkLab");
        _pass = new PosterizePass();
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        CameraType cameraType = renderingData.cameraData.cameraType;
        if (cameraType == CameraType.Preview || cameraType == CameraType.Reflection)
            return;
        bool hasPalette = _settings != null && _settings.HasPalette;
        if (!hasPalette || _shader == null)
        {
            if (!_warnedNotConfigured)
            {
                _warnedNotConfigured = true;
                Debug.LogWarning($"[{nameof(PosterizeFeature)}] Effect is inactive: " +
                                 (_shader == null
                                     ? "no shader assigned and Hidden/MeatballMadness/PosterizeOkLab was not found."
                                     : _settings == null
                                         ? "no PosterizeSettings asset assigned on the renderer feature."
                                         : "the PosterizeSettings asset has no PosterizePalette (or it has no colors)."),
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

    private class PosterizePass : ScriptableRenderPass
    {
        private Material _material;

        public PosterizePass()
        {
            profilingSampler = new ProfilingSampler("Posterize (OkLab)");
            requiresIntermediateTexture = true;
        }

        public void Setup(Material material, PosterizeSettings settings)
        {
            _material = material;
            // PosterizeSettings owns the palette + value-snap upload so posterize
            // and dither stay byte-for-byte identical.
            settings.Apply(_material);
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            var resourceData = frameData.Get<UniversalResourceData>();
            if (resourceData.isActiveTargetBackBuffer)
                return;

            // Can't read and write the same texture in one pass: blit through a
            // fresh target with the posterize material, then make that target the
            // camera color for the rest of the frame.
            TextureHandle source = resourceData.activeColorTexture;
            TextureDesc desc = renderGraph.GetTextureDesc(source);
            desc.name = "PosterizeOutput";
            desc.clearBuffer = false;
            TextureHandle destination = renderGraph.CreateTexture(desc);

            var blitParams = new RenderGraphUtils.BlitMaterialParameters(source, destination, _material, 0);
            renderGraph.AddBlitPass(blitParams, passName: "Posterize (OkLab)");

            resourceData.cameraColor = destination;
        }
    }
}
