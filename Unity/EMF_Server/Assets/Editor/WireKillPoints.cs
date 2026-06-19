using System.Reflection;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

public class WireKillPoints
{
    public static void Execute()
    {
        var canvas    = GameObject.Find("Canvas");
        var settingsT = canvas?.transform.Find("LobbyPanel/GameSettingsPanel");
        if (settingsT == null) { Debug.LogError("GameSettingsPanel not found"); return; }

        var gsp = settingsT.GetComponent<GameSettingsPanel>();
        if (gsp == null) { Debug.LogError("GameSettingsPanel component missing"); return; }

        // Check current serialized value
        var killField = typeof(GameSettingsPanel).GetField("killPointsField",
            BindingFlags.NonPublic | BindingFlags.Instance);
        var current = killField?.GetValue(gsp) as TMP_InputField;
        Debug.Log("[Wire] killPointsField is currently: " + (current == null ? "NULL" : current.gameObject.name));

        // Find the Kill PointsRow and its InputField
        var killRow = settingsT.Find("Kill PointsRow");
        if (killRow == null) { Debug.LogError("Kill PointsRow not found"); return; }

        var input = killRow.GetComponentInChildren<TMP_InputField>();
        if (input == null) { Debug.LogError("No TMP_InputField in Kill PointsRow"); return; }

        // Wire via reflection (serialized field requires SerializedObject for persistence)
        var so   = new SerializedObject(gsp);
        var prop = so.FindProperty("killPointsField");
        if (prop == null) { Debug.LogError("killPointsField property not found"); return; }
        prop.objectReferenceValue = input;
        so.ApplyModifiedProperties();
        Debug.Log("[Wire] Wired killPointsField -> " + input.gameObject.name);

        EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
        EditorSceneManager.SaveOpenScenes();
        Debug.Log("[Wire] Done.");
    }
}
