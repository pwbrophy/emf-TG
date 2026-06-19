using UnityEngine;
using UnityEngine.UI;
using TMPro;
using UnityEditor;

/// <summary>
/// Rebuilds the RobotRow prefab to match what RobotsPanelPresenter.CreateOrUpdateRow() expects:
///   children named  "Name", "Ip", "Player", "EditButton"
/// The row uses a HorizontalLayoutGroup so it fills the scroll view width.
/// </summary>
public static class RebuildRobotRowPrefab
{
    [UnityEditor.MenuItem("Thundergeddon/Rebuild Robot Row Prefab")]
    public static void Execute()
    {
        const string prefabPath = "Assets/Prefabs/RobotRow.prefab";

        // ── Build the row in the scene temporarily ────────────────────────────────
        var root = new GameObject("RobotRow");

        var rt = root.AddComponent<RectTransform>();
        rt.sizeDelta = new Vector2(0f, 36f); // height 36 px; width set by layout

        var le = root.AddComponent<LayoutElement>();
        le.preferredHeight = 36;
        le.flexibleWidth   = 1;

        var hlg = root.AddComponent<HorizontalLayoutGroup>();
        hlg.spacing = 6;
        hlg.childForceExpandWidth  = false;
        hlg.childForceExpandHeight = true;
        hlg.childControlHeight     = true;
        hlg.childControlWidth      = true;   // respect LayoutElement.preferredWidth
        hlg.padding = new RectOffset(4, 4, 2, 2);

        // Optional subtle row background
        var bg = root.AddComponent<Image>();
        bg.color = new Color(0.12f, 0.12f, 0.18f, 1f);

        // ── Name label (flexible — takes remaining space) ─────────────────────────
        var nameGO = MakeTMPLabel(root.transform, "Name", "", 13, minWidth: 80f, flexibleWidth: 1f);

        // ── IP label ──────────────────────────────────────────────────────────────
        var ipGO = MakeTMPLabel(root.transform, "Ip", "IP: ?", 11, minWidth: 120f, flexibleWidth: 0f, color: new Color(0.7f, 0.7f, 0.7f));

        // ── Player label ──────────────────────────────────────────────────────────
        var playerGO = MakeTMPLabel(root.transform, "Player", "Unassigned", 11, minWidth: 80f, flexibleWidth: 0f, color: new Color(0.6f, 0.8f, 1f));

        // ── Turret nudge buttons (◄ ►) ───────────────────────────────────────────
        MakeTurretNudgeButton(root.transform, "TurretLeft",  "◄");
        MakeTurretNudgeButton(root.transform, "TurretRight", "►");

        // ── Alliance toggle button (D / J / ?) ────────────────────────────────────
        var allianceGO = new GameObject("AllianceButton", typeof(RectTransform), typeof(Image), typeof(Button));
        allianceGO.transform.SetParent(root.transform, false);
        allianceGO.GetComponent<Image>().color = new Color(0.35f, 0.35f, 0.35f, 1f);
        var allianceLE = allianceGO.AddComponent<LayoutElement>();
        allianceLE.minWidth       = 32;
        allianceLE.preferredWidth = 32;
        allianceLE.preferredHeight = 30;

        var allianceTextGO = new GameObject("Text", typeof(RectTransform), typeof(CanvasRenderer), typeof(TextMeshProUGUI));
        allianceTextGO.transform.SetParent(allianceGO.transform, false);
        var allianceTextRT = allianceTextGO.GetComponent<RectTransform>();
        allianceTextRT.anchorMin = Vector2.zero; allianceTextRT.anchorMax = Vector2.one;
        allianceTextRT.offsetMin = Vector2.zero; allianceTextRT.offsetMax = Vector2.zero;
        var allianceTMP = allianceTextGO.GetComponent<TextMeshProUGUI>();
        allianceTMP.text      = "?";
        allianceTMP.fontSize  = 13;
        allianceTMP.color     = Color.white;
        allianceTMP.alignment = TextAlignmentOptions.Center;
        var f2 = GetFont(); if (f2 != null) allianceTMP.font = f2;

        // ── Rename button (fixed width, right-aligned by layout) ──────────────────
        var editGO = new GameObject("EditButton", typeof(RectTransform), typeof(Image), typeof(Button));
        editGO.transform.SetParent(root.transform, false);
        editGO.GetComponent<Image>().color = new Color(0.25f, 0.45f, 0.75f, 1f);
        var editLE = editGO.AddComponent<LayoutElement>();
        editLE.minWidth      = 64;
        editLE.preferredWidth  = 64;
        editLE.preferredHeight = 30;

        var editTextGO = new GameObject("Text", typeof(RectTransform), typeof(CanvasRenderer), typeof(TextMeshProUGUI));
        editTextGO.transform.SetParent(editGO.transform, false);
        var editRT = editTextGO.GetComponent<RectTransform>();
        editRT.anchorMin = Vector2.zero; editRT.anchorMax = Vector2.one;
        editRT.offsetMin = Vector2.zero; editRT.offsetMax = Vector2.zero;
        var editTMP = editTextGO.GetComponent<TextMeshProUGUI>();
        editTMP.text      = "Rename";
        editTMP.fontSize  = 11;
        editTMP.color     = Color.white;
        editTMP.alignment = TextAlignmentOptions.Center;

        // ── Save as prefab ────────────────────────────────────────────────────────
        System.IO.Directory.CreateDirectory(Application.dataPath + "/Prefabs");
        var prefab = PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
        Object.DestroyImmediate(root);

        if (prefab != null)
            Debug.Log("[RebuildRobotRowPrefab] Saved to " + prefabPath);
        else
            Debug.LogError("[RebuildRobotRowPrefab] Failed to save prefab.");
    }

