using UnityEngine;
using UnityEngine.Rendering.Universal;

[RequireComponent(typeof(DecalProjector))]
public class MarinaraDecalInstance : MonoBehaviour
{
    [Header("Optional Lifetime")]
    [SerializeField] private bool useLifetime = true;
    [SerializeField] private float lifetime = 180f;

    private MarinaraTrailPool pool;
    private DecalProjector projector;
    private float timer;
    private bool isInPool = true;

    public void SetPool(MarinaraTrailPool trailPool)
    {
        pool = trailPool;
    }

    private void Awake()
    {
        projector = GetComponent<DecalProjector>();
    }

    private void OnEnable()
    {
        isInPool = false;
        timer = lifetime;
    }

    private void Update()
    {
        if (isInPool)
            return;

        if (!useLifetime)
            return;

        timer -= Time.deltaTime;
        if (timer <= 0f)
        {
            Despawn();
        }
    }

    public void ApplyStamp(
        Vector3 worldPosition,
        Vector3 surfaceNormal,
        float size,
        float depth,
        float opacity,
        float randomAngle,
        Material materialOverride = null)
    {
        isInPool = false;
        timer = lifetime;

        transform.position = worldPosition;

        Quaternion alignToSurface = Quaternion.LookRotation(-surfaceNormal, Vector3.up);
        transform.rotation = alignToSurface * Quaternion.Euler(0f, 0f, randomAngle);

        projector.size = new Vector3(size, size, depth);
        projector.fadeFactor = opacity;

        if (materialOverride != null)
        {
            projector.material = materialOverride;
        }
    }

    public void Despawn()
    {
        if (isInPool)
            return;

        isInPool = true;
        pool.ReturnToPool(this);
    }

    private void OnDisable()
    {
        isInPool = true;
    }
}