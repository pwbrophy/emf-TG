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
        hlg.childControlWidth      = false;
        hlg.padding = new RectOffset(4, 4, 2, 2);

        // Optional subtle row background
        var bg = root.AddComponent<Image>();
        bg.color = new Color(0.12f, 0.12f, 0.18f, 1f);

        // ── Name label ────────────────────────────────────────────────────────────
        var nameGO = MakeTMPLabel(root.transform, "Name", "", 13, 90f);

        // ── IP label ──────────────────────────────────────────────────────────────
        var ipGO = MakeTMPLabel(root.transform, "Ip", "IP: ?", 11, 90f, new Color(0.7f, 0.7f, 0.7f));

        // ── Player label ──────────────────────────────────────────────────────────
        var playerGO = MakeTMPLabel(root.transform, "Player", "Unassigned", 11, 80f, new Color(0.6f, 0.8f, 1f));

        // Spacer — pushes Edit button to the right
        var spacer = new GameObject("Spacer", typeof(RectTransform));
        spacer.transform.SetParent(root.transform, false);
        var spacerLE = spacer.AddComponent<LayoutElement>();
        spacerLE.flexibleWidth = 1;

        // ── Toggle Cam button ─────────────────────────────────────────────────────
        var camGO = new GameObject("ToggleCamButton");
        camGO.transform.SetParent(root.transform, false);
        camGO.AddComponent<RectTransform>();
        camGO.AddComponent<Image>().color = new Color(0.2f, 0.2f, 0.25f, 1f);
        camGO.AddComponent<Button>();
        var camLE = camGO.AddComponent<LayoutElement>();
        camLE.preferredWidth  = 62;
        camLE.preferredHeight = 30;

        var camTextGO = new GameObject("Text");
        var camTextRT = camTextGO.AddComponent<RectTransform>();
        camTextGO.transform.SetParent(camGO.transform, false);
        camTextRT.anchorMin = Vector2.zero; camTextRT.anchorMax = Vector2.one;
        camTextRT.offsetMin = Vector2.zero; camTextRT.offsetMax = Vector2.zero;
        var camTMP = camTextGO.AddComponent<TextMeshProUGUI>();
        camTMP.text      = "Cam: OFF";
        camTMP.fontSize  = 11;
        camTMP.color     = Color.white;
        camTMP.alignment = TextAlignmentOptions.Center;
        var f2 = GetFont(); if (f2 != null) camTMP.font = f2;

        // ── Edit button ───────────────────────────────────────────────────────────
        var editGO = new GameObject("EditButton", typeof(RectTransform), typeof(Image), typeof(Button));
        editGO.transform.SetParent(root.transform, false);
        editGO.GetComponent<Image>().color = new Color(0.25f, 0.35f, 0.55f, 1f);
        var editLE = editGO.AddComponent<LayoutElement>();
        editLE.preferredWidth  = 46;
        editLE.preferredHeight = 30;

        var editTextGO = new GameObject("Text", typeof(RectTransform), typeof(CanvasRenderer), typeof(TextMeshProUGUI));
        editTextGO.transform.SetParent(editGO.transform, false);
        var editRT = editTextGO.GetComponent<RectTransform>();
        editRT.anchorMin = Vector2.zero; editRT.anchorMax = Vector2.one;
        editRT.offsetMin = Vector2.zero; editRT.offsetMax = Vector2.zero;
        var editTMP = editTextGO.GetComponent<TextMeshProUGUI>();
        editTMP.text      = "Edit";
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

    static GameObject MakeTMPLabel(Transform parent, string name, string text,
                                    float fontSize, float preferredWidth,
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
        le.preferredWidth = preferredWidth;

        return go;
    }
}
