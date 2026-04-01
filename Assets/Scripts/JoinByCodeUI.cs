using Unity.Services.Multiplayer;
using UnityEngine;
using UnityEngine.UIElements;

public class JoinByCodeUI : MonoBehaviour
{
    TextField m_JoinCodeField;
    Button m_JoinButton;
    Label m_StatusLabel;

    void OnEnable()
    {
        var root = GetComponent<UIDocument>().rootVisualElement;

        m_JoinCodeField = root.Q<TextField>("JoinCodeField");
        m_JoinButton    = root.Q<Button>("JoinButton");
        m_StatusLabel   = root.Q<Label>("StatusLabel");

        m_JoinButton.clicked += OnJoinClicked;
    }

    void OnDisable()
    {
        m_JoinButton.clicked -= OnJoinClicked;
    }

    void OnJoinClicked()
    {
        var code = m_JoinCodeField.value?.Trim();
        if (string.IsNullOrEmpty(code))
        {
            m_StatusLabel.text = "Please enter a join code.";
            return;
        }

        _ = JoinAsync(code);
    }

    async System.Threading.Tasks.Task JoinAsync(string code)
    {
        m_JoinButton.SetEnabled(false);
        m_StatusLabel.text = "Joining...";

        try
        {
            await MultiplayerService.Instance.JoinSessionByCodeAsync(code);
            m_StatusLabel.text = "Joined!";
        }
        catch (System.Exception e)
        {
            m_StatusLabel.text = $"Failed: {e.Message}";
            m_JoinButton.SetEnabled(true);
        }
    }
}
