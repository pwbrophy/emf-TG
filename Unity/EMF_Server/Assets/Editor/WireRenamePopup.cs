/// WireRenamePopup.cs
/// Menu: Thundergeddon → 8 Wire Rename Popup
///
/// Creates a full-screen modal overlay under Canvas named "RenamePopupPanel"
/// with a centred window containing only a robot-name input and OK/Cancel.
/// Adds the RenamePopup component, wires all serialized fields, then sets the
/// renamePopup reference on RobotsPanelPresenter.
///
/// Safe to re-run: destroys and recreates the panel each time.

using System.Reflection;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;

public static class WireRenamePopup
{
    const string PanelName = "RenamePopupPanel";

    [MenuItem("Thundergeddon/8 Wire Rename Popup")]
    public static void Execute()
    {
        var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();

        // Find Canvas
        GameObject canvas = null;
        foreach (var r in scene.GetRootGameObjects())
            if (r.name == "Canvas") { canvas = r; break; }
        if (canvas == null) { Debug.LogError("[WireRenamePopup] Canvas not found."); return; }

        // Destroy old panel if it exists
        var existing = canvas.transform.Find(PanelName);
        if (existing != null) Object.DestroyImmediate(existing.gameObject);

        var font = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(
            "Assets/TextMesh Pro/Resources/Fonts & Materials/LiberationSans SDF.asset");

        // ── Overlay (full-screen, semi-transparent click-blocker) ─────────────
        var overlay = new GameObject(PanelName);
        var overlayRT = overlay.AddComponent<RectTransform>();
        overlay.transform.SetParent(canvas.transform, false);
        overlayRT.anchorMin = Vector2.zero;
        overlayRT.anchorMax = Vector2.one;
        overlayRT.offsetMin = Vector2.zero;
        overlayRT.offsetMax = Vector2.zero;

        var overlayImg = overlay.AddComponent<Image>();
        overlayImg.color = new Color(0, 0, 0, 0.65f);

        // ── Window (centred, 400×200) ─────────────────────────────────────────
        var window = new GameObject("Window");
        var windowRT = window.AddComponent<RectTransform>();
        window.transform.SetParent(overlay.transform, false);
        windowRT.anchorMin        = new Vector2(0.5f, 0.5f);
        windowRT.anchorMax        = new Vector2(0.5f, 0.5f);
        windowRT.pivot            = new Vector2(0.5f, 0.5f);
        windowRT.sizeDelta        = new Vector2(400, 200);
        windowRT.anchoredPosition = Vector2.zero;

        var windowImg = window.AddComponent<Image>();
        windowImg.color = new Color(0.13f, 0.13f, 0.18f, 1f);

        var vlg = window.AddComponent<VerticalLayoutGroup>();
        vlg.padding                = new RectOffset(24, 24, 24, 24);
        vlg.spacing                = 16;
        vlg.childForceExpandWidth  = true;
        vlg.childForceExpandHeight = false;
        vlg.childControlWidth      = true;
        vlg.childControlHeight     = true;

        var csf = window.AddComponent<ContentSizeFitter>();
        csf.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

        // ── "Robot Name:" label ───────────────────────────────────────────────
        AddLabel(window.transform, "NameLabel", "Robot Name:", font);

        // ── Name input field ──────────────────────────────────────────────────
        var nameInput = AddInputField(window.transform, "NameInput", "Enter name…", font);

        // ── Button row ────────────────────────────────────────────────────────
        var btnRow = new GameObject("ButtonRow");
        btnRow.AddComponent<RectTransform>();
        btnRow.transform.SetParent(window.transform, false);

        var hlg = btnRow.AddComponent<HorizontalLayoutGroup>();
        hlg.spacing               = 16;
        hlg.childForceExpandWidth  = true;
        hlg.childForceExpandHeight = true;   // buttons fill row height
        hlg.childControlWidth      = true;
        hlg.childControlHeight     = true;

        var btnRowLE = btnRow.AddComponent<LayoutElement>();
        btnRowLE.preferredHeight = 44;
        btnRowLE.minHeight       = 44;

        var okButton     = AddButton(btnRow.transform, "OkButton",     "OK",     font, new Color(0.18f, 0.54f, 0.34f));
        var cancelButton = AddButton(btnRow.transform, "CancelButton", "Cancel", font, new Color(0.50f, 0.15f, 0.15f));

        // ── RenamePopup component ─────────────────────────────────────────────
        var popup = overlay.AddComponent<RenamePopup>();
        SetField(popup, "nameInput",   nameInput);
        SetField(popup, "okButton",    okButton);
        SetField(popup, "cancelButton", cancelButton);
        // playerDropdown intentionally left null — rename only

        overlay.SetActive(false);

        // ── Wire to RobotsPanelPresenter ──────────────────────────────────────
        var lobbyT = canvas.transform.Find("LobbyPanel");
        if (lobbyT != null)
        {
            var presenter = lobbyT.GetComponentInChildren<RobotsPanelPresenter>(true);
            if (presenter != null)
            {
                SetField(presenter, "renamePopup", popup);
                EditorUtility.SetDirty(presenter.gameObject);
                Debug.Log("[WireRenamePopup] Wired RenamePopup to RobotsPanelPresenter.");
            }
            else
                Debug.LogWarning("[WireRenamePopup] RobotsPanelPresenter not found in LobbyPanel.");
        }
        else
            Debug.LogWarning("[WireRenamePopup] LobbyPanel not found in Canvas.");

        EditorUtility.SetDirty(canvas);
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);

