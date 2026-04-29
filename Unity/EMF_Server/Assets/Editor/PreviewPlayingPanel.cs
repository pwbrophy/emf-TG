using UnityEngine;
using UnityEditor;

public static class PreviewPlayingPanel
{
    [MenuItem("Thundergeddon/Preview Playing Panel")]
    public static void Show()
    {
        // Hide all panels, show only PlayingPanel
        string[] panels = { "MainMenuPanel", "LobbyPanel", "PlayingPanel", "EndedPanel" };
        foreach (var name in panels)
        {
            var go = GameObject.Find(name);
            if (go != null) go.SetActive(name == "PlayingPanel");
        }
        EditorApplication.QueuePlayerLoopUpdate();
        SceneView.RepaintAll();
    }

    [MenuItem("Thundergeddon/Preview Main Menu")]
    public static void ShowMenu()
    {
        string[] panels = { "MainMenuPanel", "LobbyPanel", "PlayingPanel", "EndedPanel" };
        foreach (var name in panels)
        {
            var go = GameObject.Find(name);
            if (go != null) go.SetActive(name == "MainMenuPanel");
        }
        EditorApplication.QueuePlayerLoopUpdate();
        SceneView.RepaintAll();
    }
}
