using UnityEngine;
using UnityEngine.Audio;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;

[RequireComponent(typeof(UIDocument))]
public class PauseMenuController : MonoBehaviour, InputSystem_Actions.IPlayerActions
{
    /// <summary>True while the pause menu is open. Gameplay systems should skip input/look while this is set.</summary>
    public static bool IsPaused { get; private set; }

    [Header("Scene")]
    [SerializeField] private string mainMenuSceneName = "MainMenu";

    [Header("Camera")]
    [SerializeField] private MeatballOrbitCamera orbitCamera;

    [Header("Audio")]
    [Tooltip("Audio mixer with two exposed parameters (defaults: 'MusicVolume', 'SFXVolume').")]
    [SerializeField] private AudioMixer audioMixer;
    [SerializeField] private string musicVolumeParam = "MusicVolume";
    [SerializeField] private string sfxVolumeParam = "SFXVolume";

    [Header("Display")]
    [SerializeField] private bool startFullscreen = true;

    InputSystem_Actions _actions;
    InputSystem_Actions.PlayerActions _player;

    VisualElement _root;
    VisualElement _mainView;
    VisualElement _optionsView;

    Label _joinCodeLabel;

    Button _resumeBtn;
    Button _moreOptionsBtn;
    Button _quitToMenuBtn;
    Button _quitToDesktopBtn;
    Slider _sfxSlider;
    Slider _musicSlider;

    Slider _sensHSlider;
    Slider _sensVSlider;
    Toggle _fullscreenToggle;
    Toggle _requireRmbToggle;
    Button _optionsBackBtn;

    bool _isOpen;

    void Awake()
    {
        if (orbitCamera == null)
            orbitCamera = FindFirstObjectByType<MeatballOrbitCamera>();

        if (audioMixer == null && AudioMixerManager.Instance != null)
            audioMixer = AudioMixerManager.Instance.Mixer;

        Screen.fullScreen = startFullscreen;

        _actions = new InputSystem_Actions();
        _player = _actions.Player;
        _player.AddCallbacks(this);
    }

    void OnEnable()
    {
        var doc = GetComponent<UIDocument>();
        _root = doc != null ? doc.rootVisualElement : null;
        if (_root == null)
        {
            Debug.LogError("PauseMenuController: UIDocument or rootVisualElement is null.");
            return;
        }

        _root = _root.Q<VisualElement>("pause-root") ?? _root;
        _mainView = _root.Q<VisualElement>("pause-main-view");
        _optionsView = _root.Q<VisualElement>("pause-options-view");

        _joinCodeLabel = _root.Q<Label>("join-code-label");

        _resumeBtn = _root.Q<Button>("resume-button");
        _moreOptionsBtn = _root.Q<Button>("more-options-button");
        _quitToMenuBtn = _root.Q<Button>("quit-to-menu-button");
        _quitToDesktopBtn = _root.Q<Button>("quit-to-desktop-button");
        _sfxSlider = _root.Q<Slider>("sfx-volume-slider");
        _musicSlider = _root.Q<Slider>("music-volume-slider");

        _sensHSlider = _root.Q<Slider>("sensitivity-h-slider");
        _sensVSlider = _root.Q<Slider>("sensitivity-v-slider");
        _fullscreenToggle = _root.Q<Toggle>("fullscreen-toggle");
        _requireRmbToggle = _root.Q<Toggle>("require-rmb-toggle");
        _optionsBackBtn = _root.Q<Button>("options-back-button");

        if (_resumeBtn != null) _resumeBtn.clicked += OnResume;
        if (_moreOptionsBtn != null) _moreOptionsBtn.clicked += OnMoreOptions;
        if (_quitToMenuBtn != null) _quitToMenuBtn.clicked += OnQuitToMenu;
        if (_quitToDesktopBtn != null) _quitToDesktopBtn.clicked += OnQuitToDesktop;
        if (_optionsBackBtn != null) _optionsBackBtn.clicked += OnOptionsBack;

        if (_sfxSlider != null)
        {
            _sfxSlider.SetValueWithoutNotify(ReadMixerSliderValue(sfxVolumeParam));
            _sfxSlider.RegisterValueChangedCallback(OnSfxChanged);
        }

        if (_musicSlider != null)
        {
            _musicSlider.SetValueWithoutNotify(ReadMixerSliderValue(musicVolumeParam));
            _musicSlider.RegisterValueChangedCallback(OnMusicChanged);
        }

        if (_sensHSlider != null && orbitCamera != null)
        {
            _sensHSlider.SetValueWithoutNotify(orbitCamera.OrbitSpeedH);
            _sensHSlider.RegisterValueChangedCallback(OnSensHChanged);
        }

        if (_sensVSlider != null && orbitCamera != null)
        {
            _sensVSlider.SetValueWithoutNotify(orbitCamera.OrbitSpeedV);
            _sensVSlider.RegisterValueChangedCallback(OnSensVChanged);
        }

        if (_fullscreenToggle != null)
        {
            _fullscreenToggle.SetValueWithoutNotify(Screen.fullScreen);
            _fullscreenToggle.RegisterValueChangedCallback(OnFullscreenChanged);
        }

        if (_requireRmbToggle != null && orbitCamera != null)
        {
            _requireRmbToggle.SetValueWithoutNotify(orbitCamera.RequireRightMouseButtonToRotate);
            _requireRmbToggle.RegisterValueChangedCallback(OnRequireRmbChanged);
        }

        HidePause();
        _player.Enable();
    }

