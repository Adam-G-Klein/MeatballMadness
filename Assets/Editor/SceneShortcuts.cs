using UnityEditor;
using UnityEditor.SceneManagement;

public static class SceneShortcuts
{
    [MenuItem("Scenes/Open Main Menu _&1")]
    static void OpenMainMenu()
    {
        if (EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            EditorSceneManager.OpenScene("Assets/Scenes/MainMenu.unity");
    }

    [MenuItem("Scenes/Open LukeScene _&2")]
    static void OpenLukeScene()
    {
        if (EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            EditorSceneManager.OpenScene("Assets/Scenes/LukeScene.unity");
    }
}
