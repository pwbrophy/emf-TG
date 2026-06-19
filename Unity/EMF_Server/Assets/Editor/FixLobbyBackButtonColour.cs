using System.Reflection;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using UnityEditor;
using UnityEditor.SceneManagement;

public class FixLobbyBackButtonColour
{
    public static void Execute()
    {
        var canvas  = GameObject.Find("Canvas");
        var lobbyT  = canvas?.transform.Find("LobbyPanel");
        var settingsT = lobbyT?.Find("GameSettingsPanel");

        if (lobbyT == null) { Debug.LogError("LobbyPanel not found"); return; }

        // ── Fix PlayersScrollView bottom anchor (was overlapping Start Game) ─
        var psv = lobbyT.Find("PlayersScrollView");
        if (psv != null)
        {
            var rt = psv.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0.5f, 0.24f);
            rt.anchorMax = new Vector2(1f,   0.88f);
            rt.anchoredPosition = Vector2.zero;
            rt.sizeDelta = Vector2.zero;
            Debug.Log("[Fix] PlayersScrollView anchor bottom raised to 0.24");
        }

        // ── Fix Kill PointsRow label ─────────────────────────────────────────
        if (settingsT != null)
        {
            var killRow = settingsT.Find("Kill PointsRow");
            if (killRow != null)
            {
                // Try "Label" child first, then any TMP_Text
                var labelT = killRow.Find("Label") ?? killRow.Find("Lbl");
                TMP_Text tmp = null;
                if (labelT != null) tmp = labelT.GetComponent<TMP_Text>();
                if (tmp == null) tmp = killRow.GetComponentInChildren<TMP_Text>();

                if (tmp != null && tmp.text != "Kill Pts")
                {
                    tmp.text = "Kill Pts";
                    Debug.Log("[Fix] Updated Kill PointsRow label to 'Kill Pts'");
                }
                else if (tmp != null)
                    Debug.Log("[Fix] Kill PointsRow label already correct: " + tmp.text);
            }
            else Debug.LogError("[Fix] Kill PointsRow not found");
        }

        // ── Style the Back to Menu button ────────────────────────────────────
        var btn = lobbyT.Find("LobbyBackButton");
        if (btn != null)
        {
            var img = btn.GetComponent<Image>();
            if (img != null) img.color = new Color(0.22f, 0.22f, 0.28f, 1f);
            var b = btn.GetComponent<Button>();
            if (b != null)
            {
                var cb = b.colors;
                cb.normalColor      = new Color(0.22f, 0.22f, 0.28f, 1f);
                cb.highlightedColor = new Color(0.32f, 0.32f, 0.40f, 1f);
                cb.pressedColor     = new Color(0.15f, 0.15f, 0.20f, 1f);
                cb.selectedColor    = new Color(0.22f, 0.22f, 0.28f, 1f);
                b.colors = cb;
            }
            var legacyTxt = btn.GetComponentInChildren<Text>();
            if (legacyTxt != null) legacyTxt.color = Color.white;
        }

        EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
        EditorSceneManager.SaveOpenScenes();
        Debug.Log("[Fix] Done — scene saved.");
    }
}
