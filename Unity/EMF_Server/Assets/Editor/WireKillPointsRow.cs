using System.Reflection;
using TMPro;
using UnityEditor;
using UnityEngine;

public class WireKillPointsRow
{
    public static void Execute()
    {
        if (Application.isPlaying)
        {
            Debug.LogError("[WireKillPoints] Stop Play Mode before running this script.");
            return;
        }

        // FindObjectsOfTypeAll reaches inactive objects that GameObject.Find misses
        var gsps = Resources.FindObjectsOfTypeAll<GameSettingsPanel>();
        if (gsps.Length == 0) { Debug.LogError("[WireKillPoints] GameSettingsPanel not found"); return; }
        var panel = gsps[0].gameObject;

        // Remove any existing row from a previous run
        var existing = panel.transform.Find("Kill PointsRow");
        if (existing != null) Object.DestroyImmediate(existing.gameObject);

        // Duplicate the Victory Points row as a template
        var template = panel.transform.Find("Victory PointsRow");
        if (template == null) { Debug.LogError("[WireKillPoints] Victory PointsRow not found"); return; }

        var row = Object.Instantiate(template.gameObject, panel.transform);
        row.name = "Kill PointsRow";

        // Label text
        var label = row.transform.Find("Label")?.GetComponent<TextMeshProUGUI>();
        if (label != null) label.text = "Kill Pts";

        // Placeholder text
        var placeholder = row.transform.Find("InputField/Text Area/Placeholder")?.GetComponent<TextMeshProUGUI>();
        if (placeholder != null) placeholder.text = "pts";

        // Insert just before CountdownRow
        var countdown = panel.transform.Find("CountdownRow");
        int idx = countdown != null ? countdown.GetSiblingIndex() : panel.transform.childCount - 1;
        row.transform.SetSiblingIndex(idx);

        // Wire InputField → GameSettingsPanel.killPointsField via reflection
        var gsp = panel.GetComponent<GameSettingsPanel>();
        var field = typeof(GameSettingsPanel).GetField("killPointsField",
            BindingFlags.Instance | BindingFlags.NonPublic);
        if (field != null)
        {
            var inputField = row.transform.Find("InputField")?.GetComponent<TMP_InputField>();
            field.SetValue(gsp, inputField);
            Debug.Log("[WireKillPoints] killPointsField wired successfully");
        }
        else
        {
            Debug.LogError("[WireKillPoints] killPointsField not found on GameSettingsPanel — did it compile?");
        }

        EditorUtility.SetDirty(panel);
        UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(
            UnityEngine.SceneManagement.SceneManager.GetActiveScene());

        Debug.Log("[WireKillPoints] Done — Kill PointsRow added and wired.");
    }
}
