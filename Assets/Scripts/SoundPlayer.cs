using UnityEngine;

public class SoundPlayer : MonoBehaviour
{
    [SerializeField] AudioSource emoteSource;
    // Start is called once before the first execution of Update after the MonoBehaviour is created
    void Awake()
    {
        if (emoteSource == null) emoteSource = GetComponent<AudioSource>();
    }

    // Update is called once per frame
    public void PlayEmoteSound()
    {
        emoteSource.PlayOneShot(emoteSource.clip);
    }


}
