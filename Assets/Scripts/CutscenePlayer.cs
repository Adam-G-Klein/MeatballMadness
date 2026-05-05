using UnityEngine;
using UnityEngine.Playables;

[RequireComponent(typeof(PlayableDirector))]
public class CutscenePlayer : MonoBehaviour
{
    [SerializeField] private bool playOnAwake = true;
    [SerializeField] private bool restartOnKey = true;
    [SerializeField] private KeyCode restartKey = KeyCode.R;

    private PlayableDirector _director;

    private void Awake()
    {
        _director = GetComponent<PlayableDirector>();
        _director.playOnAwake = false;
        if (playOnAwake) _director.Play();
    }

    private void Update()
    {
        if (restartOnKey && Input.GetKeyDown(restartKey))
        {
            _director.Stop();
            _director.time = 0;
            _director.Play();
        }
    }
}
