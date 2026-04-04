using Unity.Netcode;
using UnityEngine;

[RequireComponent(typeof(Rigidbody))]
public class MarinaraTrailPainter : NetworkBehaviour
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

    // Sentinel value meaning "no network parent"
    private const ulong NoParent = ulong.MaxValue;

    private void Awake()
    {
        rb = GetComponent<Rigidbody>();
    }

    // Only the server runs collision-based detection
    private void OnCollisionStay(Collision collision)
    {
        if (!IsServer) return;

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
        TryPlaceStampServer(bestContact.point, bestContact.normal, hitParent);
    }

    // Runs on server only: throttle, compute random values, broadcast to all clients
    private void TryPlaceStampServer(Vector3 point, Vector3 normal, Transform hitParent)
    {
        if (Time.time - lastStampTime < minTimeBetweenStamps)
            return;

        Vector3 stampPosition = point + normal * surfaceOffset;

        if (hasStampedOnce && Vector3.Distance(lastStampPosition, stampPosition) < minDistanceBetweenStamps)
            return;

        float size = Random.Range(minStampSize, maxStampSize);
        float opacity = Random.Range(minOpacity, maxOpacity);
        float randomAngle = Random.Range(0f, 360f);

        // Resolve hit parent to a NetworkObjectId so clients can find it
        ulong parentNetId = NoParent;
        if (parentDecalsToHitObject && hitParent != null)
        {
            if (hitParent.TryGetComponent(out NetworkObject netObj))
                parentNetId = netObj.NetworkObjectId;
        }

        PlaceStampClientRpc(stampPosition, normal, size, stampDepth, opacity, randomAngle, parentNetId);

        lastStampPosition = stampPosition;
        lastStampTime = Time.time;
        hasStampedOnce = true;
    }

    // Runs on all clients (including host): pull a decal from the pool and apply it
    [ClientRpc]
    private void PlaceStampClientRpc(
        Vector3 position, Vector3 normal,
        float size, float depth,
        float opacity, float angle,
        ulong parentNetId)
    {
        MarinaraDecalInstance decal = MarinaraTrailPool.Instance.Get();
        decal.ApplyStamp(position, normal, size, depth, opacity, angle, playerSpecificDecalMaterial);

        Transform parent = null;
        if (parentNetId != NoParent &&
            NetworkManager.Singleton.SpawnManager.SpawnedObjects.TryGetValue(parentNetId, out NetworkObject netObj))
        {
            parent = netObj.transform;
        }

        decal.transform.SetParent(parent, true);
    }
}
