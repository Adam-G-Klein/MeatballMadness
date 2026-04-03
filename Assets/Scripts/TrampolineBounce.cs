using UnityEngine;

[RequireComponent(typeof(Collider))]
public class TrampolineBounce_Debug : MonoBehaviour
{
    [Header("Bounce")]
    [SerializeField] private float bounceStrength = 30f;

    [Tooltip("Direction the trampoline will launch the player in (world space).")]
    [SerializeField] private Vector3 bounceDirection = new Vector3(0f, 1f, 0f);

    [Tooltip("If true, clears velocity along the bounce direction before applying force.")]
    [SerializeField] private bool resetVelocityAlongBounceDirection = true;

    [Header("Debug")]
    [SerializeField] private bool enableLogs = true;
    [SerializeField] private bool drawDebug = true;

    private void OnCollisionEnter(Collision collision)
    {
        if (enableLogs)
            Debug.Log($"[TRAMPOLINE] Collision with: {collision.gameObject.name}");

        Rigidbody rb = GetRigidbodyFromCollision(collision);

        if (rb == null)
        {
            if (enableLogs)
                Debug.Log("[TRAMPOLINE] No Rigidbody found");
            return;
        }

        Vector3 dir = bounceDirection.normalized;

        if (drawDebug)
        {
            for (int i = 0; i < collision.contactCount; i++)
            {
                ContactPoint contact = collision.GetContact(i);
                Debug.DrawRay(contact.point, dir * 0.75f, Color.green, 1f);
            }
        }

        if (enableLogs)
            Debug.Log($"[TRAMPOLINE] Bounce triggered. Direction: {dir}");

        if (resetVelocityAlongBounceDirection)
        {
            Vector3 velocity = rb.linearVelocity;
            float velInDir = Vector3.Dot(velocity, dir);
            rb.linearVelocity = velocity - (dir * velInDir);
        }

        rb.AddForce(dir * bounceStrength, ForceMode.VelocityChange);
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