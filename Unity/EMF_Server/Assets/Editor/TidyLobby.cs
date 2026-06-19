using UnityEngine;
using UnityEngine.UI;
using UnityEditor;
using UnityEditor.SceneManagement;

public class TidyLobby
{
    public static void Execute()
    {
        var canvas = GameObject.Find("Canvas");
        if (canvas == null) { Debug.LogError("Canvas not found"); return; }

        var lobbyT  = canvas.transform.Find("LobbyPanel");
        var settingsT = lobbyT?.Find("GameSettingsPanel");
        if (lobbyT == null || settingsT == null)
        { Debug.LogError("LobbyPanel or GameSettingsPanel not found"); return; }

        // ── 1. Delete unwanted rows from GameSettingsPanel ───────────────────
        string[] rowsToDelete = { "Desert Squad Base UIDRow", "Jungle Squad Base UIDRow" };
        foreach (var name in rowsToDelete)
        {
            var t = settingsT.Find(name);
            if (t != null) { Object.DestroyImmediate(t.gameObject); Debug.Log("Deleted " + name); }
            else Debug.Log("Already gone: " + name);
        }

        // Delete the second Kill PointsRow (the stray one with a Toggle instead of InputField)
        int killRowsFound = 0;
        for (int i = settingsT.childCount - 1; i >= 0; i--)
        {
            var child = settingsT.GetChild(i);
            if (child.name == "Kill PointsRow")
            {
                killRowsFound++;
                if (killRowsFound > 1)
                {
                    Object.DestroyImmediate(child.gameObject);
                    Debug.Log("Deleted duplicate Kill PointsRow at index " + i);
                }
            }
        }

        // ── 2. Tighten the VerticalLayoutGroup on GameSettingsPanel ──────────
        var vlg = settingsT.GetComponent<VerticalLayoutGroup>();
        if (vlg != null)
        {
            vlg.spacing = 2f;
            vlg.padding = new RectOffset(6, 6, 4, 4);
            Debug.Log("Tightened VLG spacing");
        }

        // Shrink each row's preferred height to 28px
        for (int i = 0; i < settingsT.childCount; i++)
        {
            var child = settingsT.GetChild(i);
            var le = child.GetComponent<LayoutElement>();
            if (le != null) le.preferredHeight = 28f;

            var rt = child.GetComponent<RectTransform>();
            if (rt != null && rt.sizeDelta.y > 0)
                rt.sizeDelta = new Vector2(rt.sizeDelta.x, 28f);
        }

        // ── 3. Helper: set anchors on a LobbyPanel child ────────────────────
        void SetAnchors(string path, Vector2 min, Vector2 max)
        {
            var t = lobbyT.Find(path);
            if (t == null) { Debug.LogError("Not found: " + path); return; }
            var rt = t.GetComponent<RectTransform>();
            rt.anchorMin = min; rt.anchorMax = max;
            rt.anchoredPosition = Vector2.zero; rt.sizeDelta = Vector2.zero;
            Debug.Log("Anchors set: " + path);
        }

        // ── 4. Resize panels — Game Settings now takes ~43% height ──────────
        SetAnchors("GameSettingsPanel",
            new Vector2(0f,    0f),    new Vector2(0.5f,  0.43f));
        SetAnchors("RobotsScrollView",
            new Vector2(0f,    0.43f), new Vector2(0.5f,  0.88f));
        SetAnchors("PlayersScrollView",
            new Vector2(0.5f,  0.18f), new Vector2(1f,    0.88f));

        // ── 5. Buttons: Start Game above Back to Menu ────────────────────────
        SetAnchors("StartGameButton",
            new Vector2(0.52f, 0.10f), new Vector2(0.98f, 0.22f));
        SetAnchors("LobbyBackButton",
            new Vector2(0.52f, 0.01f), new Vector2(0.98f, 0.09f));

        // ── 6. Save ──────────────────────────────────────────────────────────
        EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
        EditorSceneManager.SaveOpenScenes();
        Debug.Log("[TidyLobby] Done — scene saved.");
    }
}
