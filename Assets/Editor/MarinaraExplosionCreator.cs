using UnityEditor;
using UnityEngine;

/// <summary>
/// Run via  Meatball Madness → Create Marinara Explosion Prefab.
/// Creates:
///   Assets/Art Assets/MarinaraTrail/MarinaraParticle.mat  (URP Particles/Unlit, splatter texture)
///   Assets/Prefabs/MarinaraExplosion.prefab
/// </summary>
public static class MarinaraExplosionCreator
{
    private const string SplatTexturePath =
        "Assets/Art Assets/MarinaraTrail/Marinara sauce splatter on checkerboard.png";
    private const string MarinaraMatPath =
        "Assets/Art Assets/MarinaraTrail/Marinara.mat";
    private const string ParticleMatPath =
        "Assets/Art Assets/MarinaraTrail/MarinaraParticle.mat";
    private const string PrefabPath =
        "Assets/Prefabs/MarinaraExplosion.prefab";

    [MenuItem("Meatball Madness/Create Marinara Explosion Prefab")]
    static void CreatePrefab()
    {
        // ── 1. Load source assets ─────────────────────────────────────────────
        var splatTexture = AssetDatabase.LoadAssetAtPath<Texture2D>(SplatTexturePath);
        if (splatTexture == null)
            Debug.LogWarning($"[MarinaraExplosionCreator] Could not find texture at {SplatTexturePath}");

        // ── 2. Create a URP Particles/Unlit material ──────────────────────────
        var particleMat = AssetDatabase.LoadAssetAtPath<Material>(ParticleMatPath);
        if (particleMat == null)
        {
            var shader = Shader.Find("Universal Render Pipeline/Particles/Unlit");
            if (shader == null)
            {
                Debug.LogError("[MarinaraExplosionCreator] 'Universal Render Pipeline/Particles/Unlit' " +
                               "shader not found. Make sure URP is installed.");
                return;
            }

            particleMat = new Material(shader) { name = "MarinaraParticle" };

            // Transparent alpha-blend
            particleMat.SetFloat("_Surface",  1f);   // 0 = Opaque, 1 = Transparent
            particleMat.SetFloat("_Blend",    0f);   // 0 = Alpha
            particleMat.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
            particleMat.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            particleMat.SetFloat("_ZWrite",   0f);
            particleMat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            particleMat.renderQueue = 3000;

            // Splatter texture from MarinaraTrail folder
            if (splatTexture != null)
            {
                particleMat.mainTexture = splatTexture;
                particleMat.SetTexture("_BaseMap", splatTexture);
            }

            // Warm tomato-orange tint — bright like real marinara, not dark/blood-like.
            particleMat.SetColor("_BaseColor", new Color(0.97f, 0.38f, 0.10f, 1f));

            AssetDatabase.CreateAsset(particleMat, ParticleMatPath);
        }

        // ── 3. Build the GameObject ───────────────────────────────────────────
        var go = new GameObject("MarinaraExplosion");

        // ParticleSystem — added first so RequireComponent is satisfied
        var ps = go.AddComponent<ParticleSystem>();

        // Silence the default looping play-on-awake so the script controls it
        var main = ps.main;
        main.loop        = false;
        main.playOnAwake = false;
        main.duration    = 0.2f;    // script overrides most settings in Awake anyway

        // Particle system renderer — assign the particle material
        var psr = go.GetComponent<ParticleSystemRenderer>();
        psr.material = particleMat;

        // MarinaraExplosion script
        var script = go.AddComponent<MarinaraExplosion>();
        var so = new SerializedObject(script);
        so.FindProperty("particleMaterial").objectReferenceValue = particleMat;
        if (splatTexture != null)
            so.FindProperty("splatTexture").objectReferenceValue = splatTexture;
        so.ApplyModifiedPropertiesWithoutUndo();

        // ── 4. Save as prefab ─────────────────────────────────────────────────
        var prefab = PrefabUtility.SaveAsPrefabAsset(go, PrefabPath);
        Object.DestroyImmediate(go);
        AssetDatabase.Refresh();

        Debug.Log($"[MarinaraExplosionCreator] Created prefab at {PrefabPath}");
        EditorGUIUtility.PingObject(prefab);
    }
}
