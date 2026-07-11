using UnityEngine;

/// <summary>
/// A one-shot marinara-sauce explosion particle effect.
/// 10× the diameter of PlayerMeatball (scale 2 ≈ 2 units → 20 unit spread).
/// Attach to a GameObject alongside a ParticleSystem.
/// Call Play() to trigger, or enable playOnAwake in the inspector.
/// The GameObject self-destructs after the effect completes.
/// </summary>
[RequireComponent(typeof(ParticleSystem))]
public class MarinaraExplosion : MonoBehaviour
{
    [Tooltip("Particle material. Leave null to auto-generate from splatTexture at runtime.")]
    [SerializeField] private Material particleMaterial;

    [Tooltip("Marinara sauce splatter texture used when particleMaterial is not set.")]
    [SerializeField] private Texture2D splatTexture;

    private ParticleSystem _ps;

    void Awake()
    {
        _ps = GetComponent<ParticleSystem>();
        Configure();
    }

    void Start()
    {
        _ps.Play();

        var main = _ps.main;
        float lifetime = main.duration + main.startLifetime.constantMax;
        Destroy(gameObject, lifetime + 0.5f);
    }

    void Update()
    {
        if (Camera.main != null)
            transform.LookAt(transform.position + Camera.main.transform.rotation * Vector3.forward);
    }

    /// <summary>Spawn a copy of this prefab at a world position and play it.</summary>
    public static void SpawnAt(Vector3 worldPosition, GameObject prefab)
    {
        Instantiate(prefab, worldPosition, Quaternion.identity);
    }

    private void Configure()
    {
        // The prefab may have Play On Awake enabled, so the system can already be
        // running when Awake() calls Configure(). Changing main.duration on a live
        // system throws, so stop and clear it before reconfiguring.
        _ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

        // ── Main ─────────────────────────────────────────────────────────────
        // PlayerMeatball scale = 2 (≈ 2-unit diameter sphere).
        // 10× that = 20 units. Speed + lifetime combo gives ~10-40 unit spread.
        var main = _ps.main;
        main.duration = 0.2f;
        main.loop = true;
        main.playOnAwake = false;
        main.startLifetime    = new ParticleSystem.MinMaxCurve(1.5f, 2.8f);
        main.startSpeed       = new ParticleSystem.MinMaxCurve(6f, 18f);
        main.startSize        = new ParticleSystem.MinMaxCurve(4f, 9f);
        main.startRotation    = new ParticleSystem.MinMaxCurve(-Mathf.PI, Mathf.PI);
        // Bright tomato-orange (highlight) → warm tomato red (shadow), matching real sauce.
        main.startColor       = new ParticleSystem.MinMaxGradient(
            new Color(0.97f, 0.45f, 0.12f, 1f),   // bright orange-red highlight
            new Color(0.85f, 0.20f, 0.06f, 1f));  // saturated tomato red
        main.gravityModifier  = new ParticleSystem.MinMaxCurve(0.35f, 0.65f);
        main.simulationSpace  = ParticleSystemSimulationSpace.World;
        main.maxParticles     = 100;

        // ── Emission ─────────────────────────────────────────────────────────
        var emission = _ps.emission;
        emission.rateOverTime = 0;
        emission.SetBursts(new[] { new ParticleSystem.Burst(0f, 55, 75) });

        // ── Shape ────────────────────────────────────────────────────────────
        var shape = _ps.shape;
        shape.enabled        = true;
        shape.shapeType      = ParticleSystemShapeType.Sphere;
        shape.radius         = 1f;
        shape.radiusThickness = 1f;    // emit from full sphere volume

        // ── Size over lifetime ────────────────────────────────────────────────
        var sol = _ps.sizeOverLifetime;
        sol.enabled = true;
        var sizeCurve = new AnimationCurve(
            new Keyframe(0f,   1f,  0f,  3f),
            new Keyframe(0.15f, 1.3f),
            new Keyframe(1f,   0f,  -2f, 0f));
        sol.size = new ParticleSystem.MinMaxCurve(1f, sizeCurve);

        // ── Color over lifetime ───────────────────────────────────────────────
        var col = _ps.colorOverLifetime;
        col.enabled = true;
        var gradient = new Gradient();
        // Stays bright and orange-warm throughout; only alpha fades at the end.
        // Avoids the blood/brown look that comes from dropping the G channel too fast.
        gradient.SetKeys(
            new[]
            {
                new GradientColorKey(new Color(0.98f, 0.52f, 0.18f), 0f),   // bright orange burst
                new GradientColorKey(new Color(0.92f, 0.30f, 0.09f), 0.35f),// warm tomato
                new GradientColorKey(new Color(0.85f, 0.20f, 0.06f), 0.7f), // saturated red
                new GradientColorKey(new Color(0.78f, 0.16f, 0.05f), 1f)    // still warm, not brown
            },
            new[]
            {
                new GradientAlphaKey(1f,  0f),
                new GradientAlphaKey(1f,  0.6f),
                new GradientAlphaKey(0f,  1f)
            });
        col.color = new ParticleSystem.MinMaxGradient(gradient);

        // ── Rotation over lifetime ────────────────────────────────────────────
        var rot = _ps.rotationOverLifetime;
        rot.enabled       = true;
        rot.separateAxes  = false;
        rot.z             = new ParticleSystem.MinMaxCurve(-1.2f, 1.2f); // rad/s

        // ── Noise ─────────────────────────────────────────────────────────────
        // Gives blobs an organic, splattered trajectory rather than a clean arc.
        var noise = _ps.noise;
        noise.enabled     = true;
        noise.strength    = new ParticleSystem.MinMaxCurve(2.5f);
        noise.frequency   = 0.4f;
        noise.scrollSpeed = new ParticleSystem.MinMaxCurve(0.4f);
        noise.damping     = true;

        // ── Renderer ──────────────────────────────────────────────────────────
        SetupRenderer();
    }

    private void SetupRenderer()
    {
        var r = GetComponent<ParticleSystemRenderer>();
        r.renderMode    = ParticleSystemRenderMode.Billboard;
        r.sortingFudge  = -1f;
        r.minParticleSize = 0f;
        r.maxParticleSize = 2f;     // allow very large screen-space particles

        if (particleMaterial != null)
        {
            r.material = particleMaterial;
            return;
        }

        // Build a transparent particle material from splatTexture at runtime.
        var shader = Shader.Find("Universal Render Pipeline/Particles/Unlit");
        if (shader == null) shader = Shader.Find("Particles/Standard Unlit");

        if (shader == null)
        {
            Debug.LogWarning("[MarinaraExplosion] Could not find a particle shader. " +
                             "Assign a particleMaterial in the inspector.");
            return;
        }

        var mat = new Material(shader) { name = "MarinaraExplosion_Runtime" };

        // Transparent / alpha-blend surface
        mat.SetFloat("_Surface",   1f);
        mat.SetFloat("_Blend",     0f);
        mat.SetFloat("_SrcBlend",  (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
        mat.SetFloat("_DstBlend",  (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
        mat.SetFloat("_ZWrite",    0f);
        mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
        mat.renderQueue = 3000;

        if (splatTexture != null)
        {
            mat.mainTexture = splatTexture;
            mat.SetTexture("_BaseMap", splatTexture);
        }

        mat.SetColor("_BaseColor", new Color(0.97f, 0.38f, 0.10f, 1f)); // warm tomato-orange

        r.material = mat;
    }
}
