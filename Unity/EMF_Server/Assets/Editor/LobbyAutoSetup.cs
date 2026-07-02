// LobbyAutoSetup.cs
// Runs once on editor load / recompile. Finds Canvas/LobbyPanel in the open
// scene and adds LobbyLayoutBuilder if it isn't already attached, then saves.
// After the first run the component is in the scene file and this is a no-op.

using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

[InitializeOnLoad]
public static class LobbyAutoSetup
{
    static LobbyAutoSetup()
    {
        EditorApplication.delayCall += EnsureAttached;
    }

    static void EnsureAttached()
    {
        var canvas = GameObject.Find("Canvas");
        if (canvas == null) return;

        var lobbyPanel = canvas.transform.Find("LobbyPanel");
        if (lobbyPanel == null) return;

        if (lobbyPanel.GetComponent<LobbyLayoutBuilder>() != null) return;

        lobbyPanel.gameObject.AddComponent<LobbyLayoutBuilder>();
        EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
        EditorSceneManager.SaveOpenScenes();
        Debug.Log("[LobbyAutoSetup] Attached LobbyLayoutBuilder to LobbyPanel.");
    }
}