        Debug.Log("[WireRenamePopup] Done — RenamePopupPanel created and wired.");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    static void AddLabel(Transform parent, string goName, string text, TMP_FontAsset font)
    {
        var go = new GameObject(goName);
        go.AddComponent<RectTransform>();
        go.transform.SetParent(parent, false);

        var tmp = go.AddComponent<TextMeshProUGUI>();
        tmp.text     = text;
        tmp.fontSize = 16;
        tmp.color    = Color.white;
        if (font != null) tmp.font = font;

        var le = go.AddComponent<LayoutElement>();
        le.preferredHeight = 22;
        le.minHeight       = 22;
    }

    static TMP_InputField AddInputField(Transform parent, string goName, string placeholder, TMP_FontAsset font)
    {
        var go = new GameObject(goName);
        go.AddComponent<RectTransform>();
        go.transform.SetParent(parent, false);

        var img = go.AddComponent<Image>();
        img.color = new Color(0.22f, 0.22f, 0.28f, 1f);

        var le = go.AddComponent<LayoutElement>();
        le.preferredHeight = 44;
        le.minHeight       = 44;

        // Text area
        var area = new GameObject("TextArea");
        var areaRT = area.AddComponent<RectTransform>();
        area.transform.SetParent(go.transform, false);
        area.AddComponent<RectMask2D>();
        areaRT.anchorMin = Vector2.zero;
        areaRT.anchorMax = Vector2.one;
        areaRT.offsetMin = new Vector2(10, 4);
        areaRT.offsetMax = new Vector2(-10, -4);

        // Placeholder
        var ph = new GameObject("Placeholder");
        ph.AddComponent<RectTransform>().SetParent(area.transform, false);
        var phRT = ph.GetComponent<RectTransform>();
        phRT.anchorMin = Vector2.zero; phRT.anchorMax = Vector2.one;
        phRT.offsetMin = Vector2.zero; phRT.offsetMax = Vector2.zero;
        var phTMP = ph.AddComponent<TextMeshProUGUI>();
        phTMP.text      = placeholder;
        phTMP.fontSize  = 20;
        phTMP.color     = new Color(0.55f, 0.55f, 0.55f);
        phTMP.fontStyle = FontStyles.Italic;
        phTMP.alignment = TextAlignmentOptions.MidlineLeft;
        if (font != null) phTMP.font = font;

        // Text
        var tx = new GameObject("Text");
        tx.AddComponent<RectTransform>().SetParent(area.transform, false);
        var txRT = tx.GetComponent<RectTransform>();
        txRT.anchorMin = Vector2.zero; txRT.anchorMax = Vector2.one;
        txRT.offsetMin = Vector2.zero; txRT.offsetMax = Vector2.zero;
        var txTMP = tx.AddComponent<TextMeshProUGUI>();
        txTMP.fontSize  = 20;
        txTMP.color     = Color.white;
        txTMP.alignment = TextAlignmentOptions.MidlineLeft;
        if (font != null) txTMP.font = font;

        var field = go.AddComponent<TMP_InputField>();
        field.textViewport    = areaRT;
        field.textComponent   = txTMP;
        field.placeholder     = phTMP;
        field.characterLimit  = 24;

        return field;
    }

    static Button AddButton(Transform parent, string goName, string label, TMP_FontAsset font, Color bg)
    {
        var go = new GameObject(goName);
        go.AddComponent<RectTransform>().SetParent(parent, false);

        var img = go.AddComponent<Image>();
        img.color = bg;

        var btn = go.AddComponent<Button>();
        var cols = btn.colors;
        cols.normalColor      = bg;
        cols.highlightedColor = Color.Lerp(bg, Color.white, 0.25f);
        cols.pressedColor     = Color.Lerp(bg, Color.black, 0.25f);
        btn.colors = cols;

        // Ensure button has a LayoutElement so it gets a defined height
        var le = go.AddComponent<LayoutElement>();
        le.preferredHeight = 44;
        le.minHeight       = 44;

        var tx = new GameObject("Text");
        tx.AddComponent<RectTransform>().SetParent(go.transform, false);
        var txRT = tx.GetComponent<RectTransform>();
        txRT.anchorMin = Vector2.zero; txRT.anchorMax = Vector2.one;
        txRT.offsetMin = Vector2.zero; txRT.offsetMax = Vector2.zero;

        var tmp = tx.AddComponent<TextMeshProUGUI>();
        tmp.text      = label;
        tmp.fontSize  = 18;
        tmp.color     = Color.white;
        tmp.alignment = TextAlignmentOptions.Center;
        if (font != null) tmp.font = font;

        return btn;
    }

    static void SetField(object target, string fieldName, object value)
    {
        var f = target.GetType().GetField(fieldName,
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        if (f != null) f.SetValue(target, value);
        else Debug.LogWarning($"[WireRenamePopup] Field '{fieldName}' not found on {target.GetType().Name}");
    }
}
