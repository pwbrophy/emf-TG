using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;

public class FixLobbyLayout
{
    public static void Execute()
    {
        // LobbyPanel is inactive in edit mode — use Transform.Find from Canvas root
        var canvas = GameObject.Find("Canvas");
        if (canvas == null) { Debug.LogError("[FixLobbyLayout] Canvas not found"); return; }
        var lobbyT = canvas.transform.Find("LobbyPanel");
        if (lobbyT == null) { Debug.LogError("[FixLobbyLayout] LobbyPanel not found"); return; }

        // ── Helper: find a child transform by relative path ──────────────────
        Transform FindChild(string relativePath)
        {
            Transform t = lobbyT;
            foreach (var part in relativePath.Split('/'))
            {
                t = t.Find(part);
                if (t == null) return null;
            }
            return t;
        }

        // ── Delete obsolete buttons ──────────────────────────────────────────
        string[] toDelete = { "AddFakeRobotButton", "RemoveLastRobotButton", "AddPlayerButton" };
        foreach (var name in toDelete)
        {
            var t = lobbyT.Find(name);
            if (t != null)
            {
                Object.DestroyImmediate(t.gameObject);
                Debug.Log("[FixLobbyLayout] Deleted LobbyPanel/" + name);
            }
            else
            {
                Debug.Log("[FixLobbyLayout] Already gone: " + name);
            }
        }

        // ── Helper: set anchor min/max on a child path relative to LobbyPanel ─
        void SetAnchors(string relativePath, Vector2 min, Vector2 max)
        {
            var t = FindChild(relativePath);
            if (t == null) { Debug.LogError("[FixLobbyLayout] Not found: LobbyPanel/" + relativePath); return; }
            var rt = t.GetComponent<RectTransform>();
            if (rt == null) { Debug.LogError("[FixLobbyLayout] No RT: " + relativePath); return; }
            rt.anchorMin = min;
            rt.anchorMax = max;
            rt.anchoredPosition = Vector2.zero;
            rt.sizeDelta = Vector2.zero;
            Debug.Log("[FixLobbyLayout] Set anchors on LobbyPanel/" + relativePath);
        }

        // ── Game Settings — bottom-left, lower 63% of left column ───────────
        SetAnchors("GameSettingsPanel",
            new Vector2(0f, 0f), new Vector2(0.5f, 0.63f));

        // ── Robots scroll view — top of left column above game settings ──────
        SetAnchors("RobotsScrollView",
            new Vector2(0f, 0.63f), new Vector2(0.5f, 0.88f));

        // ── Players scroll view — right column, below back button ────────────
        SetAnchors("PlayersScrollView",
            new Vector2(0.5f, 0.16f), new Vector2(1f, 0.88f));

        // ── Back to Menu button — right column, above Start Game ─────────────
        SetAnchors("LobbyBackButton",
            new Vector2(0.52f, 0.16f), new Vector2(0.98f, 0.24f));

        // ── Start Game button — right column, bottom ─────────────────────────
        SetAnchors("StartGameButton",
            new Vector2(0.55f, 0.02f), new Vector2(0.98f, 0.15f));

        // ── Mark scene dirty and save ────────────────────────────────────────
        EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
        EditorSceneManager.SaveOpenScenes();
        Debug.Log("[FixLobbyLayout] Done — scene saved.");
    }
}
