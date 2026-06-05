using System;
using UnityEngine.UIElements;

/// <summary>
/// View for the "the host left the game" screen. Owns the queries and button
/// wiring for the host-disconnect-view declared in PauseMenu.uxml.
/// Follows the class-per-view convention (see JoinCodeView).
/// </summary>
public class HostDisconnectView
{
    readonly VisualElement m_Root;
    readonly Button m_ReturnToMenuBtn;

    /// <summary>Raised when the player clicks the return-to-main-menu button.</summary>
    public event Action ReturnToMenu;

    public HostDisconnectView(VisualElement parent)
    {
        m_Root = parent.Q<VisualElement>("host-disconnect-view");
        m_ReturnToMenuBtn = m_Root?.Q<Button>("host-disconnect-menu-button");

        if (m_ReturnToMenuBtn != null)
            m_ReturnToMenuBtn.clicked += OnReturnToMenu;

        Hide();
    }

    public void Show()
    {
        if (m_Root != null) m_Root.style.display = DisplayStyle.Flex;
    }

    public void Hide()
    {
        if (m_Root != null) m_Root.style.display = DisplayStyle.None;
    }

    public void Dispose()
    {
        if (m_ReturnToMenuBtn != null) m_ReturnToMenuBtn.clicked -= OnReturnToMenu;
        ReturnToMenu = null;
    }

    void OnReturnToMenu() => ReturnToMenu?.Invoke();
}
