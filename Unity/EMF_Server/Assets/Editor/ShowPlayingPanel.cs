using UnityEditor;
using UnityEngine;

public static class ShowPlayingPanel
{
    [MenuItem("Thundergeddon/Show Playing Panel")]
    public static void Execute()
    {
        var canvas = GameObject.Find("Canvas");
        if (canvas == null) { Debug.LogError("[ShowPlayingPanel] Canvas not found."); return; }

        string[] panels = { "MainMenuPanel", "LobbyPanel", "PlayingPanel", "EndedPanel" };
        foreach (var panelName in panels)
        {
            var t = canvas.transform.Find(panelName);
            if (t != null) t.gameObject.SetActive(panelName == "PlayingPanel");
        }

        EditorApplication.QueuePlayerLoopUpdate();
        SceneView.RepaintAll();
        Debug.Log("[ShowPlayingPanel] Done.");
    }
}
