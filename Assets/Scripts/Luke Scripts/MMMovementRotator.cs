using UnityEngine;

[RequireComponent(typeof(MMMovement))]
public class MMMovementRotator : MonoBehaviour
{
    [Header("Rotation")]
    [Tooltip("Degrees per second on each axis.")]
    [SerializeField] private Vector3 rotationSpeed = new Vector3(0f, 90f, 0f);

    [Tooltip("If false, rotation will not run.")]
    [SerializeField] private bool rotateObject = true;

    private MMMovement movement;

    private void Awake()
    {
        movement = GetComponent<MMMovement>();
    }

    private void FixedUpdate()
    {
        if (!rotateObject)
            return;

        // MMMovement only actually applies MoveRotation on the server/host
        if (!movement.IsServer)
            return;

        Vector3 deltaEuler = rotationSpeed * Time.fixedDeltaTime;
        Quaternion deltaRotation = Quaternion.Euler(deltaEuler);
        Quaternion targetRotation = transform.rotation * deltaRotation;

        movement.MoveRotation(targetRotation);
    }
}