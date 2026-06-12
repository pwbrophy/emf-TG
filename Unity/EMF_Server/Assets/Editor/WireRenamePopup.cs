/// WireRenamePopup.cs
/// Menu: Thundergeddon → 8 Wire Rename Popup
///
/// Creates a full-screen modal overlay under Canvas named "RenamePopupPanel"
/// containing a centred window with a name input, player dropdown, and OK/Cancel
/// buttons.  Adds the RenamePopup component, wires all serialized fields, then
/// sets the renamePopup reference on RobotsPanelPresenter.
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

        // ── Overlay (full-screen, semi-transparent) ──────────────────────────
        var overlay = new GameObject(PanelName);
        var overlayRT = overlay.AddComponent<RectTransform>();
        overlay.transform.SetParent(canvas.transform, false);
        overlayRT.anchorMin = Vector2.zero;
        overlayRT.anchorMax = Vector2.one;
        overlayRT.offsetMin = Vector2.zero;
        overlayRT.offsetMax = Vector2.zero;

        var overlayImg = overlay.AddComponent<Image>();
        overlayImg.color = new Color(0, 0, 0, 0.6f);

        // ── Window (centred, 440×320) ─────────────────────────────────────────
        var window = new GameObject("Window");
        var windowRT = window.AddComponent<RectTransform>();
        window.transform.SetParent(overlay.transform, false);
        windowRT.anchorMin = new Vector2(0.5f, 0.5f);
        windowRT.anchorMax = new Vector2(0.5f, 0.5f);
        windowRT.pivot     = new Vector2(0.5f, 0.5f);
        windowRT.sizeDelta = new Vector2(440, 320);
        windowRT.anchoredPosition = Vector2.zero;

        var windowImg = window.AddComponent<Image>();
        windowImg.color = new Color(0.15f, 0.15f, 0.15f, 1f);

        var vlg = window.AddComponent<VerticalLayoutGroup>();
        vlg.padding = new RectOffset(20, 20, 20, 20);
        vlg.spacing = 14;
        vlg.childForceExpandWidth  = true;
        vlg.childForceExpandHeight = false;
        vlg.childControlWidth  = true;
        vlg.childControlHeight = true;

        var csf = window.AddComponent<ContentSizeFitter>();
        csf.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

        var font = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(
            "Assets/TextMesh Pro/Resources/Fonts & Materials/LiberationSans SDF.asset");

        // ── Name row ─────────────────────────────────────────────────────────
        AddLabel(window.transform, "NameLabel", "Robot Name:", font);
        var nameInput = AddInputField(window.transform, "NameInput", "Enter name…", font);

        // ── Player row ────────────────────────────────────────────────────────
        AddLabel(window.transform, "PlayerLabel", "Assigned Player:", font);
        var playerDropdown = AddDropdown(window.transform, "PlayerDropdown", font);

        // ── Button row ────────────────────────────────────────────────────────
        var btnRow = new GameObject("ButtonRow");
        var btnRowRT = btnRow.AddComponent<RectTransform>();
        btnRow.transform.SetParent(window.transform, false);

        var hlg = btnRow.AddComponent<HorizontalLayoutGroup>();
        hlg.spacing = 16;
        hlg.childForceExpandWidth  = true;
        hlg.childForceExpandHeight = false;
        hlg.childControlWidth  = true;
        hlg.childControlHeight = true;

        var btnRowLE = btnRow.AddComponent<LayoutElement>();
        btnRowLE.preferredHeight = 44;

        var okButton     = AddButton(btnRow.transform, "OkButton",     "OK",     font, new Color(0.18f, 0.54f, 0.34f));
        var cancelButton = AddButton(btnRow.transform, "CancelButton", "Cancel", font, new Color(0.45f, 0.13f, 0.13f));

        // ── RenamePopup component ─────────────────────────────────────────────
        var popup = overlay.AddComponent<RenamePopup>();
        SetField(popup, "nameInput",       nameInput);
        SetField(popup, "playerDropdown",  playerDropdown);
        SetField(popup, "okButton",        okButton);
        SetField(popup, "cancelButton",    cancelButton);

        // Start inactive — it activates itself when Open() is called.
        overlay.SetActive(false);

        // ── Wire to RobotsPanelPresenter ──────────────────────────────────────
        var lobbyT = canvas.transform.Find("LobbyPanel");
        if (lobbyT != null)
        {
            var presenter = lobbyT.GetComponentInChildren<RobotsPanelPresenter>(true);
            if (presenter != null)
            {
                SetField(presenter, "renamePopup", popup);
                EditorUtility.SetDirty(presenter);
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
        var rt = go.AddComponent<RectTransform>();
        go.transform.SetParent(parent, false);

        var tmp = go.AddComponent<TextMeshProUGUI>();
        tmp.text     = text;
        tmp.fontSize = 16;
        if (font != null) tmp.font = font;
        tmp.color = Color.white;

        var le = go.AddComponent<LayoutElement>();
        le.preferredHeight = 22;
    }

    static TMP_InputField AddInputField(Transform parent, string goName, string placeholder, TMP_FontAsset font)
    {
        var go = new GameObject(goName);
        var rt = go.AddComponent<RectTransform>();
        go.transform.SetParent(parent, false);

        var img = go.AddComponent<Image>();
        img.color = new Color(0.25f, 0.25f, 0.25f);

        var le = go.AddComponent<LayoutElement>();
        le.preferredHeight = 40;

        // Text area
        var textArea = new GameObject("TextArea");
        textArea.AddComponent<RectTransform>();
        textArea.transform.SetParent(go.transform, false);
        var mask = textArea.AddComponent<RectMask2D>();

        var taRT = textArea.GetComponent<RectTransform>();
        taRT.anchorMin = Vector2.zero;
        taRT.anchorMax = Vector2.one;
        taRT.offsetMin = new Vector2(8, 4);
        taRT.offsetMax = new Vector2(-8, -4);

        // Placeholder
        var phGO = new GameObject("Placeholder");
        phGO.AddComponent<RectTransform>().SetParent(textArea.transform, false);
        var phRT = phGO.GetComponent<RectTransform>();
        phRT.anchorMin = Vector2.zero; phRT.anchorMax = Vector2.one;
        phRT.offsetMin = Vector2.zero; phRT.offsetMax = Vector2.zero;
        var phTMP = phGO.AddComponent<TextMeshProUGUI>();
        phTMP.text      = placeholder;
        phTMP.fontSize  = 18;
        phTMP.color     = new Color(0.6f, 0.6f, 0.6f);
        phTMP.fontStyle = FontStyles.Italic;
        if (font != null) phTMP.font = font;

        // Text
        var textGO = new GameObject("Text");
        textGO.AddComponent<RectTransform>().SetParent(textArea.transform, false);
        var txtRT = textGO.GetComponent<RectTransform>();
        txtRT.anchorMin = Vector2.zero; txtRT.anchorMax = Vector2.one;
        txtRT.offsetMin = Vector2.zero; txtRT.offsetMax = Vector2.zero;
        var txt = textGO.AddComponent<TextMeshProUGUI>();
        txt.fontSize = 18;
        txt.color    = Color.white;
        if (font != null) txt.font = font;

        var inputField = go.AddComponent<TMP_InputField>();
        inputField.textViewport   = taRT;
        inputField.textComponent  = txt;
        inputField.placeholder    = phTMP;
        inputField.characterLimit = 24;

        return inputField;
    }

    static TMP_Dropdown AddDropdown(Transform parent, string goName, TMP_FontAsset font)
    {
        var go = new GameObject(goName);
        go.AddComponent<RectTransform>();
        go.transform.SetParent(parent, false);

        var img = go.AddComponent<Image>();
        img.color = new Color(0.25f, 0.25f, 0.25f);

        var le = go.AddComponent<LayoutElement>();
        le.preferredHeight = 40;

        var labelGO = new GameObject("Label");
        labelGO.AddComponent<RectTransform>().SetParent(go.transform, false);
        var labelRT = labelGO.GetComponent<RectTransform>();
        labelRT.anchorMin = Vector2.zero; labelRT.anchorMax = Vector2.one;
        labelRT.offsetMin = new Vector2(8, 4); labelRT.offsetMax = new Vector2(-30, -4);
        var labelTMP = labelGO.AddComponent<TextMeshProUGUI>();
        labelTMP.fontSize  = 18;
        labelTMP.color     = Color.white;
        labelTMP.alignment = TextAlignmentOptions.MidlineLeft;
        if (font != null) labelTMP.font = font;

        var dd = go.AddComponent<TMP_Dropdown>();
        dd.captionText = labelTMP;

        return dd;
    }

    static Button AddButton(Transform parent, string goName, string label, TMP_FontAsset font, Color bgColor)
    {
        var go = new GameObject(goName);
        go.AddComponent<RectTransform>().SetParent(parent, false);

        var img = go.AddComponent<Image>();
        img.color = bgColor;

        var btn = go.AddComponent<Button>();
        var colors = btn.colors;
        colors.normalColor      = bgColor;
        colors.highlightedColor = bgColor * 1.2f;
        colors.pressedColor     = bgColor * 0.8f;
        btn.colors = colors;

        var textGO = new GameObject("Text");
        textGO.AddComponent<RectTransform>().SetParent(go.transform, false);
        var tRT = textGO.GetComponent<RectTransform>();
        tRT.anchorMin = Vector2.zero; tRT.anchorMax = Vector2.one;
        tRT.offsetMin = Vector2.zero; tRT.offsetMax = Vector2.zero;

        var tmp = textGO.AddComponent<TextMeshProUGUI>();
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
        if (f != null)
            f.SetValue(target, value);
        else
            Debug.LogWarning($"[WireRenamePopup] Field '{fieldName}' not found on {target.GetType().Name}");
    }
}
