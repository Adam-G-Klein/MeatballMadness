using UnityEngine;

[ExecuteAlways]
public class Letterbox : MonoBehaviour
{
    public RectTransform topBar;
    public RectTransform bottomBar;
    public RectTransform leftBar;
    public RectTransform rightBar;

    [Range(0f, 0.5f)] public float topAmount;
    [Range(0f, 0.5f)] public float bottomAmount;
    [Range(0f, 0.5f)] public float leftAmount;
    [Range(0f, 0.5f)] public float rightAmount;

    void LateUpdate()
    {
        float h = Screen.height;
        float w = Screen.width;

        SetVertical(topBar, topAmount * h);
        SetVertical(bottomBar, bottomAmount * h);
        SetHorizontal(leftBar, leftAmount * w);
        SetHorizontal(rightBar, rightAmount * w);
    }

    static void SetVertical(RectTransform rt, float pixels)
    {
        if (!rt) return;
        var s = rt.sizeDelta;
        rt.sizeDelta = new Vector2(s.x, pixels);
    }

    static void SetHorizontal(RectTransform rt, float pixels)
    {
        if (!rt) return;
        var s = rt.sizeDelta;
        rt.sizeDelta = new Vector2(pixels, s.y);
    }
}
