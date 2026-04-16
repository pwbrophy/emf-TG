// WireInputMonitor.cs
// Removes the operator tank-control widgets from PlayingPanel and adds
// a PlayerInputMonitor scroll view showing live phone player inputs.
// Menu: Thundergeddon / 6 Wire Input Monitor

#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;
using UnityEditor.SceneManagement;
using UnityEngine.UI;
using TMPro;

public static class WireInputMonitor
{
    [MenuItem("Thundergeddon/6 Wire Input Monitor")]
    public static void Execute()
    {
        var playingPanel = GameObject.Find("PlayingPanel");
        if (playingPanel == null)
        {
            Debug.LogError("[WireInputMonitor] PlayingPanel not found in scene.");
            return;
        }

        // ── 1. Remove old operator controls ─────────────────────────────────────

        string[] removeNames =
        {
            "JoystickBase", "TurretSlider", "ShootButton",
            "ShootResultLabel", "CooldownLabel"
        };

        foreach (string n in removeNames)
        {
            var child = playingPanel.transform.Find(n);
            if (child != null)
            {
                Object.DestroyImmediate(child.gameObject);
                Debug.Log("[WireInputMonitor] Removed " + n);
            }
        }

        // Unwire ShootingController's UI refs to avoid null warnings
        var shooting = playingPanel.GetComponent<ShootingController>();
        if (shooting == null)
            shooting = playingPanel.GetComponentInChildren<ShootingController>();
        if (shooting != null)
        {
            var so = new SerializedObject(shooting);
            so.FindProperty("shootButton")   ?.SetValue(null);
            so.FindProperty("resultLabel")   ?.SetValue(null);
            so.FindProperty("cooldownLabel") ?.SetValue(null);
            so.ApplyModifiedProperties();
        }

        // ── 2. Create InputsScrollView ───────────────────────────────────────────

        var font = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(
            "Assets/TextMesh Pro/Resources/Fonts & Materials/LiberationSans SDF.asset");

        // ScrollView root
        var sv = new GameObject("InputsScrollView");
        sv.transform.SetParent(playingPanel.transform, false);

        var svRT = sv.AddComponent<RectTransform>();
        svRT.anchorMin = new Vector2(0f, 0f);
        svRT.anchorMax = new Vector2(0.48f, 0.88f);
        svRT.offsetMin = new Vector2(4f, 4f);
        svRT.offsetMax = new Vector2(-4f, -4f);

        var svImg = sv.AddComponent<Image>();
        svImg.color = new Color(0.04f, 0.04f, 0.07f, 0.95f);

        var scrollRect = sv.AddComponent<ScrollRect>();
        scrollRect.horizontal = false;
        scrollRect.vertical   = true;

        // Viewport
        var vp = new GameObject("Viewport");
        var vpRT = vp.AddComponent<RectTransform>();
        vp.transform.SetParent(sv.transform, false);
        vpRT.anchorMin = Vector2.zero;
        vpRT.anchorMax = Vector2.one;
        vpRT.offsetMin = Vector2.zero;
        vpRT.offsetMax = Vector2.zero;

        vp.AddComponent<Mask>().showMaskGraphic = false;
        var vpImg = vp.AddComponent<Image>();
        vpImg.color = Color.clear;

        scrollRect.viewport = vpRT;

        // Content
        var content = new GameObject("Content");
        var cRT = content.AddComponent<RectTransform>();
        content.transform.SetParent(vp.transform, false);
        cRT.anchorMin = new Vector2(0f, 1f);
        cRT.anchorMax = new Vector2(1f, 1f);
        cRT.pivot     = new Vector2(0.5f, 1f);
        cRT.offsetMin = Vector2.zero;
        cRT.offsetMax = Vector2.zero;

        var vlg = content.AddComponent<VerticalLayoutGroup>();
        vlg.childForceExpandWidth  = true;
        vlg.childForceExpandHeight = false;
        vlg.childAlignment         = TextAnchor.UpperLeft;
        vlg.spacing                = 2f;
        vlg.padding                = new RectOffset(4, 4, 4, 4);

        var csf = content.AddComponent<ContentSizeFitter>();
        csf.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
        csf.verticalFit   = ContentSizeFitter.FitMode.PreferredSize;

        scrollRect.content = cRT;

        // Header label
        var hdr = new GameObject("Header");
        var hdrRT = hdr.AddComponent<RectTransform>();
        hdr.transform.SetParent(content.transform, false);
        hdrRT.anchorMin = new Vector2(0f, 1f);
        hdrRT.anchorMax = new Vector2(1f, 1f);
        hdrRT.offsetMin = Vector2.zero;
        hdrRT.offsetMax = Vector2.zero;

        var hdrLe = hdr.AddComponent<LayoutElement>();
        hdrLe.preferredHeight = 22f;
        hdrLe.flexibleWidth   = 1f;

        var hdrBg = hdr.AddComponent<Image>();
        hdrBg.color = new Color(0.08f, 0.08f, 0.14f);

        var hdrTmpGo = new GameObject("Text");
        var hdrTmpRT = hdrTmpGo.AddComponent<RectTransform>();
        hdrTmpGo.transform.SetParent(hdr.transform, false);
        hdrTmpRT.anchorMin = Vector2.zero;
        hdrTmpRT.anchorMax = Vector2.one;
        hdrTmpRT.offsetMin = new Vector2(8f, 0f);
        hdrTmpRT.offsetMax = new Vector2(-8f, 0f);

        var hdrTmp = hdrTmpGo.AddComponent<TextMeshProUGUI>();
        hdrTmp.text      = "PLAYER INPUTS";
        hdrTmp.fontSize  = 11f;
        hdrTmp.color     = new Color(0.45f, 0.45f, 0.65f);
        hdrTmp.alignment = TextAlignmentOptions.MidlineLeft;
        hdrTmp.fontStyle = FontStyles.Bold;
        if (font != null) hdrTmp.font = font;

        // ── 3. Add PlayerInputMonitor component ──────────────────────────────────

        var monitor = playingPanel.GetComponent<PlayerInputMonitor>();
        if (monitor == null) monitor = playingPanel.AddComponent<PlayerInputMonitor>();

        var monitorSO = new SerializedObject(monitor);
        monitorSO.FindProperty("rowContainer").objectReferenceValue = cRT;
        monitorSO.ApplyModifiedProperties();

        // ── Done ─────────────────────────────────────────────────────────────────

        EditorSceneManager.MarkSceneDirty(playingPanel.scene);
        Debug.Log("[WireInputMonitor] Done — InputsScrollView added, old controls removed.");
    }
}

// Helper: set SerializedProperty value generically
internal static class SerializedPropertyExt
{
    public static void SetValue(this SerializedProperty prop, Object value)
    {
        if (prop != null) prop.objectReferenceValue = value;
    }
}
#endif
