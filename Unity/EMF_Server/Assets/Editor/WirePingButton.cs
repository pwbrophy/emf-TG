// WirePingButton.cs — Thundergeddon → 7 Wire Ping Button
// Adds a Ping button + result label to PlayingPanel and wires RobotPingButton.
// Run once; safe to re-run (finds existing objects by name).
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

public static class WirePingButton
{
    [MenuItem("Thundergeddon/7 Wire Ping Button")]
    public static void Run()
    {
        // ── Find PlayingPanel ─────────────────────────────────────────────────────
        var playingPanel = GameObject.Find("PlayingPanel");
        if (playingPanel == null)
        {
            Debug.LogError("[WirePingButton] PlayingPanel not found in scene.");
            return;
        }

        // ── Find or create PingRow container ─────────────────────────────────────
        var existingRow = playingPanel.transform.Find("PingRow");
        GameObject row;
        if (existingRow != null)
        {
            row = existingRow.gameObject;
            Debug.Log("[WirePingButton] PingRow already exists — rewiring.");
        }
        else
        {
            row = new GameObject("PingRow");
            var rowRT = row.AddComponent<RectTransform>();
            row.transform.SetParent(playingPanel.transform, false);

            // Place it below the existing robot info labels
            rowRT.anchorMin = new Vector2(0f, 0f);
            rowRT.anchorMax = new Vector2(1f, 0f);
            rowRT.pivot     = new Vector2(0.5f, 0f);
            rowRT.anchoredPosition = new Vector2(0f, 10f);
            rowRT.sizeDelta = new Vector2(0f, 40f);

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

        // ── Ping button ───────────────────────────────────────────────────────────
        var existingBtn = row.transform.Find("PingButton");
        GameObject btnGo;
        if (existingBtn != null)
        {
            btnGo = existingBtn.gameObject;
        }
        else
        {
            btnGo = new GameObject("PingButton");
            var btnRT = btnGo.AddComponent<RectTransform>();
            btnGo.transform.SetParent(row.transform, false);
            btnRT.sizeDelta = new Vector2(100f, 32f);
            btnGo.AddComponent<Image>();
            btnGo.AddComponent<Button>();

            var lblGo = new GameObject("Label");
            var lblRT = lblGo.AddComponent<RectTransform>();
            lblGo.transform.SetParent(btnGo.transform, false);
            lblRT.anchorMin = Vector2.zero;
            lblRT.anchorMax = Vector2.one;
            lblRT.sizeDelta = Vector2.zero;
            var lbl = lblGo.AddComponent<TextMeshProUGUI>();
            lbl.text = "Ping";
            lbl.fontSize = 14;
            lbl.alignment = TextAlignmentOptions.Center;
            if (font) lbl.font = font;
        }

        // ── Result label ──────────────────────────────────────────────────────────
        var existingResult = row.transform.Find("PingResult");
        GameObject resultGo;
        if (existingResult != null)
        {
            resultGo = existingResult.gameObject;
        }
        else
        {
            resultGo = new GameObject("PingResult");
            var resultRT = resultGo.AddComponent<RectTransform>();
            resultGo.transform.SetParent(row.transform, false);
            resultRT.sizeDelta = new Vector2(240f, 32f);
            var resultTmp = resultGo.AddComponent<TextMeshProUGUI>();
            resultTmp.text = "—";
            resultTmp.fontSize = 13;
            resultTmp.alignment = TextAlignmentOptions.MidlineLeft;
            if (font) resultTmp.font = font;
        }

        // ── RobotPingButton component ────────────────────────────────────────────
        var pingComp = row.GetComponent<RobotPingButton>();
        if (pingComp == null) pingComp = row.AddComponent<RobotPingButton>();

        var so = new SerializedObject(pingComp);
        so.FindProperty("pingButton").objectReferenceValue   = btnGo.GetComponent<Button>();
        so.FindProperty("resultLabel").objectReferenceValue  = resultGo.GetComponent<TextMeshProUGUI>();

        // Find RobotSelectionPanel on the PlayingPanel
        var sel = playingPanel.GetComponentInChildren<RobotSelectionPanel>(true);
        if (sel != null)
            so.FindProperty("selectionPanel").objectReferenceValue = sel;
        else
            Debug.LogWarning("[WirePingButton] RobotSelectionPanel not found — wire manually.");

        so.ApplyModifiedProperties();

        EditorUtility.SetDirty(row);
        UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(
            UnityEngine.SceneManagement.SceneManager.GetActiveScene());

        Debug.Log("[WirePingButton] Done — PingRow added to PlayingPanel.");
    }
}
