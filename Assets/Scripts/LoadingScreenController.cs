using System.Collections;
using UnityEngine;
using UnityEngine.UIElements;

/// <summary>
/// Full-screen loading screen for gameplay scenes (currently LukeScene). A plain black
/// rectangle covers the view the instant the scene opens, then swipes up and out one
/// second after the locally-owned meatball has finished spawning in.
///
/// Built with UI Toolkit to match the project's UI conventions. The overlay tree is
/// constructed in code so no UXML/VisualTreeAsset wiring is needed — only the
/// <see cref="UIDocument"/>'s PanelSettings has to be assigned in the inspector. Give
/// this UIDocument a higher sorting order than the HUD/pause document so the black
/// rectangle renders on top of everything until it swipes away.
/// </summary>
[RequireComponent(typeof(UIDocument))]
public class LoadingScreenController : MonoBehaviour
{
    [Header("Timing")]
    [Tooltip("Delay (seconds) after the local meatball spawns before the screen swipes away.")]
    [SerializeField] private float _hideDelayAfterSpawn = 1f;

    [Tooltip("Duration (seconds) of the upward swipe-out animation.")]
    [SerializeField] private float _swipeDuration = 0.5f;

    [Tooltip("Safety net: if the local meatball never reports as spawned within this many " +
             "seconds, swipe the screen away anyway so the player is never stuck behind it. " +
             "Set to 0 to wait indefinitely.")]
    [SerializeField] private float _maxWaitForSpawn = 30f;

    private VisualElement _overlay;
    private bool _dismissed;
    private float _elapsedWaiting;

    private void OnEnable()
    {
        MeatballClientInputHandler.LocalMeatballSpawned += OnLocalMeatballSpawned;
    }

    private void OnDisable()
    {
        MeatballClientInputHandler.LocalMeatballSpawned -= OnLocalMeatballSpawned;
    }

    private void Start()
    {
        BuildOverlay();

        // If the meatball already spawned before we started listening, run the
        // dismiss sequence right away rather than waiting for an event that has
        // already fired.
        if (MeatballClientInputHandler.HasLocalMeatballSpawned)
            OnLocalMeatballSpawned();
    }

    private void Update()
    {
        if (_dismissed || _maxWaitForSpawn <= 0f) return;

        _elapsedWaiting += Time.unscaledDeltaTime;
        if (_elapsedWaiting >= _maxWaitForSpawn)
        {
            Debug.LogWarning("[LoadingScreen] Local meatball never reported as spawned; " +
                             "dismissing loading screen on the safety timeout.");
            BeginDismiss();
        }
    }

    private void BuildOverlay()
    {
        var root = GetComponent<UIDocument>().rootVisualElement;
        if (root == null)
        {
            Debug.LogError("[LoadingScreen] UIDocument rootVisualElement is null; cannot build overlay.");
            return;
        }

        root.pickingMode = PickingMode.Ignore;

        _overlay = new VisualElement { name = "loading-overlay" };
        _overlay.style.position = Position.Absolute;
        _overlay.style.left = 0;
        _overlay.style.top = 0;
        _overlay.style.right = 0;
        _overlay.style.bottom = 0;
        _overlay.style.backgroundColor = Color.black;
        _overlay.pickingMode = PickingMode.Ignore;

        root.Add(_overlay);
    }

    private void OnLocalMeatballSpawned()
    {
        if (_dismissed) return;
        _dismissed = true;
        StartCoroutine(DismissAfterDelay());
    }

    private void BeginDismiss()
    {
        if (_dismissed) return;
        _dismissed = true;
        StartCoroutine(SwipeUpAndOut());
    }

    private IEnumerator DismissAfterDelay()
    {
        // Unscaled so the delay holds even if something has paused/slowed time.
        yield return new WaitForSecondsRealtime(_hideDelayAfterSpawn);
        yield return SwipeUpAndOut();
    }

    private IEnumerator SwipeUpAndOut()
    {
        if (_overlay == null) yield break;

        float t = 0f;
        float duration = Mathf.Max(0.0001f, _swipeDuration);
        while (t < 1f)
        {
            t += Time.unscaledDeltaTime / duration;
            float eased = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(t));
            // Percent is relative to the element's own (full-screen) height, so -100%
            // slides it completely off the top of the screen.
            _overlay.style.translate = new Translate(0, Length.Percent(-100f * eased), 0);
            yield return null;
        }

        _overlay.style.display = DisplayStyle.None;
    }
}
