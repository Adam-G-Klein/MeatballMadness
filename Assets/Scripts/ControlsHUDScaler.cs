using UnityEngine;
using UnityEngine.UIElements;

/// <summary>
/// Scales the ControlsHUD panel font size and width proportionally with screen
/// resolution, enforcing a minimum font size so text never goes unreadably small.
/// Attach to the same GameObject as the UIDocument that holds ControlsHUD.uxml.
/// </summary>
[RequireComponent(typeof(UIDocument))]
public class ControlsHUDScaler : MonoBehaviour
{
    [Tooltip("Font size = screen width * this factor, floored at MinFontSize.")]
    [SerializeField, Min(0.001f)] float _fontScaleFactor = 0.009f;

    [Tooltip("Font size will never go below this value.")]
    [SerializeField, Min(1f)] float _minFontSize = 13f;

    [Tooltip("Panel width = screen width * this factor.")]
    [SerializeField, Min(0.01f)] float _widthScaleFactor = 0.089f;

    UIDocument _doc;
    int _lastWidth;
    int _lastHeight;

    void Awake() => _doc = GetComponent<UIDocument>();

    void OnEnable() => ApplyScale();

    void Update()
    {
        if (Screen.width != _lastWidth || Screen.height != _lastHeight)
            ApplyScale();
    }

    void ApplyScale()
    {
        _lastWidth = Screen.width;
        _lastHeight = Screen.height;

        float fontSize = Mathf.Max(_minFontSize, Screen.width * _fontScaleFactor);
        float panelWidth = Mathf.Max(160f, Screen.width * _widthScaleFactor);

        VisualElement root = _doc.rootVisualElement;
        VisualElement container = root.Q("controls-root");
        VisualElement panel = root.Q("controls-panel");

        if (container != null)
            container.style.width = panelWidth;

        if (panel != null)
            panel.style.fontSize = fontSize; // all children inherit this
    }
}
