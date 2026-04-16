using UnityEngine;

/// Directly flips panel visibility to preview the Lobby in play mode.
public static class TestLobby
{
    public static void Execute()
    {
        var canvas = GameObject.Find("Canvas");
        if (canvas == null) { Debug.LogError("[TestLobby] Canvas not found"); return; }
        var mainMenu = canvas.transform.Find("MainMenuPanel");
        var lobby    = canvas.transform.Find("LobbyPanel");
        if (mainMenu) mainMenu.gameObject.SetActive(false);
        if (lobby)    lobby.gameObject.SetActive(true);
        Debug.Log("[TestLobby] Swapped panels directly");
    }
}
