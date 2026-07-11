using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.RenderGraphModule.Util;
using UnityEngine.Rendering.Universal;

/// <summary>
/// Screen-space "derez" pixelation renderer feature. Equivalent to rendering the
/// camera image into a small render texture and stretching it back over the
/// screen with point filtering — but done as a fullscreen pass so it slots into
/// the URP feature list. Place it ABOVE ScreenOutlineFeature (same injection
/// point) so the outline runs on the pixelated image. Add it to the URP
/// Universal Renderer asset's Renderer Features list.
/// </summary>
public class DerezFeature : ScriptableRendererFeature
{
    [Tooltip("Screen pixels per block. 1 = no effect, 4 = 4x4 chunky pixels, higher = coarser. " +
             "Blocks are computed from the camera target size so the look is resolution-independent.")]
    [Range(1, 64)]
    [SerializeField] private int _pixelSize = 4;

    [Tooltip("Where in the frame the effect runs. Keep at the same event as ScreenOutlineFeature and " +
             "order this ABOVE it in the Renderer Features list so the outline sees the derez output.")]
    [SerializeField] private RenderPassEvent _injectionPoint = RenderPassEvent.AfterRenderingPostProcessing;

    [Tooltip("Fullscreen derez shader. Assign Assets/Shaders/DerezSnap.shader manually, or leave " +
             "empty and it is auto-found (Hidden/MeatballMadness/DerezSnap) when the feature loads.")]
    [SerializeField] private Shader _shader;

    // Shared with ScreenOutlineOkLab.shader: the block grid the outline snaps its
    // depth/normal edge detection to. Set globally each frame from here.
    private static readonly int DerezParamsId = Shader.PropertyToID("_DerezParams");

    private Material _material;
    private DerezPass _pass;
    private bool _warnedNotConfigured;

    public override void Create()
    {
        // Fallback auto-wire when no shader was assigned by hand; once serialized
        // here it is referenced by the renderer asset and included in builds.
        if (_shader == null)
            _shader = Shader.Find("Hidden/MeatballMadness/DerezSnap");
        _pass = new DerezPass();
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        CameraType cameraType = renderingData.cameraData.cameraType;
        if (cameraType == CameraType.Preview || cameraType == CameraType.Reflection)
            return;

        // pixelSize 1 is a no-op; skip the pass entirely so we don't pay a blit for nothing.
        if (_shader == null || _pixelSize <= 1)
        {
            // Clear the shared grid so ScreenOutlineFeature stops snapping its edge
            // detection to a stale derez grid when the effect is off.
            Shader.SetGlobalVector(DerezParamsId, Vector4.zero);
            if (_shader == null && !_warnedNotConfigured)
            {
                _warnedNotConfigured = true;
                Debug.LogWarning($"[{nameof(DerezFeature)}] Effect is inactive: no shader assigned and " +
                                 "Hidden/MeatballMadness/DerezSnap was not found.", this);
            }
            return;
        }
        _warnedNotConfigured = false;

        if (_material == null)
            _material = CoreUtils.CreateEngineMaterial(_shader);

        // Block count from the camera target size — the same source ScreenOutlineFeature
        // uses for its texel size — so the derez fill and the outline's grid snapping
        // agree on where block boundaries fall. Blocks live in UV space, so this stays
        // resolution-independent (assumes render scale 1, like the outline's texel step).
        RenderTextureDescriptor camDesc = renderingData.cameraData.cameraTargetDescriptor;
        float blocksX = Mathf.Max(1f, camDesc.width / (float)_pixelSize);
        float blocksY = Mathf.Max(1f, camDesc.height / (float)_pixelSize);
        Vector4 derezParams = new Vector4(blocksX, blocksY, 0f, 0f);

        // Publish the grid so the outline shader can snap depth/normal sampling to it,
        // keeping outlines flush against the pixelated fill instead of the true edge.
        Shader.SetGlobalVector(DerezParamsId, derezParams);

        _pass.renderPassEvent = _injectionPoint;
        _pass.Setup(_material, derezParams);
        renderer.EnqueuePass(_pass);
    }

    protected override void Dispose(bool disposing)
    {
        CoreUtils.Destroy(_material);
        _material = null;
    }

    private class DerezPass : ScriptableRenderPass
    {
        private Material _material;

        public DerezPass()
        {
            profilingSampler = new ProfilingSampler("Derez");
            requiresIntermediateTexture = true;
        }

        public void Setup(Material material, Vector4 derezParams)
        {
            _material = material;
            _material.SetVector(DerezParamsId, derezParams);
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            var resourceData = frameData.Get<UniversalResourceData>();
            if (resourceData.isActiveTargetBackBuffer)
                return;

            // Can't read and write the same texture in one pass: blit through a fresh
            // target with the derez material, then make that the camera color.
            TextureHandle source = resourceData.activeColorTexture;
            TextureDesc desc = renderGraph.GetTextureDesc(source);

            desc.name = "DerezOutput";
            desc.clearBuffer = false;
            TextureHandle destination = renderGraph.CreateTexture(desc);

            var blitParams = new RenderGraphUtils.BlitMaterialParameters(source, destination, _material, 0);
            renderGraph.AddBlitPass(blitParams, passName: "Derez");

            resourceData.cameraColor = destination;
        }
    }
}
