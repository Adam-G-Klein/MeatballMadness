using UnityEngine;

/// <summary>
/// Makes a checkpoint float up and down with a sine wave and slowly rotate.
/// Purely visual.
/// </summary>
public class CheckpointFloatRotate : MonoBehaviour
{
    [Header("Floating")]
    [SerializeField] private float floatAmplitude = 0.35f;
    [SerializeField] private float floatSpeed = 2f;

    [Header("Rotation")]
    [SerializeField] private Vector3 rotationAxis = Vector3.up;
    [SerializeField] private float rotationSpeed = 35f;

    private Vector3 startLocalPosition;

    private void Awake()
    {
        startLocalPosition = transform.localPosition;
    }

    private void Update()
    {
        float yOffset = Mathf.Sin(Time.time * floatSpeed) * floatAmplitude;
        transform.localPosition = startLocalPosition + new Vector3(0f, yOffset, 0f);

        transform.Rotate(rotationAxis.normalized, rotationSpeed * Time.deltaTime, Space.Self);
    }
}