using UnityEngine;

/// <summary>
/// Makes a checkpoint float along a chosen axis using a sine wave
/// and slowly rotate for visual flair.
/// </summary>
public class CheckpointFloatRotate : MonoBehaviour
{
    [Header("Floating")]
    [Tooltip("Direction the object floats in. Can be any axis or custom direction.")]
    [SerializeField] private Vector3 floatAxis = Vector3.up;

    [Tooltip("How far the object moves along the float axis.")]
    [SerializeField] private float floatAmplitude = 0.35f;

    [Tooltip("Speed of the floating motion.")]
    [SerializeField] private float floatSpeed = 2f;

    [Header("Rotation")]
    [Tooltip("Axis the object rotates around.")]
    [SerializeField] private Vector3 rotationAxis = Vector3.up;

    [Tooltip("Degrees per second.")]
    [SerializeField] private float rotationSpeed = 35f;

    private Vector3 startLocalPosition;

    private void Awake()
    {
        startLocalPosition = transform.localPosition;
    }

    private void Update()
    {
        // Normalize axis so amplitude behaves correctly
        Vector3 normalizedAxis = floatAxis.normalized;

        float offset = Mathf.Sin(Time.time * floatSpeed) * floatAmplitude;

        transform.localPosition = startLocalPosition + normalizedAxis * offset;

        transform.Rotate(rotationAxis.normalized, rotationSpeed * Time.deltaTime, Space.Self);
    }
}