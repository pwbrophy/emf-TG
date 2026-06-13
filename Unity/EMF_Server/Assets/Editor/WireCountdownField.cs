using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Adds a "Countdown (s)" TMP_InputField row to the GameSettingsPanel and
/// wires the countdownField serialized reference.  Run via menu:
/// Thundergeddon → Wire Countdown Field
/// </summary>
public static class WireCountdownField
{
    [MenuItem("Thundergeddon/Wire Countdown Field")]
    public static void Execute()
    {
        // Use FindObjectsOfTypeAll to find inactive objects too
        var panels = Resources.FindObjectsOfTypeAll<GameSettingsPanel>();
        if (panels.Length == 0)
        {
            Debug.LogError("[WireCountdownField] Could not find GameSettingsPanel component in scene.");
            return;
        }
        var panel   = panels[0];
        var panelGo = panel.gameObject;

        // Check whether countdownField is already wired
        var so = new SerializedObject(panel);
        var prop = so.FindProperty("countdownField");
        if (prop != null && prop.objectReferenceValue != null)
        {
            Debug.Log("[WireCountdownField] countdownField already wired — nothing to do.");
            return;
        }

        // Find or create a row for the countdown field.
        // Pattern: clone the last existing row (MaxTeamPoints) as a template.
        Transform parent = panelGo.transform;

        // Try to find an existing "CountdownRow" child
        var existingRow = parent.Find("CountdownRow");
        GameObject row;
        if (existingRow != null)
        {
            row = existingRow.gameObject;
        }
        else
        {
            // Clone the last child as a template
            int childCount = parent.childCount;
            if (childCount == 0)
            {
                Debug.LogError("[WireCountdownField] GameSettingsPanel has no children to clone.");
                return;
            }

            var template = parent.GetChild(childCount - 1).gameObject;
            row = Object.Instantiate(template, parent);
            row.name = "CountdownRow";
        }

        // Update the label text
        var labels = row.GetComponentsInChildren<TMP_Text>(true);
        foreach (var lbl in labels)
        {
            if (lbl.GetComponent<TMP_InputField>() == null) // not the input itself
            {
                lbl.text = "Countdown (s)";
                break;
            }
        }

        // Find the TMP_InputField in the row
        var field = row.GetComponentInChildren<TMP_InputField>(true);
        if (field == null)
        {
            Debug.LogError("[WireCountdownField] No TMP_InputField found in the cloned row.");
            Object.DestroyImmediate(row);
            return;
        }

        field.text = "5";
        field.contentType = TMP_InputField.ContentType.IntegerNumber;

        // Wire the serialized reference
        so.Update();
        prop.objectReferenceValue = field;
        so.ApplyModifiedProperties();

        EditorUtility.SetDirty(panel);
        UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(panelGo.scene);

        Debug.Log("[WireCountdownField] Done — countdownField wired to " + field.name + " in " + row.name);
    }
}