    void OnDisable()
    {
        _player.Disable();

        if (_resumeBtn != null) _resumeBtn.clicked -= OnResume;
        if (_moreOptionsBtn != null) _moreOptionsBtn.clicked -= OnMoreOptions;
        if (_quitToMenuBtn != null) _quitToMenuBtn.clicked -= OnQuitToMenu;
        if (_quitToDesktopBtn != null) _quitToDesktopBtn.clicked -= OnQuitToDesktop;
        if (_optionsBackBtn != null) _optionsBackBtn.clicked -= OnOptionsBack;

        if (_sfxSlider != null) _sfxSlider.UnregisterValueChangedCallback(OnSfxChanged);
        if (_musicSlider != null) _musicSlider.UnregisterValueChangedCallback(OnMusicChanged);
        if (_sensHSlider != null) _sensHSlider.UnregisterValueChangedCallback(OnSensHChanged);
        if (_sensVSlider != null) _sensVSlider.UnregisterValueChangedCallback(OnSensVChanged);
        if (_fullscreenToggle != null) _fullscreenToggle.UnregisterValueChangedCallback(OnFullscreenChanged);
        if (_requireRmbToggle != null) _requireRmbToggle.UnregisterValueChangedCallback(OnRequireRmbChanged);
    }

    void OnDestroy()
    {
        _actions?.Dispose();
    }

    public void OnMenu(InputAction.CallbackContext context)
    {
        if (!context.performed) return;
        if (_isOpen) HidePause();
        else ShowMainPause();
    }

    void ShowMainPause()
    {
        if (_root == null) return;
        _isOpen = true;
        IsPaused = true;
        SetCursorVisible(true);
        _root.RemoveFromClassList("hidden");
        if (_mainView != null) _mainView.style.display = DisplayStyle.Flex;
        if (_optionsView != null) _optionsView.style.display = DisplayStyle.None;

        if (_joinCodeLabel != null)
        {
            var code = MeatballMultiplayerSessionManager.Instance != null
                ? MeatballMultiplayerSessionManager.Instance.JoinCode
                : null;
            _joinCodeLabel.text = string.IsNullOrEmpty(code) ? "Join Code: ---" : $"Join Code: {code}";
        }
    }

