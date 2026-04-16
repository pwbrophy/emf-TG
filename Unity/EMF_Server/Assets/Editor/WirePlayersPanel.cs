/// WirePlayersPanel.cs
/// Menu: Thundergeddon → 4 Wire Players Panel
///
/// Does two things in one shot:
///   1. Creates (or recreates) Assets/Prefabs/PlayerRow.prefab — a row with
///      TMP_InputField (name), TMP_Dropdown (alliance), TMP_Dropdown (robot),
///      Button (remove), and a PlayerRowUI component tying them together.
///   2. Wires the scene's LobbyPanel — creates PlayersScrollView (Viewport/Content
///      with VLG + ContentSizeFitter), assigns rowContainer / rowPrefab / addButton
///      on the PlayersEditorPanel component via reflection, and deactivates the old
///      dropdown/field controls that the panel no longer uses.
///
/// Safe to re-run: recreates the prefab fresh each time and is idempotent for the
/// scroll-view hierarchy (won't add duplicate Viewport/Content nodes).

using System.Collections.Generic;
using System.Reflection;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

public static class WirePlayersPanel
{
    const string PrefabPath = "Assets/Prefabs/PlayerRow.prefab";

    [MenuItem("Thundergeddon/4 Wire Players Panel")]
    public static void Execute()
    {
        // ── 1. Build (or rebuild) the PlayerRow prefab ────────────────────────
        var prefab = BuildPrefab();
        if (prefab == null) { Debug.LogError("[WirePlayers] Prefab build failed."); return; }

        // ── 2. Find Canvas / LobbyPanel ───────────────────────────────────────
        var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
        GameObject canvas = null;
        foreach (var r in scene.GetRootGameObjects())
            if (r.name == "Canvas") { canvas = r; break; }
        if (canvas == null) { Debug.LogError("[WirePlayers] Canvas not found."); return; }

        var lobbyT = canvas.transform.Find("LobbyPanel");
        if (lobbyT == null) { Debug.LogError("[WirePlayers] LobbyPanel not found."); return; }
        var lobby = lobbyT.gameObject;

        // ── 3. Create / repair PlayersScrollView under LobbyPanel ─────────────
        var svT = lobbyT.Find("PlayersScrollView");
        var svGO = svT != null ? svT.gameObject : new GameObject("PlayersScrollView");
        if (svT == null) svGO.transform.SetParent(lobbyT, false);
        if (svGO.GetComponent<RectTransform>() == null) svGO.AddComponent<RectTransform>();

        SetupScrollView(svGO, out var contentRT);

        // ── 4. Get or add PlayersEditorPanel ──────────────────────────────────
        var panel = lobby.GetComponent<PlayersEditorPanel>()
                 ?? lobby.AddComponent<PlayersEditorPanel>();

        var type = typeof(PlayersEditorPanel);
        SetPrivateField(panel, type, "rowContainer", contentRT);
        SetPrivateField(panel, type, "rowPrefab",    prefab);

        var addBtnT = lobbyT.Find("AddPlayerButton");
        if (addBtnT != null)
            SetPrivateField(panel, type, "addButton", addBtnT.GetComponent<Button>());
        else
            Debug.LogWarning("[WirePlayers] AddPlayerButton not found in LobbyPanel.");

        // ── 5. Deactivate superseded controls ─────────────────────────────────
        foreach (var n in new[] { "PlayersDropdown", "PlayerNameField", "AllianceDropdown", "RemovePlayerButton" })
        {
            var t = lobbyT.Find(n);
            if (t != null) t.gameObject.SetActive(false);
        }

        // ── 6. Save ───────────────────────────────────────────────────────────
        EditorUtility.SetDirty(lobby);
        UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(scene);
        UnityEditor.SceneManagement.EditorSceneManager.SaveScene(scene);

        Debug.Log("[WirePlayers] Done. PlayersEditorPanel wired with PlayerRow prefab + scroll container.");
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  Scroll-view setup (idempotent)
    // ═══════════════════════════════════════════════════════════════════════════

    static void SetupScrollView(GameObject svGO, out RectTransform contentRT)
    {
        contentRT = null;

        AddIfMissing<Image>(svGO).color = new Color(0.04f, 0.04f, 0.07f, 0.95f);
        var sr = AddIfMissing<ScrollRect>(svGO);
        sr.vertical = true; sr.horizontal = false;

        // Viewport — create outside first so we can add RT before parenting
        var vpT = svGO.transform.Find("Viewport");
        RectTransform vpRT;
        GameObject vpGO;
        if (vpT == null)
        {
            vpGO = new GameObject("Viewport");
            vpRT = vpGO.AddComponent<RectTransform>();  // RT before parenting
            vpGO.transform.SetParent(svGO.transform, false);
        }
        else
        {
            vpGO = vpT.gameObject;
            vpRT = vpGO.GetComponent<RectTransform>();
            if (vpRT == null) vpRT = vpGO.AddComponent<RectTransform>();
        }
        vpRT.anchorMin = Vector2.zero; vpRT.anchorMax = Vector2.one;
        vpRT.offsetMin = Vector2.zero; vpRT.offsetMax = Vector2.zero;
        var vpMask = AddIfMissing<Mask>(vpGO); vpMask.showMaskGraphic = false;
        AddIfMissing<Image>(vpGO).color = new Color(1, 1, 1, 0.01f);
        sr.viewport = vpRT;

        // Content
        var contentT = vpGO.transform.Find("Content");
        GameObject contentGO;
        if (contentT == null)
        {
            contentGO = new GameObject("Content");
            contentRT = contentGO.AddComponent<RectTransform>();  // RT before parenting
            contentGO.transform.SetParent(vpGO.transform, false);
        }
        else
        {
            contentGO = contentT.gameObject;
            contentRT = contentGO.GetComponent<RectTransform>();
            if (contentRT == null) contentRT = contentGO.AddComponent<RectTransform>();
        }
        contentRT.anchorMin = new Vector2(0, 1);
        contentRT.anchorMax = new Vector2(1, 1);
        contentRT.pivot = new Vector2(0.5f, 1);
        contentRT.sizeDelta = Vector2.zero;
        contentRT.anchoredPosition = Vector2.zero;
        sr.content = contentRT;

        var vlg = AddIfMissing<VerticalLayoutGroup>(contentGO);
        vlg.spacing = 2;
        vlg.childForceExpandWidth = true; vlg.childForceExpandHeight = false;
        vlg.childControlWidth = true; vlg.childControlHeight = true;
        vlg.padding = new RectOffset(2, 2, 2, 2);

        var csf = AddIfMissing<ContentSizeFitter>(contentGO);
        csf.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
    }

    static T AddIfMissing<T>(GameObject go) where T : Component
    {
        var c = go.GetComponent<T>();
        return c != null ? c : go.AddComponent<T>();
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  PlayerRow prefab creation
    // ═══════════════════════════════════════════════════════════════════════════

    static GameObject BuildPrefab()
    {
        var font = Font();

        var root = new GameObject("PlayerRow");
        root.AddComponent<RectTransform>();  // no parent yet — plain add is fine
        root.AddComponent<Image>().color = new Color(0.12f, 0.12f, 0.18f);

        var hlg = root.AddComponent<HorizontalLayoutGroup>();
        hlg.spacing = 3;
        hlg.padding = new RectOffset(3, 3, 3, 3);
        hlg.childForceExpandWidth = false;
        hlg.childForceExpandHeight = true;
        hlg.childControlWidth = true;
        hlg.childControlHeight = true;
        hlg.childAlignment = TextAnchor.MiddleLeft;

        var le = root.AddComponent<LayoutElement>();
        le.preferredHeight = 38;
        le.minHeight = 38;

        var rowUI = root.AddComponent<PlayerRowUI>();

        // Name InputField (flexible width)
        var nameGO = MakeInputField("NameField", root.transform, font, "Player name...");
        var nameLE = nameGO.AddComponent<LayoutElement>();
        nameLE.flexibleWidth = 1;
        nameLE.minWidth = 55;
        rowUI.nameField = nameGO.GetComponent<TMP_InputField>();

        // Alliance Dropdown
        var alliGO = MakeDropdown("AllianceDropdown", root.transform, font,
                                  new List<string> { "Alliance 1", "Alliance 2" });
        var alliLE = alliGO.AddComponent<LayoutElement>();
        alliLE.preferredWidth = 78; alliLE.minWidth = 78;
        rowUI.allianceDropdown = alliGO.GetComponent<TMP_Dropdown>();

        // Robot Dropdown
        var robGO = MakeDropdown("RobotDropdown", root.transform, font,
                                 new List<string> { "None" });
        var robLE = robGO.AddComponent<LayoutElement>();
        robLE.preferredWidth = 78; robLE.minWidth = 78;
        rowUI.robotDropdown = robGO.GetComponent<TMP_Dropdown>();

        // Remove Button
        var remGO = MakeButton("RemoveButton", root.transform, font, "✕",
                               new Color(0.45f, 0.12f, 0.12f));
        var remLE = remGO.AddComponent<LayoutElement>();
        remLE.preferredWidth = 30; remLE.minWidth = 30;
        rowUI.removeButton = remGO.GetComponent<Button>();

        // Save
        var prefab = PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
        Object.DestroyImmediate(root);
        AssetDatabase.Refresh();
        Debug.Log($"[WirePlayers] Prefab saved → {PrefabPath}");
        return prefab;
    }

    // ── Component factories ───────────────────────────────────────────────────

    static GameObject MakeInputField(string name, Transform parent, TMP_FontAsset font, string placeholder)
    {
        var go = new GameObject(name);
        go.AddComponent<RectTransform>();  // RT before parenting
        go.transform.SetParent(parent, false);
        go.AddComponent<Image>().color = new Color(0.15f, 0.15f, 0.22f);
        var fi = go.AddComponent<TMP_InputField>();

        // Text Area
        var ta = new GameObject("Text Area");
        var taRT = ta.AddComponent<RectTransform>();  // RT before parenting
        ta.transform.SetParent(go.transform, false);
        taRT.anchorMin = Vector2.zero; taRT.anchorMax = Vector2.one;
        taRT.offsetMin = new Vector2(5, 2); taRT.offsetMax = new Vector2(-5, -2);
        ta.AddComponent<RectMask2D>();

        // Placeholder — RT before parenting, then TMP
        var ph = new GameObject("Placeholder");
        var phRT = ph.AddComponent<RectTransform>();
        ph.transform.SetParent(ta.transform, false);
        phRT.anchorMin = Vector2.zero; phRT.anchorMax = Vector2.one;
        phRT.offsetMin = Vector2.zero; phRT.offsetMax = Vector2.zero;
        var phT = ph.AddComponent<TextMeshProUGUI>();
        phT.text = placeholder; phT.fontSize = 11;
        phT.color = new Color(0.45f, 0.45f, 0.55f);
        phT.fontStyle = FontStyles.Italic;
        phT.alignment = TextAlignmentOptions.MidlineLeft;
        if (font) phT.font = font;

        // Text — RT before parenting, then TMP
        var tx = new GameObject("Text");
        var txRT = tx.AddComponent<RectTransform>();
        tx.transform.SetParent(ta.transform, false);
        txRT.anchorMin = Vector2.zero; txRT.anchorMax = Vector2.one;
        txRT.offsetMin = Vector2.zero; txRT.offsetMax = Vector2.zero;
        var txT = tx.AddComponent<TextMeshProUGUI>();
        txT.fontSize = 11; txT.color = Color.white;
        txT.alignment = TextAlignmentOptions.MidlineLeft;
        if (font) txT.font = font;

        // Assign to TMP_InputField after all children exist
        fi.textViewport = taRT;
        fi.placeholder = phT;
        fi.textComponent = txT;

        return go;
    }

    static GameObject MakeDropdown(string name, Transform parent, TMP_FontAsset font, List<string> options)
    {
        var go = new GameObject(name);
        go.AddComponent<RectTransform>();  // RT before parenting
        go.transform.SetParent(parent, false);
        go.AddComponent<Image>().color = new Color(0.15f, 0.15f, 0.22f);
        var dd = go.AddComponent<TMP_Dropdown>();

        // Caption label
        var lbl = new GameObject("Label");
        var lblRT = lbl.AddComponent<RectTransform>();
        lbl.transform.SetParent(go.transform, false);
        lblRT.anchorMin = Vector2.zero; lblRT.anchorMax = Vector2.one;
        lblRT.offsetMin = new Vector2(6, 2); lblRT.offsetMax = new Vector2(-18, -2);
        var lblT = lbl.AddComponent<TextMeshProUGUI>();
        lblT.fontSize = 10; lblT.color = Color.white;
        lblT.alignment = TextAlignmentOptions.MidlineLeft;
        if (font) lblT.font = font;
        dd.captionText = lblT;

        // Arrow indicator
        var arrow = new GameObject("Arrow");
        var arrowRT = arrow.AddComponent<RectTransform>();
        arrow.transform.SetParent(go.transform, false);
        arrowRT.anchorMin = new Vector2(1, 0.5f); arrowRT.anchorMax = new Vector2(1, 0.5f);
        arrowRT.sizeDelta = new Vector2(13, 13);
        arrowRT.anchoredPosition = new Vector2(-9, 0);
        arrow.AddComponent<Image>().color = new Color(0.65f, 0.65f, 0.65f);

        // ── Template (the dropdown list shown on click) ─────────────────────
        var tmpl = new GameObject("Template");
        var tmplRT = tmpl.AddComponent<RectTransform>();
        tmpl.transform.SetParent(go.transform, false);
        tmpl.SetActive(false); // TMP_Dropdown requires template to be inactive
        tmplRT.anchorMin = new Vector2(0, 0); tmplRT.anchorMax = new Vector2(1, 0);
        tmplRT.pivot = new Vector2(0.5f, 1);
        tmplRT.anchoredPosition = new Vector2(0, 2);
        tmplRT.sizeDelta = new Vector2(0, 120);
        tmpl.AddComponent<Image>().color = new Color(0.11f, 0.11f, 0.18f);
        var sr = tmpl.AddComponent<ScrollRect>();
        sr.movementType = ScrollRect.MovementType.Clamped;
        dd.template = tmplRT;

        // Viewport
        var vp = new GameObject("Viewport");
        var vpRT = vp.AddComponent<RectTransform>();
        vp.transform.SetParent(tmpl.transform, false);
        vpRT.anchorMin = Vector2.zero; vpRT.anchorMax = Vector2.one;
        vpRT.offsetMin = Vector2.zero; vpRT.offsetMax = Vector2.zero;
        vpRT.pivot = new Vector2(0, 1);
        var vpM = vp.AddComponent<Mask>(); vpM.showMaskGraphic = false;
        vp.AddComponent<Image>().color = new Color(1, 1, 1, 0.01f);
        sr.viewport = vpRT;

        // Content
        var cont = new GameObject("Content");
        var contRT = cont.AddComponent<RectTransform>();
        cont.transform.SetParent(vp.transform, false);
        contRT.anchorMin = new Vector2(0, 1); contRT.anchorMax = new Vector2(1, 1);
        contRT.pivot = new Vector2(0.5f, 1);
        contRT.sizeDelta = new Vector2(0, 28);
        sr.content = contRT;

        // Item (template row — TMP_Dropdown clones this for each option)
        var item = new GameObject("Item");
        var itemRT = item.AddComponent<RectTransform>();
        item.transform.SetParent(cont.transform, false);
        itemRT.anchorMin = new Vector2(0, 0.5f); itemRT.anchorMax = new Vector2(1, 0.5f);
        itemRT.sizeDelta = new Vector2(0, 25);
        item.AddComponent<Image>().color = new Color(0.17f, 0.17f, 0.26f);
        var toggle = item.AddComponent<Toggle>();

        // Item Background (toggle target graphic — highlighted when selected)
        var bg = new GameObject("Item Background");
        var bgRT = bg.AddComponent<RectTransform>();
        bg.transform.SetParent(item.transform, false);
        bgRT.anchorMin = Vector2.zero; bgRT.anchorMax = Vector2.one;
        bgRT.offsetMin = Vector2.zero; bgRT.offsetMax = Vector2.zero;
        var bgImg = bg.AddComponent<Image>();
        bgImg.color = new Color(0.24f, 0.30f, 0.50f);
        toggle.targetGraphic = bgImg;

        // Item Checkmark (shown when item is selected)
        var ck = new GameObject("Item Checkmark");
        var ckRT = ck.AddComponent<RectTransform>();
        ck.transform.SetParent(item.transform, false);
        ckRT.anchorMin = new Vector2(0, 0.5f); ckRT.anchorMax = new Vector2(0, 0.5f);
        ckRT.sizeDelta = new Vector2(16, 16); ckRT.anchoredPosition = new Vector2(10, 0);
        var ckImg = ck.AddComponent<Image>();
        ckImg.color = new Color(0.35f, 0.90f, 0.35f);
        toggle.graphic = ckImg;

        // Item Label
        var iLbl = new GameObject("Item Label");
        var iLblRT = iLbl.AddComponent<RectTransform>();
        iLbl.transform.SetParent(item.transform, false);
        iLblRT.anchorMin = Vector2.zero; iLblRT.anchorMax = Vector2.one;
        iLblRT.offsetMin = new Vector2(22, 2); iLblRT.offsetMax = new Vector2(-4, -2);
        var iLblT = iLbl.AddComponent<TextMeshProUGUI>();
        iLblT.fontSize = 10; iLblT.color = Color.white;
        iLblT.alignment = TextAlignmentOptions.MidlineLeft;
        if (font) iLblT.font = font;
        dd.itemText = iLblT;

        dd.ClearOptions();
        dd.AddOptions(options);

        return go;
    }

    static GameObject MakeButton(string name, Transform parent, TMP_FontAsset font,
                                  string label, Color bg)
    {
        var go = new GameObject(name);
        go.AddComponent<RectTransform>();  // RT before parenting
        go.transform.SetParent(parent, false);
        go.AddComponent<Image>().color = bg;
        go.AddComponent<Button>();

        var lbl = new GameObject("Label");
        var lblRT = lbl.AddComponent<RectTransform>();  // RT before parenting
        lbl.transform.SetParent(go.transform, false);
        lblRT.anchorMin = Vector2.zero; lblRT.anchorMax = Vector2.one;
        lblRT.offsetMin = Vector2.zero; lblRT.offsetMax = Vector2.zero;
        var t = lbl.AddComponent<TextMeshProUGUI>();
        t.text = label; t.fontSize = 14; t.color = Color.white;
        t.alignment = TextAlignmentOptions.Center;
        if (font) t.font = font;

        return go;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    static RectTransform FullStretch(GameObject go)
    {
        var rt = go.GetComponent<RectTransform>();
        if (rt == null) rt = go.AddComponent<RectTransform>();
        rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
        rt.offsetMin = Vector2.zero; rt.offsetMax = Vector2.zero;
        return rt;
    }

    static void SetPrivateField(object target, System.Type type, string fieldName, object value)
    {
        var fi = type.GetField(fieldName,
                               BindingFlags.NonPublic | BindingFlags.Instance);
        if (fi != null) fi.SetValue(target, value);
        else Debug.LogWarning($"[WirePlayers] Field '{fieldName}' not found on {type.Name}");
    }

    static TMP_FontAsset Font()
    {
        var f = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(
            "Assets/TextMesh Pro/Resources/Fonts & Materials/LiberationSans SDF.asset");
        return f != null ? f : TMP_Settings.defaultFontAsset;
    }
}
