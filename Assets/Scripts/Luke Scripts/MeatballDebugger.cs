using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UIElements;

/// <summary>
/// Simple in-game debugger that can be toggled on and off.
/// Supports changing orbit camera sensitivity, toggling music,
/// switching between fullscreen and windowed mode,
/// toggling RMB requirement,
/// AND toggling all HUD/UI with T.
/// </summary>
public class MeatballDebugger : MonoBehaviour
{
    [Header("Toggle")]
    [SerializeField] private Key toggleDebuggerKey = Key.Backquote;

    [Header("Global UI Toggle")]
    [SerializeField] private Key toggleUIKey = Key.T;
    [SerializeField] private UIDocument controlsHUD; // <-- drag your UI Document here

    [Header("Camera")]
    [SerializeField] private MeatballOrbitCamera orbitCamera;
    [SerializeField] private float sensitivityStep = 20f;
    [SerializeField] private float minSensitivity = 20f;
    [SerializeField] private float maxSensitivity = 600f;

    [Header("Music")]
    [SerializeField] private GameObject musicObject;
    [SerializeField] private AudioSource musicSource;

    [Header("Display")]
    [SerializeField] private bool startFullscreen = true;

    [Header("UI")]
    [SerializeField] private bool showDebuggerOnStart = false;

    private bool debuggerVisible;
    private bool musicEnabled = true;
    private bool uiVisible = true; // NEW

    private void Awake()
    {
        if (orbitCamera == null)
            orbitCamera = FindFirstObjectByType<MeatballOrbitCamera>();

        if (musicSource == null && musicObject != null)
            musicSource = musicObject.GetComponent<AudioSource>();

        if (musicSource == null)
            musicSource = FindFirstObjectByType<AudioSource>();

        debuggerVisible = showDebuggerOnStart;

        if (musicSource != null)
            musicEnabled = !musicSource.mute && musicSource.gameObject.activeSelf;

        Screen.fullScreen = startFullscreen;
    }

    private void Update()
    {
        if (Keyboard.current == null)
            return;

        // EXISTING DEBUGGER TOGGLE
        if (Keyboard.current[toggleDebuggerKey].wasPressedThisFrame)
            debuggerVisible = !debuggerVisible;

        // NEW GLOBAL UI TOGGLE (T)
        if (Keyboard.current[toggleUIKey].wasPressedThisFrame)
            ToggleUI();

        if (!debuggerVisible)
            return;

        HandleDebuggerInput();
    }

    private void ToggleUI()
    {
        uiVisible = !uiVisible;

        // Toggle debugger visibility
        debuggerVisible = uiVisible;

        // Toggle UI Toolkit HUD
        if (controlsHUD != null)
            controlsHUD.enabled = uiVisible;
    }

    private void HandleDebuggerInput()
    {
        if (Keyboard.current.digit1Key.wasPressedThisFrame)
            ChangeSensitivity(-sensitivityStep);

        if (Keyboard.current.digit2Key.wasPressedThisFrame)
            ChangeSensitivity(sensitivityStep);

        if (Keyboard.current.mKey.wasPressedThisFrame)
            ToggleMusic();

        if (Keyboard.current.fKey.wasPressedThisFrame)
            ToggleFullscreen();

        if (Keyboard.current.rKey.wasPressedThisFrame)
            ToggleRequireRightMouseButton();
    }

    private void ChangeSensitivity(float amount)
    {
        if (orbitCamera == null)
            return;

        orbitCamera.OrbitSpeedH = Mathf.Clamp(orbitCamera.OrbitSpeedH + amount, minSensitivity, maxSensitivity);
        orbitCamera.OrbitSpeedV = Mathf.Clamp(orbitCamera.OrbitSpeedV + amount, minSensitivity, maxSensitivity);
    }

    private void ToggleMusic()
    {
        if (musicSource == null)
            return;

        musicEnabled = !musicEnabled;
        musicSource.mute = !musicEnabled;
    }

    private void ToggleFullscreen()
    {
        Screen.fullScreen = !Screen.fullScreen;
    }

    private void ToggleRequireRightMouseButton()
    {
        if (orbitCamera == null)
            return;

        orbitCamera.RequireRightMouseButtonToRotate = !orbitCamera.RequireRightMouseButtonToRotate;
    }

    private void OnGUI()
    {
        if (!debuggerVisible)
            return;

        const float width = 420f;
        const float lineHeight = 24f;
        float height = 250f;

        GUI.Box(new Rect(15f, 15f, width, height), "Debugger");

        float y = 45f;

        GUI.Label(new Rect(30f, y, width - 30f, lineHeight), $"Toggle Debugger: {toggleDebuggerKey}");
        y += lineHeight;

        GUI.Label(new Rect(30f, y, width - 30f, lineHeight), $"Toggle HUD (T): {toggleUIKey}");
        y += lineHeight;

        if (orbitCamera != null)
        {
            GUI.Label(new Rect(30f, y, width - 30f, lineHeight), $"Look Sensitivity H: {orbitCamera.OrbitSpeedH:F1}");
            y += lineHeight;

            GUI.Label(new Rect(30f, y, width - 30f, lineHeight), $"Look Sensitivity V: {orbitCamera.OrbitSpeedV:F1}");
            y += lineHeight;

            GUI.Label(new Rect(30f, y, width - 30f, lineHeight), $"[1] Lower Sensitivity");
            y += lineHeight;

            GUI.Label(new Rect(30f, y, width - 30f, lineHeight), $"[2] Raise Sensitivity");
            y += lineHeight;

            GUI.Label(
                new Rect(30f, y, width - 30f, lineHeight),
                $"[R] Require RMB: {(orbitCamera.RequireRightMouseButtonToRotate ? "ON" : "OFF")}"
            );
            y += lineHeight;
        }

        string musicText = musicSource == null
            ? "Music source not found."
            : $"[M] Music: {(musicEnabled ? "ON" : "OFF")}";

        GUI.Label(new Rect(30f, y, width - 30f, lineHeight), musicText);
        y += lineHeight;

        GUI.Label(
            new Rect(30f, y, width - 30f, lineHeight),
            $"[F] Display: {(Screen.fullScreen ? "Fullscreen" : "Windowed")}"
        );
    }
}