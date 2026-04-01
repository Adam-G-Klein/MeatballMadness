using UnityEngine;

[RequireComponent(typeof(Collider))]
public class TrampolineBounce_Debug : MonoBehaviour
{
    [Header("Bounce")]
    [SerializeField] private float bounceStrength = 30f;

    [Header("Debug")]
    [SerializeField] private bool enableLogs = true;
    [SerializeField] private bool drawDebug = true;

    private void OnCollisionStay(Collision collision)
    {
        if (enableLogs)
            Debug.Log($"[TRAMPOLINE] Collision with: {collision.gameObject.name}");

        Rigidbody rb = GetRigidbodyFromCollision(collision);

        if (rb == null)
        {
            if (enableLogs)
                Debug.Log("[TRAMPOLINE] ❌ No Rigidbody found");
            return;
        }

        if (enableLogs)
            Debug.Log($"[TRAMPOLINE] ✅ Rigidbody found: {rb.name}");

        // Draw contact points
        for (int i = 0; i < collision.contactCount; i++)
        {
            ContactPoint contact = collision.GetContact(i);

            if (drawDebug)
            {
                Debug.DrawRay(contact.point, Vector3.up * 0.5f, Color.green);
                Debug.DrawRay(contact.point, contact.normal, Color.yellow);
            }
        }

        // Simple "on top" check using position
        if (rb.worldCenterOfMass.y < transform.position.y)
        {
            if (enableLogs)
                Debug.Log("[TRAMPOLINE] ❌ Object is below trampoline, ignoring");
            return;
        }

        if (enableLogs)
            Debug.Log("[TRAMPOLINE] ✅ Bounce triggered!");

        // Reset vertical velocity for consistent bounce
        Vector3 velocity = rb.linearVelocity;
        velocity = new Vector3(velocity.x, 0f, velocity.z);
        rb.linearVelocity = velocity;

        // Apply bounce
        rb.AddForce(transform.up * bounceStrength, ForceMode.VelocityChange);
    }

    private Rigidbody GetRigidbodyFromCollision(Collision collision)
    {
        if (collision.rigidbody != null)
            return collision.rigidbody;

        if (collision.collider != null)
        {
            Rigidbody rb = collision.collider.GetComponentInParent<Rigidbody>();
            if (rb != null)
                return rb;
        }

        for (int i = 0; i < collision.contactCount; i++)
        {
            ContactPoint contact = collision.GetContact(i);

            if (contact.otherCollider != null)
            {
                Rigidbody rb = contact.otherCollider.GetComponentInParent<Rigidbody>();
                if (rb != null)
                    return rb;
            }
        }

        return null;
    }
}