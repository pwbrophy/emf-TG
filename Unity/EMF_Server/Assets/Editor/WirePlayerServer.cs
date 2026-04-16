// WirePlayerServer.cs
// Adds PlayerWebSocketServer to the Servers GameObject in the scene.
// Menu: Thundergeddon / 5 Wire Player Server

#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;
using UnityEditor.SceneManagement;

public static class WirePlayerServer
{
    [MenuItem("Thundergeddon/5 Wire Player Server")]
    public static void Execute()
    {
        var servers = GameObject.Find("Servers");
        if (servers == null)
        {
            Debug.LogError("[WirePlayerServer] Could not find 'Servers' GameObject in scene.");
            return;
        }

        if (servers.GetComponent<PlayerWebSocketServer>() == null)
            servers.AddComponent<PlayerWebSocketServer>();

        EditorSceneManager.MarkSceneDirty(servers.scene);
        Debug.Log("[WirePlayerServer] PlayerWebSocketServer added to Servers.");
    }
}
#endif
