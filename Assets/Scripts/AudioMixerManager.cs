using UnityEngine;
using UnityEngine.Audio;

/// <summary>
/// Single source of truth for the project's <see cref="AudioMixer"/> and its named groups.
/// Drop on a bootstrap GameObject that lives in the first-loaded scene; survives scene loads.
/// Pause-menu volume sliders and any runtime audio-source routing (see MeatballSFXController)
/// reach the mixer through this singleton instead of duplicating serialized references.
/// </summary>
public class AudioMixerManager : MonoBehaviour
{
    public static AudioMixerManager Instance { get; private set; }

    [SerializeField] private AudioMixer mixer;
    [SerializeField] private AudioMixerGroup masterGroup;
    [SerializeField] private AudioMixerGroup musicGroup;
    [SerializeField] private AudioMixerGroup sfxGroup;

    public AudioMixer Mixer => mixer;
    public AudioMixerGroup MasterGroup => masterGroup;
    public AudioMixerGroup MusicGroup => musicGroup;
    public AudioMixerGroup SfxGroup => sfxGroup;

    void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;
        DontDestroyOnLoad(gameObject);
    }

    void OnDestroy()
    {
        if (Instance == this)
            Instance = null;
    }
}
