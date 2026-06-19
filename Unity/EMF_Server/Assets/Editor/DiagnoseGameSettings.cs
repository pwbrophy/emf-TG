using UnityEngine;
using UnityEditor;

public class DiagnoseGameSettings
{
    public static void Execute()
    {
        var canvas = GameObject.Find("Canvas");
        if (canvas == null) { Debug.LogError("Canvas not found"); return; }

        var panel = canvas.transform.Find("LobbyPanel/GameSettingsPanel");
        if (panel == null) { Debug.LogError("GameSettingsPanel not found"); return; }

        Debug.Log("[Diag] GameSettingsPanel has " + panel.childCount + " children:");
        for (int i = 0; i < panel.childCount; i++)
        {
            var child = panel.GetChild(i);
            Debug.Log("[Diag]  [" + i + "] " + child.name);

            // Also log grandchildren so we can see labels
            for (int j = 0; j < child.childCount; j++)
                Debug.Log("[Diag]       -> " + child.GetChild(j).name);
        }
    }
}