    static TMP_FontAsset GetFont()
    {
        var f = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(
            "Assets/TextMesh Pro/Resources/Fonts & Materials/LiberationSans SDF.asset");
        return f ?? TMP_Settings.defaultFontAsset;
    }

    static void MakeTurretNudgeButton(Transform parent, string name, string label)
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(Image));
        go.transform.SetParent(parent, false);
        go.GetComponent<Image>().color = new Color(0.20f, 0.40f, 0.45f, 1f);
        go.AddComponent<HoldButton>();

        var le = go.AddComponent<LayoutElement>();
        le.minWidth       = 28;
        le.preferredWidth = 28;
        le.preferredHeight = 30;

        var textGO = new GameObject("Text");
        var textRT = textGO.AddComponent<RectTransform>();
        textGO.transform.SetParent(go.transform, false);
        textRT.anchorMin = Vector2.zero; textRT.anchorMax = Vector2.one;
        textRT.offsetMin = Vector2.zero; textRT.offsetMax = Vector2.zero;
        var tmp = textGO.AddComponent<TextMeshProUGUI>();
        tmp.text      = label;
        tmp.fontSize  = 13;
        tmp.color     = Color.white;
        tmp.alignment = TextAlignmentOptions.Center;
        var f = GetFont(); if (f != null) tmp.font = f;
    }

    static GameObject MakeTMPLabel(Transform parent, string name, string text,
                                    float fontSize, float minWidth, float flexibleWidth,
                                    Color? color = null)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        go.AddComponent<RectTransform>();
        go.AddComponent<TextMeshProUGUI>(); // [RequireComponent] auto-adds CanvasRenderer

        var tmp = go.GetComponent<TextMeshProUGUI>();
        tmp.text         = text;
        tmp.fontSize     = fontSize;
        tmp.color        = color ?? Color.white;
        tmp.alignment    = TextAlignmentOptions.MidlineLeft;
        tmp.overflowMode = TextOverflowModes.Ellipsis;
        var f = GetFont(); if (f != null) tmp.font = f;

        var le = go.AddComponent<LayoutElement>();
        le.minWidth      = minWidth;
        le.flexibleWidth = flexibleWidth;

        return go;
    }
}