    void ShowOptions()
    {
        if (_root == null) return;
        _isOpen = true;
        IsPaused = true;
        SetCursorVisible(true);
        _root.RemoveFromClassList("hidden");
        if (_mainView != null) _mainView.style.display = DisplayStyle.None;
        if (_optionsView != null) _optionsView.style.display = DisplayStyle.Flex;
    }

    void HidePause()
    {
        if (_root == null) return;
        _isOpen = false;
        IsPaused = false;
        SetCursorVisible(false);
        _root.AddToClassList("hidden");
        if (_mainView != null) _mainView.style.display = DisplayStyle.None;
        if (_optionsView != null) _optionsView.style.display = DisplayStyle.None;
    }

    static void SetCursorVisible(bool visible)
    {
        UnityEngine.Cursor.visible = visible;
        UnityEngine.Cursor.lockState = visible ? CursorLockMode.None : CursorLockMode.Locked;
    }

    void OnResume() => HidePause();
    void OnMoreOptions() => ShowOptions();
    void OnOptionsBack() => ShowMainPause();

    async void OnQuitToMenu()
    {
        HidePause();
        if (MeatballMultiplayerSessionManager.Instance != null)
            await MeatballMultiplayerSessionManager.Instance.LeaveSessionAsync();
        SceneManager.LoadScene(mainMenuSceneName);
    }

    void OnQuitToDesktop()
    {
#if UNITY_EDITOR
        UnityEditor.EditorApplication.isPlaying = false;
#else
        Application.Quit();
#endif
    }

    void OnSfxChanged(ChangeEvent<float> evt) => SetMixerVolume(sfxVolumeParam, evt.newValue);
    void OnMusicChanged(ChangeEvent<float> evt) => SetMixerVolume(musicVolumeParam, evt.newValue);

    void SetMixerVolume(string param, float sliderValue)
    {
        if (audioMixer == null || string.IsNullOrEmpty(param)) return;
        audioMixer.SetFloat(param, SliderToDb(sliderValue));
    }

    float ReadMixerSliderValue(string param)
    {
        if (audioMixer == null || string.IsNullOrEmpty(param)) return 100f;
        if (!audioMixer.GetFloat(param, out float db)) return 100f;
        return DbToSlider(db);
    }

    /// <summary>0..100 slider -> dB. 0 -> -80 dB (silent), 100 -> 0 dB. Perceptually-even via 20*log10(linear).</summary>
    static float SliderToDb(float sliderValue)
    {
        float linear = Mathf.Clamp(sliderValue / 100f, 0.0001f, 1f);
        return Mathf.Log10(linear) * 20f;
    }

    static float DbToSlider(float db)
    {
        float linear = Mathf.Pow(10f, db / 20f);
        return Mathf.Clamp(linear * 100f, 0f, 100f);
    }

    void OnSensHChanged(ChangeEvent<float> evt)
    {
        if (orbitCamera != null)
            orbitCamera.OrbitSpeedH = evt.newValue;
    }

    void OnSensVChanged(ChangeEvent<float> evt)
    {
        if (orbitCamera != null)
            orbitCamera.OrbitSpeedV = evt.newValue;
    }

    void OnFullscreenChanged(ChangeEvent<bool> evt)
    {
        Screen.fullScreen = evt.newValue;
    }

    void OnRequireRmbChanged(ChangeEvent<bool> evt)
    {
        if (orbitCamera != null)
            orbitCamera.RequireRightMouseButtonToRotate = evt.newValue;
    }

    public void OnMove(InputAction.CallbackContext context) { }
    public void OnLook(InputAction.CallbackContext context) { }
    public void OnJump(InputAction.CallbackContext context) { }
    public void OnSprint(InputAction.CallbackContext context) { }
    public void OnToggleLooking(InputAction.CallbackContext context) { }
    public void OnReel(InputAction.CallbackContext context) { }
    public void OnEmote(InputAction.CallbackContext context) { }
}
