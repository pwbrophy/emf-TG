// WireFlipButtons.cs — Thundergeddon → 8 Wire Flip Buttons
// Adds H Flip and V Flip toggle buttons to PlayingPanel and wires them to
// RobotSelectionPanel.  Run once; safe to re-run (finds existing by name).
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

public static class WireFlipButtons
{
    [MenuItem("Thundergeddon/8 Wire Flip Buttons")]
    public static void Run()
    {
        // ── Find PlayingPanel ─────────────────────────────────────────────────────
        var playingPanel = GameObject.Find("PlayingPanel");
        if (playingPanel == null)
        {
            Debug.LogError("[WireFlipButtons] PlayingPanel not found in scene.");
            return;
        }

        // ── Find or create FlipRow container ─────────────────────────────────────
        var existingRow = playingPanel.transform.Find("FlipRow");
        GameObject row;
        if (existingRow != null)
        {
            row = existingRow.gameObject;
            Debug.Log("[WireFlipButtons] FlipRow already exists — rewiring.");
        }
        else
        {
            row = new GameObject("FlipRow");
            var rowRT = row.AddComponent<RectTransform>();
            row.transform.SetParent(playingPanel.transform, false);

            // Initial position — LayoutPanels.cs will re-anchor this properly when re-run
            rowRT.anchorMin        = new Vector2(0f, 0.49f);
            rowRT.anchorMax        = new Vector2(0.37f, 0.56f);
            rowRT.pivot            = new Vector2(0.5f, 0.5f);
            rowRT.anchoredPosition = Vector2.zero;
            rowRT.sizeDelta        = Vector2.zero;

            var hlg = row.AddComponent<HorizontalLayoutGroup>();
            hlg.spacing = 8f;
            hlg.childControlWidth  = false;
            hlg.childControlHeight = false;
            hlg.childForceExpandWidth  = false;
            hlg.childForceExpandHeight = false;
            hlg.padding = new RectOffset(8, 8, 4, 4);
        }

        var font = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(
            "Assets/TextMesh Pro/Resources/Fonts & Materials/LiberationSans SDF.asset");

        // ── H Flip button ─────────────────────────────────────────────────────────
        Button flipHBtn = MakeButton(row.transform, "FlipHButton", "H Flip: OFF", font);

        // ── V Flip button ─────────────────────────────────────────────────────────
        Button flipVBtn = MakeButton(row.transform, "FlipVButton", "V Flip: OFF", font);

        // ── Wire to RobotSelectionPanel ───────────────────────────────────────────
        var sel = playingPanel.GetComponentInChildren<RobotSelectionPanel>(true);
        if (sel == null)
        {
            Debug.LogWarning("[WireFlipButtons] RobotSelectionPanel not found — wire flipHButton/flipVButton manually.");
        }
        else
        {
            var so = new SerializedObject(sel);
            so.FindProperty("flipHButton").objectReferenceValue = flipHBtn;
            so.FindProperty("flipVButton").objectReferenceValue = flipVBtn;
            so.ApplyModifiedProperties();
            EditorUtility.SetDirty(sel);
        }

        EditorUtility.SetDirty(row);
        UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(
            UnityEngine.SceneManagement.SceneManager.GetActiveScene());

        Debug.Log("[WireFlipButtons] Done — FlipRow added to PlayingPanel.");
    }

    private static Button MakeButton(Transform parent, string name, string label, TMP_FontAsset font)
    {
        var existing = parent.Find(name);
        if (existing != null)
            return existing.GetComponent<Button>();

        var go = new GameObject(name);
        var rt = go.AddComponent<RectTransform>();
        go.transform.SetParent(parent, false);
        rt.sizeDelta = new Vector2(120f, 32f);
        go.AddComponent<Image>();
        var btn = go.AddComponent<Button>();

        var lblGo = new GameObject("Label");
        var lblRT = lblGo.AddComponent<RectTransform>();
        lblGo.transform.SetParent(go.transform, false);
        lblRT.anchorMin = Vector2.zero;
        lblRT.anchorMax = Vector2.one;
        lblRT.sizeDelta = Vector2.zero;
        var tmp = lblGo.AddComponent<TextMeshProUGUI>();
        tmp.text = label;
        tmp.fontSize = 13;
        tmp.alignment = TextAlignmentOptions.Center;
        if (font) tmp.font = font;

        return btn;
    }
}
