using System;
using UnityEngine.UIElements;

public class JoinCodeView
{
    readonly VisualElement m_Root;
    readonly TextField m_CodeField;
    readonly Button m_SubmitBtn;
    readonly Button m_BackBtn;

    public event Action<string> Submitted;
    public event Action BackPressed;

    public JoinCodeView(VisualElement parent)
    {
        m_Root = parent.Q<VisualElement>("join-code-view");
        m_CodeField = m_Root.Q<TextField>("join-code-field");
        m_SubmitBtn = m_Root.Q<Button>("join-code-submit-button");
        m_BackBtn = m_Root.Q<Button>("join-code-back-button");

        m_SubmitBtn.clicked += OnSubmit;
        m_BackBtn.clicked += OnBack;

        Hide();
    }

    public void Show()
    {
        m_Root.style.display = DisplayStyle.Flex;
        m_CodeField.SetValueWithoutNotify(string.Empty);
        m_CodeField.Focus();
    }

    public void Hide()
    {
        m_Root.style.display = DisplayStyle.None;
    }

    public void Dispose()
    {
        if (m_SubmitBtn != null) m_SubmitBtn.clicked -= OnSubmit;
        if (m_BackBtn != null) m_BackBtn.clicked -= OnBack;
        Submitted = null;
        BackPressed = null;
    }

    void OnSubmit() => Submitted?.Invoke(m_CodeField != null ? m_CodeField.value : string.Empty);
    void OnBack() => BackPressed?.Invoke();
}
