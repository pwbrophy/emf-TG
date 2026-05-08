using UnityEditor;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

public class WireHandshakeToggle
{
    public static void Execute()
    {
        const string contentPath =
            "Canvas/PlayingPanel/Body/Columns/ShotTimingColumn/ShotTimingScroll/Viewport/Content";
        const string templateName = "Row_DisableMotors";
        const string newRowName   = "Row_HandshakeIr";

        // ── Find the scroll-view content container ──────────────────────────────
        var contentGo = GameObject.Find(contentPath);
        if (contentGo == null)
        {
            Debug.LogError("[WireHandshakeToggle] Could not find Content at: " + contentPath);
            return;
        }

        // Remove old row if it already exists (idempotent)
        var existing = contentGo.transform.Find(newRowName);
        if (existing != null)
        {
            Undo.DestroyObjectImmediate(existing.gameObject);
            Debug.Log("[WireHandshakeToggle] Removed old " + newRowName);
        }

        // ── Clone the DisableMotors row as a template ──────────────────────────
        var templateGo = contentGo.transform.Find(templateName)?.gameObject;
        if (templateGo == null)
        {
            Debug.LogError("[WireHandshakeToggle] Template row not found: " + templateName);
            return;
        }

        var newRow = (GameObject)PrefabUtility.InstantiatePrefab(templateGo, contentGo.transform);
        if (newRow == null)
        {
            // Fallback: plain Instantiate
            newRow = Object.Instantiate(templateGo, contentGo.transform);
        }
        Undo.RegisterCreatedObjectUndo(newRow, "Add Row_HandshakeIr");
        newRow.name = newRowName;

        // Place it at the end of the content list
        newRow.transform.SetAsLastSibling();

        // ── Update label text ──────────────────────────────────────────────────
        var topRow = newRow.transform.Find("TopRow");
        if (topRow != null)
        {
            var lbl = topRow.Find("Lbl")?.GetComponent<TextMeshProUGUI>();
            if (lbl != null) lbl.text = "Handshake IR";
        }

        var desc = newRow.transform.Find("Desc")?.GetComponent<TextMeshProUGUI>();
        if (desc != null)
            desc.text = "ACK-driven mode: no clock sync, eliminates ping-spike misses (~300–500 ms/shot)";

        // ── Find the Toggle inside the new row ─────────────────────────────────
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

        // ── Find PlayingSettingsPanel and wire the field ────────────────────────
        var playingPanel = Object.FindObjectOfType<PlayingSettingsPanel>();
        if (playingPanel == null)
        {
            Debug.LogError("[WireHandshakeToggle] PlayingSettingsPanel not found in scene");
            return;
        }

        // Use SerializedObject so the assignment is recorded in the scene and shows in Inspector
        var so    = new SerializedObject(playingPanel);
        var prop  = so.FindProperty("useHandshakeIrToggle");
        if (prop == null)
        {
            Debug.LogError("[WireHandshakeToggle] Field 'useHandshakeIrToggle' not found on PlayingSettingsPanel");
            return;
        }
        prop.objectReferenceValue = toggle;
        so.ApplyModifiedProperties();

        // ── Save scene ─────────────────────────────────────────────────────────
        UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(
            UnityEngine.SceneManagement.SceneManager.GetActiveScene());
        UnityEditor.SceneManagement.EditorSceneManager.SaveOpenScenes();

        Debug.Log("[WireHandshakeToggle] Done — Row_HandshakeIr added and useHandshakeIrToggle wired.");
    }
}
