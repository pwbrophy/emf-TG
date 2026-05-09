using UnityEditor;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

public class WireHandshakeToggle
{
    public static void Execute()
    {
        const string templateName = "Row_DisableMotors";
        const string newRowName   = "Row_HandshakeIr";

        // Find the Content container including inactive GameObjects via scene traversal
        var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
        Transform contentTransform = null;
        foreach (var root in scene.GetRootGameObjects())
        {
            contentTransform = root.transform.Find(
                "PlayingPanel/Body/Columns/ShotTimingColumn/ShotTimingScroll/Viewport/Content");
            if (contentTransform != null) break;
        }

        if (contentTransform == null)
        {
            Debug.LogError("[WireHandshakeToggle] Could not find ShotTimingScroll Content in scene");
            return;
        }
        Debug.Log("[WireHandshakeToggle] Found Content at: " + GetPath(contentTransform));

        // Remove old row if it already exists (idempotent)
        var existing = contentTransform.Find(newRowName);
        if (existing != null)
        {
            Undo.DestroyObjectImmediate(existing.gameObject);
            Debug.Log("[WireHandshakeToggle] Removed old " + newRowName);
        }

        // Clone the DisableMotors row as a template
        var templateGo = contentTransform.Find(templateName)?.gameObject;
        if (templateGo == null)
        {
            Debug.LogError("[WireHandshakeToggle] Template row not found: " + templateName);
            return;
        }

        var newRow = (GameObject)PrefabUtility.InstantiatePrefab(templateGo, contentTransform);
        if (newRow == null)
            newRow = Object.Instantiate(templateGo, contentTransform);
        Undo.RegisterCreatedObjectUndo(newRow, "Add Row_HandshakeIr");
        newRow.name = newRowName;

        // Place just before TotalRow
        var totalRow = contentTransform.Find("TotalRow");
        if (totalRow != null)
            newRow.transform.SetSiblingIndex(totalRow.GetSiblingIndex());
        else
            newRow.transform.SetAsLastSibling();

        // Update label text
        var topRow = newRow.transform.Find("TopRow");
        if (topRow != null)
        {
            var lbl = topRow.Find("Lbl")?.GetComponent<TextMeshProUGUI>();
            if (lbl != null) lbl.text = "Handshake IR";
        }

        var desc = newRow.transform.Find("Desc")?.GetComponent<TextMeshProUGUI>();
        if (desc != null)
            desc.text = "ACK-driven: no clock sync, eliminates ping-spike misses";

        // Find the Toggle inside the new row
        var toggleGo = topRow?.Find("Toggle");
        if (toggleGo == null)
        {
            Debug.LogError("[WireHandshakeToggle] Toggle child not found inside TopRow");
            return;
        }
        var toggle = toggleGo.GetComponent<Toggle>();
        if (toggle == null)
        {
            Debug.LogError("[WireHandshakeToggle] Toggle component missing");
            return;
        }

        // Find PlayingSettingsPanel (including inactive)
        PlayingSettingsPanel playingPanel = null;
        foreach (var root in scene.GetRootGameObjects())
        {
            playingPanel = root.GetComponentInChildren<PlayingSettingsPanel>(true);
            if (playingPanel != null) break;
        }
        if (playingPanel == null)
        {
            Debug.LogError("[WireHandshakeToggle] PlayingSettingsPanel not found in scene");
            return;
        }

        // Wire the toggle field
        var so   = new SerializedObject(playingPanel);
        var prop = so.FindProperty("useHandshakeIrToggle");
        if (prop == null)
        {
            Debug.LogError("[WireHandshakeToggle] Field 'useHandshakeIrToggle' not found on PlayingSettingsPanel");
            return;
        }
        prop.objectReferenceValue = toggle;
        so.ApplyModifiedProperties();

        // Save scene
        UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(scene);
        UnityEditor.SceneManagement.EditorSceneManager.SaveOpenScenes();

        Debug.Log("[WireHandshakeToggle] Done — Row_HandshakeIr added and useHandshakeIrToggle wired.");
    }

    static string GetPath(Transform t)
    {
        return t.parent == null ? t.name : GetPath(t.parent) + "/" + t.name;
    }
}
