using UnityEngine;
using UnityEngine.Audio;

/// <summary>
/// Routes every <see cref="AudioSource"/> on this GameObject (and optionally its children)
/// to the SFX <see cref="AudioMixerGroup"/> from <see cref="AudioMixerManager"/>. Attach to
/// the meatball prefab root so player-spawned SFX go through the mixer's SFX bus and obey
/// the pause-menu volume slider.
/// </summary>
public class MeatballSFXController : MonoBehaviour
{
    [Tooltip("Also re-route AudioSources on child GameObjects (e.g. nested SFX emitters).")]
    [SerializeField] private bool includeChildren = true;

    [Tooltip("Include inactive AudioSources when scanning. Catches sources that get enabled later.")]
    [SerializeField] private bool includeInactive = true;

    [Tooltip("Optional explicit override. If left null, the SFX group comes from AudioMixerManager.")]
    [SerializeField] private AudioMixerGroup sfxGroupOverride;

    void Start()
    {
        RouteToSfxGroup();
    }

    /// <summary>
    /// Public so callers (spawn flows, late-added AudioSources) can re-run routing after
    /// adding sources at runtime.
    /// </summary>
    public void RouteToSfxGroup()
    {
        AudioMixerGroup group = ResolveSfxGroup();
        if (group == null)
        {
            Debug.LogWarning(
                $"{nameof(MeatballSFXController)} on '{name}': no SFX mixer group available " +
                $"(AudioMixerManager.Instance missing or its SfxGroup unset). Sources left on their current output.",
                this
            );
            return;
        }

        AudioSource[] sources = includeChildren
            ? GetComponentsInChildren<AudioSource>(includeInactive)
            : GetComponents<AudioSource>();

        for (int i = 0; i < sources.Length; i++)
            sources[i].outputAudioMixerGroup = group;
    }

    AudioMixerGroup ResolveSfxGroup()
    {
        if (sfxGroupOverride != null) return sfxGroupOverride;
        return AudioMixerManager.Instance != null ? AudioMixerManager.Instance.SfxGroup : null;
    }
}
