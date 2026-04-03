using UnityEngine;

public class SimpleRotator : MonoBehaviour
{
    [Header("Rotation Settings")]
    [Tooltip("Rotation speed in degrees per second")]
    public float rotationSpeed = 90f;

    [Tooltip("Axis to rotate around (X, Y, Z)")]
    public Vector3 rotationAxis = Vector3.up;

    void Update()
    {
        // Rotate the object every frame based on speed and deltaTime
        transform.Rotate(rotationAxis.normalized * rotationSpeed * Time.deltaTime);
    }
}