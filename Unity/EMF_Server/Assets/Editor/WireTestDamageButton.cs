using UnityEngine;
using UnityEditor;
using UnityEngine.UI;
using TMPro;

public static class WireTestDamageButton
{
    [MenuItem("Thundergeddon/8 Wire Test Damage Button")]
    public static void Execute()
    {
        // Find PlayingPanel
        var playingPanel = GameObject.Find("PlayingPanel");
        if (playingPanel == null) { Debug.LogError("[WireTestDamage] PlayingPanel not found."); return; }

        // Find PingRow as a reference for placement
        var pingRow = playingPanel.transform.Find("PingRow");

        // Remove existing TestDamageRow if present (idempotent)
        var existing = playingPanel.transform.Find("TestDamageRow");
        if (existing != null) Object.DestroyImmediate(existing.gameObject);

        // Create row
        var rowGo = new GameObject("TestDamageRow");
        var rowRT = rowGo.AddComponent<RectTransform>();
        rowGo.transform.SetParent(playingPanel.transform, false);
        rowRT.anchorMin = new Vector2(0f, 1f);
        rowRT.anchorMax = new Vector2(1f, 1f);
        rowRT.pivot     = new Vector2(0.5f, 1f);
        rowRT.sizeDelta = new Vector2(0f, 40f);

        // Position just below PingRow if it exists
        if (pingRow != null)
        {
            var pingRT = pingRow.GetComponent<RectTransform>();
            rowRT.anchoredPosition = new Vector2(0f, pingRT.anchoredPosition.y - 50f);
        }
        else
        {
            rowRT.anchoredPosition = new Vector2(0f, -500f);
        }

        var hlg = rowGo.AddComponent<HorizontalLayoutGroup>();
        hlg.spacing = 8f;
        hlg.padding = new RectOffset(8, 8, 4, 4);
        hlg.childAlignment = TextAnchor.MiddleLeft;
        hlg.childForceExpandWidth  = false;
        hlg.childForceExpandHeight = true;

        // Button
        var btnGo = new GameObject("DamageButton");
        var btnRT = btnGo.AddComponent<RectTransform>();
        btnGo.transform.SetParent(rowGo.transform, false);
        btnRT.sizeDelta = new Vector2(160f, 32f);
        var img = btnGo.AddComponent<Image>();
        img.color = new Color(0.7f, 0.15f, 0.15f, 1f);
        var btn = btnGo.AddComponent<Button>();

        // Button label
        var lblGo = new GameObject("Label");
        var lblRT = lblGo.AddComponent<RectTransform>();
        lblGo.transform.SetParent(btnGo.transform, false);
        var tmp = lblGo.AddComponent<TextMeshProUGUI>();
        var font = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(
            "Assets/TextMesh Pro/Resources/Fonts & Materials/LiberationSans SDF.asset");
        tmp.font      = font;
        tmp.text      = "Damage 10%";
        tmp.fontSize  = 14f;
        tmp.alignment = TextAlignmentOptions.Center;
        lblRT.anchorMin  = Vector2.zero;
        lblRT.anchorMax  = Vector2.one;
        lblRT.sizeDelta  = Vector2.zero;
        lblRT.offsetMin  = Vector2.zero;
        lblRT.offsetMax  = Vector2.zero;

        // Add TestDamageButton component to PlayingPanel and wire refs
        var comp = playingPanel.GetComponent<TestDamageButton>();
        if (comp == null) comp = playingPanel.AddComponent<TestDamageButton>();

        var so = new SerializedObject(comp);
        so.FindProperty("damageButton").objectReferenceValue = btn;

        // Find RobotSelectionPanel component (may be on PlayingPanel itself or a child)
        var rsp = playingPanel.GetComponent<RobotSelectionPanel>();
        if (rsp == null) rsp = playingPanel.GetComponentInChildren<RobotSelectionPanel>(true);
        if (rsp != null) so.FindProperty("selectionPanel").objectReferenceValue = rsp;
        else Debug.LogWarning("[WireTestDamage] RobotSelectionPanel not found — wire manually.");

        so.ApplyModifiedProperties();

        EditorUtility.SetDirty(playingPanel);
        UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(
            UnityEngine.SceneManagement.SceneManager.GetActiveScene());

        Debug.Log("[WireTestDamage] Done. TestDamageRow added to PlayingPanel.");
    }
}
