using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Simple in-game debugger that can be toggled on and off.
/// Supports changing orbit camera sensitivity, toggling music,
/// and switching between fullscreen and windowed mode.
/// </summary>
public class MeatballDebugger : MonoBehaviour
{
    [Header("Toggle")]
    [SerializeField] private Key toggleDebuggerKey = Key.Backquote;

    [Header("Camera")]
    [SerializeField] private MeatballOrbitCamera orbitCamera;
    [SerializeField] private float sensitivityStep = 20f;
    [SerializeField] private float minSensitivity = 20f;
    [SerializeField] private float maxSensitivity = 600f;

    [Header("Music")]
    [Tooltip("GameObject that contains the background music AudioSource.")]
    [SerializeField] private GameObject musicObject;
    [SerializeField] private AudioSource musicSource;

    [Header("Display")]
    [SerializeField] private bool startFullscreen = true;

    [Header("UI")]
    [SerializeField] private bool showDebuggerOnStart = false;

    private bool debuggerVisible;
    private bool musicEnabled = true;

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

        if (Keyboard.current[toggleDebuggerKey].wasPressedThisFrame)
        {
            debuggerVisible = !debuggerVisible;
        }

        if (!debuggerVisible)
            return;

        HandleDebuggerInput();
    }

    private void HandleDebuggerInput()
    {
        if (Keyboard.current.digit1Key.wasPressedThisFrame)
        {
            ChangeSensitivity(-sensitivityStep);
        }

        if (Keyboard.current.digit2Key.wasPressedThisFrame)
        {
            ChangeSensitivity(sensitivityStep);
        }

        if (Keyboard.current.mKey.wasPressedThisFrame)
        {
            ToggleMusic();
        }

        if (Keyboard.current.fKey.wasPressedThisFrame)
        {
            ToggleFullscreen();
        }
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

    private void OnGUI()
    {
        if (!debuggerVisible)
            return;

        const float width = 380f;
        const float lineHeight = 24f;
        float height = 220f;

        GUI.Box(new Rect(15f, 15f, width, height), "Debugger");

        float y = 45f;

        GUI.Label(new Rect(30f, y, width - 30f, lineHeight), $"Toggle Debugger: {toggleDebuggerKey}");
        y += lineHeight;

        if (orbitCamera != null)
        {
            GUI.Label(new Rect(30f, y, width - 30f, lineHeight), $"Look Sensitivity H: {orbitCamera.OrbitSpeedH:F1}");
            y += lineHeight;

            GUI.Label(new Rect(30f, y, width - 30f, lineHeight), $"Look Sensitivity V: {orbitCamera.OrbitSpeedV:F1}");
            y += lineHeight;

            GUI.Label(new Rect(30f, y, width - 30f, lineHeight), $"[1] Lower Sensitivity by {sensitivityStep:F0}");
            y += lineHeight;

            GUI.Label(new Rect(30f, y, width - 30f, lineHeight), $"[2] Raise Sensitivity by {sensitivityStep:F0}");
            y += lineHeight;
        }
        else
        {
            GUI.Label(new Rect(30f, y, width - 30f, lineHeight), "Orbit camera not assigned/found.");
            y += lineHeight * 2f;
        }

        string musicText = musicSource == null
            ? "Music source not assigned/found."
            : $"[M] Music: {(musicEnabled ? "ON" : "OFF")}";

        GUI.Label(new Rect(30f, y, width - 30f, lineHeight), musicText);
        y += lineHeight;

        GUI.Label(
            new Rect(30f, y, width - 30f, lineHeight),
            $"[F] Display Mode: {(Screen.fullScreen ? "Fullscreen" : "Windowed")}"
        );
    }
}