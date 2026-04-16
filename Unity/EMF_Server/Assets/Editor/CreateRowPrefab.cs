using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

public class CreateRowPrefab
{
    public static void Execute()
    {
        string prefabPath = "Assets/Prefabs/RobotRow.prefab";

        // Build the row
        var row = new GameObject("RobotRow");
        row.AddComponent<RectTransform>().sizeDelta = new Vector2(400, 40);

        var nameGO    = MakeTMPLabel(row.transform, "Name",   "(robot)",    16, 120);
        var ipGO      = MakeTMPLabel(row.transform, "Ip",     "IP: ?",      14, 100);
        var playerGO  = MakeTMPLabel(row.transform, "Player", "Unassigned", 14, 100);
        var editGO    = MakeButton  (row.transform, "EditButton", "Edit",   14,  60);

        Debug.Log("[CreateRowPrefab] Row built, attempting to save at: " + prefabPath);

        bool savedOk;
        var prefabAsset = PrefabUtility.SaveAsPrefabAsset(row, prefabPath, out savedOk);

        Object.DestroyImmediate(row);

        Debug.Log($"[CreateRowPrefab] savedOk={savedOk}, prefabAsset={prefabAsset}");

        if (!savedOk || prefabAsset == null)
        {
            Debug.LogError("[CreateRowPrefab] Save failed. Path: " + prefabPath);
            return;
        }

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        Debug.Log("[CreateRowPrefab] Prefab saved successfully.");

        // Wire into RobotsPanelPresenter
        var lobbyPanel = GameObject.Find("Canvas/LobbyPanel");
        if (lobbyPanel == null) { Debug.LogWarning("[CreateRowPrefab] LobbyPanel not found."); return; }

        var rpp = lobbyPanel.GetComponent<RobotsPanelPresenter>();
        if (rpp == null) { Debug.LogWarning("[CreateRowPrefab] RobotsPanelPresenter not found."); return; }

        var f = typeof(RobotsPanelPresenter).GetField("rowPrefab",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        if (f == null) { Debug.LogWarning("[CreateRowPrefab] rowPrefab field not found via reflection."); return; }

        f.SetValue(rpp, prefabAsset);
        EditorUtility.SetDirty(rpp);

        UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(
            UnityEngine.SceneManagement.SceneManager.GetActiveScene());

        Debug.Log("[CreateRowPrefab] rowPrefab wired into RobotsPanelPresenter.");
    }

    static GameObject MakeTMPLabel(Transform parent, string name, string text, int fontSize, float width)
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(TextMeshProUGUI));
        go.transform.SetParent(parent, false);
        var le = go.AddComponent<LayoutElement>();
        le.preferredWidth = width;
        var tmp = go.GetComponent<TextMeshProUGUI>();
        tmp.text = text;
        tmp.fontSize = fontSize;
        tmp.color = Color.white;
        return go;
    }

    static GameObject MakeButton(Transform parent, string name, string label, int fontSize, float width)
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(Image), typeof(Button));
        go.transform.SetParent(parent, false);
        var le = go.AddComponent<LayoutElement>();
        le.preferredWidth = width;

        var textGO = new GameObject("Text", typeof(RectTransform), typeof(CanvasRenderer), typeof(TextMeshProUGUI));
        textGO.transform.SetParent(go.transform, false);
        var rt = textGO.GetComponent<RectTransform>();
        rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
        rt.offsetMin = Vector2.zero; rt.offsetMax = Vector2.zero;
        var tmp = textGO.GetComponent<TextMeshProUGUI>();
        tmp.text = label;
        tmp.fontSize = fontSize;
        tmp.color = Color.white;
        tmp.alignment = TextAlignmentOptions.Center;
        return go;
    }
}
