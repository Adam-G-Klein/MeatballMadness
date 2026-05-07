using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Playables;

[RequireComponent(typeof(PlayableDirector))]
public class CutscenePlayer : MonoBehaviour, InputSystem_Actions.IPlayerActions
{
    [SerializeField] private bool playOnAwake = true;
    [SerializeField] private bool restartOnKey = true;
    [SerializeField] private float skipSeconds = 5f;

    private PlayableDirector _director;
    private InputSystem_Actions _actions;

    private void Awake()
    {
        _director = GetComponent<PlayableDirector>();
        _director.playOnAwake = false;

        _actions = new InputSystem_Actions();
        _actions.Player.AddCallbacks(this);

        if (playOnAwake) _director.Play();
    }

    private void OnEnable()
    {
        _actions.Player.Enable();
    }

    private void OnDisable()
    {
        _actions.Player.Disable();
    }

    private void OnDestroy()
    {
        _actions?.Dispose();
    }

    private void Update()
    {
        var keyboard = Keyboard.current;
        if (keyboard == null) return;

        if (restartOnKey && keyboard.rKey.wasPressedThisFrame)
        {
            _director.Stop();
            _director.time = 0;
            _director.Play();
        }

        if (keyboard.qKey.wasPressedThisFrame)
        {
            Scrub(-skipSeconds);
        }

        if (keyboard.eKey.wasPressedThisFrame)
        {
            Scrub(skipSeconds);
        }
    }

    private void Scrub(float deltaSeconds)
    {
        if (_director.playableAsset == null) return;

        double duration = _director.playableAsset.duration;
        double newTime = _director.time + deltaSeconds;
        if (newTime < 0) newTime = 0;
        if (newTime > duration) newTime = duration;

        _director.time = newTime;
        _director.Evaluate();
    }

    public void OnMove(InputAction.CallbackContext context) {}
    public void OnLook(InputAction.CallbackContext context) {}
    public void OnJump(InputAction.CallbackContext context) {}
    public void OnSprint(InputAction.CallbackContext context) {}
    public void OnToggleLooking(InputAction.CallbackContext context) {}
    public void OnMenu(InputAction.CallbackContext context) {}
    public void OnReel(InputAction.CallbackContext context) {}
    public void OnEmote(InputAction.CallbackContext context) {}
}
