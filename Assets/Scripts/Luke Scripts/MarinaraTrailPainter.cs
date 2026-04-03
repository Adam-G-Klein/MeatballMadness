using UnityEngine;

[RequireComponent(typeof(Rigidbody))]
public class MarinaraTrailPainter : MonoBehaviour
{
    [Header("Stamp Timing")]
    [SerializeField] private float minDistanceBetweenStamps = 0.18f;
    [SerializeField] private float minTimeBetweenStamps = 0.04f;
    [SerializeField] private float minSurfaceSpeedToPaint = 0.35f;

    [Header("Stamp Placement")]
    [SerializeField] private float surfaceOffset = 0.01f;
    [SerializeField] private float stampDepth = 0.22f;
    [SerializeField] private LayerMask paintableLayers = ~0;

    [Header("Stamp Size")]
    [SerializeField] private float minStampSize = 0.22f;
    [SerializeField] private float maxStampSize = 0.36f;

    [Header("Opacity")]
    [SerializeField] private float minOpacity = 0.82f;
    [SerializeField] private float maxOpacity = 1f;

    [Header("Player Visual Variation")]
    [SerializeField] private Material playerSpecificDecalMaterial;

    [Header("Parenting")]
    [SerializeField] private bool parentDecalsToHitObject = true;

    private Rigidbody rb;
    private Vector3 lastStampPosition;
    private float lastStampTime = -999f;
    private bool hasStampedOnce;

    private void Awake()
    {
        rb = GetComponent<Rigidbody>();
    }

    private void OnCollisionStay(Collision collision)
    {
        if (((1 << collision.gameObject.layer) & paintableLayers) == 0)
            return;

        if (collision.contactCount == 0)
            return;

        Vector3 velocity = rb.linearVelocity;
        if (velocity.magnitude < minSurfaceSpeedToPaint)
            return;

        ContactPoint bestContact = collision.GetContact(0);

        float bestScore = -999f;
        for (int i = 0; i < collision.contactCount; i++)
        {
            ContactPoint cp = collision.GetContact(i);

            float score = Vector3.Dot(velocity.normalized, -cp.normal);
            if (score > bestScore)
            {
                bestScore = score;
                bestContact = cp;
            }
        }

        Transform hitParent = collision.collider != null ? collision.collider.transform : null;
        TryPlaceStamp(bestContact.point, bestContact.normal, hitParent);
    }

    private void TryPlaceStamp(Vector3 point, Vector3 normal, Transform hitParent)
    {
        if (Time.time - lastStampTime < minTimeBetweenStamps)
            return;

        Vector3 stampPosition = point + normal * surfaceOffset;

        if (hasStampedOnce)
        {
            float dist = Vector3.Distance(lastStampPosition, stampPosition);
            if (dist < minDistanceBetweenStamps)
                return;
        }

        MarinaraDecalInstance decal = MarinaraTrailPool.Instance.Get();

        float size = Random.Range(minStampSize, maxStampSize);
        float opacity = Random.Range(minOpacity, maxOpacity);
        float randomAngle = Random.Range(0f, 360f);

        decal.ApplyStamp(
            stampPosition,
            normal,
            size,
            stampDepth,
            opacity,
            randomAngle,
            playerSpecificDecalMaterial
        );

        if (parentDecalsToHitObject && hitParent != null)
        {
            decal.transform.SetParent(hitParent, true);
        }
        else
        {
            decal.transform.SetParent(null, true);
        }

        lastStampPosition = stampPosition;
        lastStampTime = Time.time;
        hasStampedOnce = true;
    }
}