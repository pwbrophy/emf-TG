using System.Reflection;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using UnityEditor;
using UnityEditor.SceneManagement;

public class FixKillPointsRow
{
    public static void Execute()
    {
        var canvas = GameObject.Find("Canvas");
        var lobbyT = canvas?.transform.Find("LobbyPanel");
        var settingsT = lobbyT?.Find("GameSettingsPanel");
        if (settingsT == null) { Debug.LogError("GameSettingsPanel not found"); return; }

        // Diagnose what Kill PointsRow children remain
        Debug.Log("[Fix] GameSettingsPanel has " + settingsT.childCount + " children:");
        for (int i = 0; i < settingsT.childCount; i++)
        {
            var c = settingsT.GetChild(i);
            string types = "";
            foreach (var comp in c.GetComponents<Component>())
                types += comp.GetType().Name + " ";
            Debug.Log("[Fix]  [" + i + "] " + c.name + " | " + types);
        }

        // Delete any Kill PointsRow that has a Toggle instead of TMP_InputField (stray)
        for (int i = settingsT.childCount - 1; i >= 0; i--)
        {
            var child = settingsT.GetChild(i);
            if (child.name != "Kill PointsRow") continue;
            bool hasInput = child.GetComponentInChildren<TMP_InputField>() != null;
            if (!hasInput)
            {
                Debug.Log("[Fix] Deleting stray Kill PointsRow at [" + i + "] (no TMP_InputField)");
                Object.DestroyImmediate(child.gameObject);
            }
            else
            {
                Debug.Log("[Fix] Keeping Kill PointsRow at [" + i + "] (has TMP_InputField)");
            }
        }

        // If no proper Kill PointsRow exists, clone CountdownRow to make one
        bool hasKillRow = false;
        for (int i = 0; i < settingsT.childCount; i++)
            if (settingsT.GetChild(i).name == "Kill PointsRow") { hasKillRow = true; break; }

        if (!hasKillRow)
        {
            Debug.Log("[Fix] No Kill PointsRow found — cloning CountdownRow");
            var countdownRow = settingsT.Find("CountdownRow");
            if (countdownRow == null) { Debug.LogError("[Fix] CountdownRow not found to clone"); return; }

            // Duplicate the countdown row
            var newRow = Object.Instantiate(countdownRow.gameObject, settingsT);
            newRow.name = "Kill PointsRow";

            // Place it just before BuzzerRow (or second-to-last if BuzzerRow not found)
            var buzzerRow = settingsT.Find("BuzzerRow");
            if (buzzerRow != null)
                newRow.transform.SetSiblingIndex(buzzerRow.GetSiblingIndex());
            else
                newRow.transform.SetSiblingIndex(settingsT.childCount - 2);

            // Update the label
            var label = newRow.transform.Find("Label");
            if (label != null)
            {
                var tmp = label.GetComponent<TMP_Text>();
                if (tmp != null) tmp.text = "Kill Pts";
                var legacyText = label.GetComponent<Text>();
                if (legacyText != null) legacyText.text = "Kill Pts";
            }

            // Wire the InputField to GameSettingsPanel.killPointsField via reflection
            var inputField = newRow.GetComponentInChildren<TMP_InputField>();
            var gsp = settingsT.GetComponent<GameSettingsPanel>();
            if (gsp != null && inputField != null)
            {
                var field = typeof(GameSettingsPanel).GetField("killPointsField",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                if (field != null)
                {
                    field.SetValue(gsp, inputField);
                    Debug.Log("[Fix] Wired killPointsField");
                }
            }

            Debug.Log("[Fix] Kill PointsRow recreated");
        }

        EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
        EditorSceneManager.SaveOpenScenes();
        Debug.Log("[Fix] Done — scene saved.");
    }
}
