using UnityEngine;

public class WorldspaceCanvasCameraRotator : MonoBehaviour
{
    Camera _cam;

    void OnEnable()
    {
        _cam = Camera.main;
    }

    void LateUpdate()
    {
        if (_cam == null)
        {
            _cam = Camera.main;
            if (_cam == null) return;
        }

        bool facingAway = Vector3.Dot(transform.forward, _cam.transform.forward) < 0f;
        Vector3 scale = transform.localScale;
        float desiredX = Mathf.Abs(scale.x) * (facingAway ? -1f : 1f);
        if (!Mathf.Approximately(scale.x, desiredX))
        {
            scale.x = desiredX;
            transform.localScale = scale;
        }
    }
}
