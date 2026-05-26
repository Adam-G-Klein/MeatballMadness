using UnityEngine;

/// <summary>
/// Deprecated. All in-game controls have moved to <see cref="PauseMenuController"/>
/// (UI Toolkit pause menu, opened with the Menu input action). Remove this component
/// from any scene it's attached to and add PauseMenuController to a GameObject with
/// a UIDocument referencing Assets/UI/PauseMenu.uxml.
/// </summary>
public class MeatballDebugger : MonoBehaviour
{
    void Awake()
    {
        Debug.LogWarning(
            "MeatballDebugger is deprecated. Replace this component with PauseMenuController " +
            "(see Assets/Scripts/PauseMenuController.cs)."
        );
    }
}
