using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

public static class ShowLobbyPanel
{
    [MenuItem("Thundergeddon/Show Lobby Panel")]
    public static void Execute()
    {
        var canvas = GameObject.Find("Canvas");
        if (canvas == null) { Debug.LogError("[ShowLobbyPanel] Canvas not found."); return; }

        string[] panels = { "MainMenuPanel", "LobbyPanel", "PlayingPanel", "EndedPanel" };
        foreach (var panelName in panels)
        {
            var t = canvas.transform.Find(panelName);
            if (t != null) t.gameObject.SetActive(panelName == "LobbyPanel");
        }

        EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
        EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo();
        EditorApplication.QueuePlayerLoopUpdate();
        SceneView.RepaintAll();
        Debug.Log("[ShowLobbyPanel] Done.");
    }
}
