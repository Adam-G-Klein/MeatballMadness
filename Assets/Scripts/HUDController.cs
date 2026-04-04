using System.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;

[RequireComponent(typeof(UIDocument))]
public class HUDController : MonoBehaviour
{
    [SerializeField] private string mainMenuSceneName = "MainMenu";
    [SerializeField] private float returnToMenuDelay = 5f;

    private Label _winLabel;

    private void Awake()
    {
        var root = GetComponent<UIDocument>().rootVisualElement;
        _winLabel = root.Q<Label>("win-label");
    }

    public void ShowWinScreen()
    {
        if (_winLabel != null)
            _winLabel.style.display = DisplayStyle.Flex;

        StartCoroutine(ReturnToMenuAfterDelay());
    }

    private IEnumerator ReturnToMenuAfterDelay()
    {
        yield return new WaitForSeconds(returnToMenuDelay);
        SceneManager.LoadScene(mainMenuSceneName);
    }
}
