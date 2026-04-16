using UnityEngine;
using UnityEngine.UI;

public static class TestGoToLobby
{
    public static void Execute()
    {
        var canvas = GameObject.Find("Canvas");
        if (canvas == null) { Debug.LogError("[Test] Canvas not found"); return; }
        var btn = canvas.transform.Find("MainMenuPanel/ToLobbyButton");
        if (btn == null) { Debug.LogError("[Test] ToLobbyButton not found"); return; }
        var button = btn.GetComponent<Button>();
        if (button != null) button.onClick.Invoke();
        else Debug.LogError("[Test] No Button on ToLobbyButton");
    }
}
