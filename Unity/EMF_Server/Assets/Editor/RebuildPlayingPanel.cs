// RebuildPlayingPanel.cs — menu: Thundergeddon / 9 Rebuild Playing Panel
// Layout (top→bottom):
//   Header (58px)  — THUNDERGEDDON title + timer
//   Body (fills)   — VLG:
//     TeamPointsBar (44px)
//     EventLog      (82px)
//     Columns HLG   (flex)
//       LeftColumn  (flex 1.5)  — TeamRosterPanel in scroll
//       RightColumn (130px)     — 3 capture-point indicators
//   Footer (54px)  — END GAME button

using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.UI;
using UnityEngine.SceneManagement;
using TMPro;

public static class RebuildPlayingPanel
{
    // ── Palette ───────────────────────────────────────────────────────────────
    static readonly Color C_BG     = new Color(0.051f, 0.051f, 0.051f);
    static readonly Color C_PANEL  = new Color(0.102f, 0.102f, 0.102f);
    static readonly Color C_TEXT   = new Color(0.910f, 0.910f, 0.910f);
    static readonly Color C_DIM    = new Color(0.400f, 0.400f, 0.400f);
    static readonly Color C_YELLOW = new Color(0.941f, 0.753f, 0.251f);
    static readonly Color C_CYAN   = new Color(0.000f, 0.835f, 1.000f);
    static readonly Color C_BLUE   = new Color(0.290f, 0.620f, 1.000f);
    static readonly Color C_RED    = new Color(1.000f, 0.420f, 0.210f);
    static readonly Color C_DKRED  = new Color(0.550f, 0.100f, 0.100f);
    static readonly Color C_DKBLUE = new Color(0.100f, 0.240f, 0.440f);

    static TMP_FontAsset _font;

    [MenuItem("Thundergeddon/9 Rebuild Playing Panel")]
    public static void Execute()
    {
        _font = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(
            "Assets/TextMesh Pro/Resources/Fonts & Materials/LiberationSans SDF.asset");

        // GameObject.Find skips inactive objects, so search via the Canvas transform instead.
        var canvas = GameObject.Find("Canvas");
        if (canvas == null) { Debug.LogError("[RebuildPlaying] Canvas not found."); return; }
        var ppTransform = canvas.transform.Find("PlayingPanel");
        if (ppTransform == null) { Debug.LogError("[RebuildPlaying] PlayingPanel not found under Canvas."); return; }
        var pp = ppTransform.gameObject;

        // ── 1. Remove all old children ────────────────────────────────────────
        for (int i = pp.transform.childCount - 1; i >= 0; i--)
            Object.DestroyImmediate(pp.transform.GetChild(i).gameObject);

        // Remove old presenter components that are no longer needed
        var pim = pp.GetComponent<PlayerInputMonitor>();
        if (pim != null) Object.DestroyImmediate(pim);
        var rhp = pp.GetComponent<RobotHpPanel>();
        if (rhp != null) Object.DestroyImmediate(rhp);
        var oldTrp = pp.GetComponent<TeamRosterPanel>();
        if (oldTrp != null) Object.DestroyImmediate(oldTrp);

        // ── 2. Panel background ───────────────────────────────────────────────
        var bgImg = pp.GetComponent<Image>();
        if (bgImg == null) bgImg = pp.AddComponent<Image>();
        bgImg.color = C_BG;

        // ── 3. Header ─────────────────────────────────────────────────────────
        var header = MakeRect(pp.transform, "Header");
        Anchor(header, 0, 1, 1, 1, 0, -58, 0, 0);
        header.AddComponent<Image>().color = C_PANEL;

        var titleLbl = MakeTmp(header.transform, "TitleLabel", "THUNDERGEDDON",
                                C_YELLOW, 18f, TextAlignmentOptions.MidlineLeft, bold: true);
        titleLbl.characterSpacing = 2f;
        Anchor(titleLbl.gameObject, 0, 0, 0.55f, 1, 18, 0, 0, 0);

        var timerLbl = MakeTmp(header.transform, "TimerLabel", "3:00",
                                C_TEXT, 32f, TextAlignmentOptions.MidlineRight, bold: true);
        timerLbl.fontStyle = FontStyles.Bold | FontStyles.SmallCaps;
        Anchor(timerLbl.gameObject, 0.55f, 0, 1, 1, 0, 0, -18, 0);

        // ── 4. Footer ─────────────────────────────────────────────────────────
        var footer = MakeRect(pp.transform, "Footer");
        Anchor(footer, 0, 0, 1, 0, 0, 0, 0, 54);
        footer.AddComponent<Image>().color = C_PANEL;

        var sepGo = MakeRect(footer.transform, "Sep");
        Anchor(sepGo, 0, 1, 1, 1, 0, -1, 0, 0);
        sepGo.AddComponent<Image>().color = new Color(0.165f, 0.165f, 0.165f);

        var pauseBtn   = MakeButton(footer.transform, "PauseButton",   "PAUSE",    C_DKBLUE, C_TEXT, 13f);
        Anchor(pauseBtn, 0.25f, 0, 0.25f, 1, -90, 7, 90, -7);

        var endGameBtn = MakeButton(footer.transform, "EndGameButton", "END GAME", C_DKRED, C_TEXT, 13f);
        Anchor(endGameBtn, 0.75f, 0, 0.75f, 1, -90, 7, 90, -7);

        // ── 5. Body (between header and footer) ───────────────────────────────
        var body = MakeRect(pp.transform, "Body");
        Anchor(body, 0, 0, 1, 1, 8, 58, -8, -62);

        var bodyVLG = body.AddComponent<VerticalLayoutGroup>();
        bodyVLG.spacing             = 6f;
        bodyVLG.padding             = new RectOffset(0, 0, 6, 0);
        bodyVLG.childForceExpandWidth  = true;
        bodyVLG.childForceExpandHeight = false;
        bodyVLG.childControlWidth      = true;
        bodyVLG.childControlHeight     = true;
        bodyVLG.childAlignment         = TextAnchor.UpperLeft;

        // ── 6. Team points bar ────────────────────────────────────────────────
        var tpBar = MakeRect(body.transform, "TeamPointsBar");
        tpBar.AddComponent<Image>().color = C_PANEL;
        tpBar.AddComponent<LayoutElement>().preferredHeight = 44f;

        // Label row
        var tpLabels = MakeRect(tpBar.transform, "Labels");
        Anchor(tpLabels, 0, 0.5f, 1, 1, 10, 0, -10, 0);
        var tpLabelHLG = tpLabels.AddComponent<HorizontalLayoutGroup>();
        tpLabelHLG.childForceExpandWidth  = true;
        tpLabelHLG.childForceExpandHeight = true;
        tpLabelHLG.childControlWidth      = true;
        tpLabelHLG.childControlHeight     = true;

        var tpLabel0 = MakeTmp(tpLabels.transform, "Label0", "ALLIANCE 1 — 0 PTS",
                                C_BLUE, 10f, TextAlignmentOptions.MidlineLeft, bold: true);
        var tpLabel1 = MakeTmp(tpLabels.transform, "Label1", "0 PTS — ALLIANCE 2",
                                C_RED,  10f, TextAlignmentOptions.MidlineRight, bold: true);

        // Track
        var tpTrack = MakeRect(tpBar.transform, "Track");
        Anchor(tpTrack, 0, 0, 1, 0.5f, 10, 4, -10, 0);
        tpTrack.AddComponent<Image>().color = new Color(0.067f, 0.067f, 0.067f);

        var fill0 = MakeRect(tpTrack.transform, "Fill0");
        fill0.AddComponent<Image>().color = C_BLUE;
        Anchor(fill0, 0, 0, 0, 1, 0, 0, 0, 0); // starts at 0 width

        var fill1 = MakeRect(tpTrack.transform, "Fill1");
        fill1.AddComponent<Image>().color = C_RED;
        Anchor(fill1, 1, 0, 1, 1, 0, 0, 0, 0); // starts at 0 width from right

        var centreLine = MakeRect(tpTrack.transform, "Centre");
        Anchor(centreLine, 0.5f, 0, 0.5f, 1, -1, 0, 1, 0);
        centreLine.AddComponent<Image>().color = new Color(0.25f, 0.25f, 0.25f);

        // Wire TeamPointsBarUI
        var tpBarUI = pp.GetComponent<TeamPointsBarUI>();
        if (tpBarUI == null) tpBarUI = pp.AddComponent<TeamPointsBarUI>();
        {
            var so = new SerializedObject(tpBarUI);
            SetProp(so, "fill0",  fill0.GetComponent<RectTransform>());
            SetProp(so, "fill1",  fill1.GetComponent<RectTransform>());
            SetProp(so, "label0", tpLabel0);
            SetProp(so, "label1", tpLabel1);
            so.ApplyModifiedProperties();
        }

        // ── 7. Event log ──────────────────────────────────────────────────────
        var evtGo = MakeRect(body.transform, "EventLog");
        evtGo.AddComponent<Image>().color = C_PANEL;
        evtGo.AddComponent<LayoutElement>().preferredHeight = 82f;

        var evtContainer = MakeRect(evtGo.transform, "Container");
        Anchor(evtContainer, 0, 0, 1, 1, 10, 6, -10, -6);
        var evtVLG = evtContainer.AddComponent<VerticalLayoutGroup>();
        evtVLG.spacing             = 2f;
        evtVLG.childForceExpandWidth  = true;
        evtVLG.childForceExpandHeight = false;
        evtVLG.childControlWidth      = true;
        evtVLG.childControlHeight     = true;
        evtVLG.childAlignment         = TextAnchor.UpperLeft;

        var evtPanel = pp.GetComponent<EventLogPanelUI>();
        if (evtPanel == null) evtPanel = pp.AddComponent<EventLogPanelUI>();
        {
            var so = new SerializedObject(evtPanel);
            SetProp(so, "container", evtContainer.GetComponent<RectTransform>());
            so.ApplyModifiedProperties();
        }

        // ── 8. Columns area ───────────────────────────────────────────────────
        var columns = MakeRect(body.transform, "Columns");
        columns.AddComponent<LayoutElement>().flexibleHeight = 1f;

        var colHLG = columns.AddComponent<HorizontalLayoutGroup>();
        colHLG.spacing             = 8f;
        colHLG.childForceExpandWidth  = false;
        colHLG.childForceExpandHeight = true;
        colHLG.childControlWidth      = true;
        colHLG.childControlHeight     = true;
        colHLG.childAlignment         = TextAnchor.UpperLeft;

        // ── 8a. Left column — team roster ─────────────────────────────────────
        var leftCol = MakeRect(columns.transform, "LeftColumn");
        var leftLE = leftCol.AddComponent<LayoutElement>();
        leftLE.flexibleWidth  = 1f;
        leftLE.flexibleHeight = 1f;

        var leftVLG = leftCol.AddComponent<VerticalLayoutGroup>();
        leftVLG.spacing             = 4f;
        leftVLG.childForceExpandWidth  = true;
        leftVLG.childForceExpandHeight = false;
        leftVLG.childControlWidth      = true;
        leftVLG.childControlHeight     = true;
        leftVLG.childAlignment         = TextAnchor.UpperLeft;

        // Section label
        var rosterLbl = MakeTmp(leftCol.transform, "LblRoster", "ROBOTS & PLAYERS",
                                 C_CYAN, 10f, TextAlignmentOptions.MidlineLeft, bold: true);
        rosterLbl.characterSpacing = 1.5f;
        rosterLbl.gameObject.AddComponent<LayoutElement>().preferredHeight = 22f;

        // Controls row (PING + DAMAGE 10%)
        var ctrlRow = MakeRect(leftCol.transform, "ControlsRow");
        ctrlRow.AddComponent<LayoutElement>().preferredHeight = 36f;

        var ctrlHLG = ctrlRow.AddComponent<HorizontalLayoutGroup>();
        ctrlHLG.spacing                = 6f;
        ctrlHLG.childForceExpandWidth  = true;
        ctrlHLG.childForceExpandHeight = false; // prevents HLG reporting flexibleHeight=1 to parent VLG
        ctrlHLG.childControlWidth      = true;
        ctrlHLG.childControlHeight     = true;

        var pingBtn  = MakeButton(ctrlRow.transform, "PingButton",   "PING",       C_DKBLUE, C_TEXT, 12f);
        pingBtn.AddComponent<LayoutElement>().preferredHeight = 34f;
        var dmgBtn   = MakeButton(ctrlRow.transform, "DamageButton", "DAMAGE 10%", C_DKRED,  C_TEXT, 12f);
        dmgBtn.AddComponent<LayoutElement>().preferredHeight = 34f;
        var healBtn  = MakeButton(ctrlRow.transform, "HealButton",   "HEAL",       new Color(0.10f, 0.35f, 0.10f), C_TEXT, 12f);
        healBtn.AddComponent<LayoutElement>().preferredHeight = 34f;
        var pingResult = MakeTmp(pp.transform, "PingResult", "—", C_DIM, 10f, TextAlignmentOptions.MidlineLeft);
        pingResult.gameObject.SetActive(false); // hidden label used for wiring only

        // Roster scroll
        RectTransform rosterContent;
        var rosterScroll = CreateScrollCard(leftCol.transform, "RosterScroll", out rosterContent);
        rosterScroll.AddComponent<LayoutElement>().flexibleHeight = 1f;
        rosterScroll.GetComponent<Image>().color = C_PANEL;

        // Remove stale TeamRosterPanel if still present, then add RobotListPanel
        var stale = pp.GetComponent<TeamRosterPanel>();
        if (stale != null) Object.DestroyImmediate(stale);

        var rlp = pp.GetComponent<RobotListPanel>();
        if (rlp == null) rlp = pp.AddComponent<RobotListPanel>();
        {
            var so = new SerializedObject(rlp);
            SetProp(so, "rowContainer", rosterContent);
            so.ApplyModifiedProperties();
        }

        // ── 8b. Right column — capture points ─────────────────────────────────
        var rightCol = MakeRect(columns.transform, "RightColumn");
        var rightLE  = rightCol.AddComponent<LayoutElement>();
        rightLE.preferredWidth = 130f;
        rightLE.flexibleHeight = 1f;
        rightCol.AddComponent<Image>().color = C_PANEL;

        rightLE.flexibleWidth = 0f; // prevent VLG's childForceExpandWidth from stealing flex space

        var rightVLG = rightCol.AddComponent<VerticalLayoutGroup>();
        rightVLG.spacing             = 8f;
        rightVLG.padding             = new RectOffset(8, 8, 8, 8);
        rightVLG.childForceExpandWidth  = false; // prevent reporting flexibleWidth=1 to parent HLG
        rightVLG.childForceExpandHeight = false;
        rightVLG.childControlWidth      = true;
        rightVLG.childControlHeight     = true;
        rightVLG.childAlignment         = TextAnchor.UpperCenter;

        // Section label
        var cpLbl = MakeTmp(rightCol.transform, "LblCapture", "CAPTURE POINTS",
                             C_CYAN, 9f, TextAlignmentOptions.Center, bold: true);
        cpLbl.characterSpacing = 1f;
        cpLbl.gameObject.AddComponent<LayoutElement>().preferredHeight = 20f;

        // Three capture point blocks: North, Centre, South
        Image cpCircle0 = MakeCpBlock(rightCol.transform, "CpNorth",  "NORTH");
        Image cpCircle1 = MakeCpBlock(rightCol.transform, "CpCentre", "CENTRE");
        Image cpCircle2 = MakeCpBlock(rightCol.transform, "CpSouth",  "SOUTH");

        System.Type cpType = null;
        foreach (var asm in System.AppDomain.CurrentDomain.GetAssemblies())
        {
            cpType = asm.GetType("CapturePointsPanelUI");
            if (cpType != null) break;
        }
        if (cpType != null)
        {
            var cpUI = pp.GetComponent(cpType);
            if (cpUI == null) cpUI = pp.AddComponent(cpType);
            var so = new SerializedObject(cpUI);
            SetProp(so, "circle0", cpCircle0);
            SetProp(so, "circle1", cpCircle1);
            SetProp(so, "circle2", cpCircle2);
            so.ApplyModifiedProperties();
        }
        else Debug.LogWarning("[RebuildPlaying] CapturePointsPanelUI type not found.");

        // ── 9. Wire existing components ───────────────────────────────────────

        var pgb = pp.GetComponent<PauseGameButton>();
        if (pgb == null) pgb = pp.AddComponent<PauseGameButton>();
        {
            var so = new SerializedObject(pgb);
            SetProp(so, "button", pauseBtn.GetComponent<Button>());
            SetProp(so, "label",  pauseBtn.GetComponentInChildren<TextMeshProUGUI>());
            so.ApplyModifiedProperties();
        }

        var mtd = pp.GetComponent<MatchTimerDisplay>();
        if (mtd != null)
        {
            var so = new SerializedObject(mtd);
            SetProp(so, "timerLabel", timerLbl);
            so.ApplyModifiedProperties();
        }

        var gpp = pp.GetComponent<GamePanelPresenter>();
        if (gpp != null)
        {
            var so = new SerializedObject(gpp);
            SetProp(so, "endGameButton", endGameBtn.GetComponent<Button>());
            so.ApplyModifiedProperties();
        }

        var sc = pp.GetComponent<ShootingController>();
        if (sc != null)
        {
            var so = new SerializedObject(sc);
            SetProp(so, "shootButton",   null);
            SetProp(so, "cooldownLabel", null);
            so.ApplyModifiedProperties();
        }

        var hb = pp.GetComponent<HealButton>();
        if (hb == null) hb = pp.AddComponent<HealButton>();
        {
            var so = new SerializedObject(hb);
            SetProp(so, "healButton",     healBtn.GetComponent<Button>());
            SetProp(so, "robotListPanel", rlp);
            so.ApplyModifiedProperties();
        }

        var tdb = pp.GetComponent<TestDamageButton>();
        if (tdb == null) tdb = pp.AddComponent<TestDamageButton>();
        {
            var so = new SerializedObject(tdb);
            SetProp(so, "damageButton",   dmgBtn.GetComponent<Button>());
            SetProp(so, "robotListPanel", rlp);
            so.ApplyModifiedProperties();
        }

        var rpb = pp.GetComponent<RobotPingButton>();
        if (rpb == null) rpb = pp.AddComponent<RobotPingButton>();
        {
            var so = new SerializedObject(rpb);
            SetProp(so, "pingButton",     pingBtn.GetComponent<Button>());
            SetProp(so, "resultLabel",    pingResult);
            SetProp(so, "robotListPanel", rlp);
            so.ApplyModifiedProperties();
        }

        var rsp = pp.GetComponent<RobotSelectionPanel>();
        if (rsp != null)
        {
            var so = new SerializedObject(rsp);
            SetProp(so, "prevButton",    null);
            SetProp(so, "nextButton",    null);
            SetProp(so, "clearButton",   null);
            SetProp(so, "nameLabel",     null);
            SetProp(so, "ipLabel",       null);
            SetProp(so, "playerLabel",   null);
            SetProp(so, "allianceLabel", null);
            SetProp(so, "clientLabel",   null);
            so.ApplyModifiedProperties();
        }

        Canvas.ForceUpdateCanvases();
        LayoutRebuilder.ForceRebuildLayoutImmediate(pp.GetComponent<RectTransform>());

        EditorUtility.SetDirty(pp);
        EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
        Debug.Log("[RebuildPlaying] Done.");
    }

    // ── Capture point block helper ────────────────────────────────────────────

    static Image MakeCpBlock(Transform parent, string name, string label)
    {
        var block = MakeRect(parent, name);
        block.AddComponent<LayoutElement>().preferredHeight = 70f;

        var blockVLG = block.AddComponent<VerticalLayoutGroup>();
        blockVLG.spacing             = 4f;
        blockVLG.childForceExpandWidth  = true;
        blockVLG.childForceExpandHeight = false;
        blockVLG.childControlWidth      = true;
        blockVLG.childControlHeight     = true;
        blockVLG.childAlignment         = TextAnchor.UpperCenter;

        // Circle (square indicator)
        var circleGo = MakeRect(block.transform, "Circle");
        var circleLE = circleGo.AddComponent<LayoutElement>();
        circleLE.preferredHeight = 48f;
        circleLE.flexibleWidth   = 0f;
        var img = circleGo.AddComponent<Image>();
        img.color = new Color(0.20f, 0.20f, 0.20f);

        // Label
        var lbl = MakeTmp(block.transform, "Label", label,
                           new Color(0.40f, 0.40f, 0.40f), 9f, TextAlignmentOptions.Center, bold: true);
        lbl.characterSpacing = 1f;
        lbl.gameObject.AddComponent<LayoutElement>().preferredHeight = 16f;

        return img;
    }

    // Null-safe SerializedObject property setter — logs a warning if property not found.
    static void SetProp(SerializedObject so, string propName, UnityEngine.Object value)
    {
        var prop = so.FindProperty(propName);
        if (prop == null)
            Debug.LogWarning($"[RebuildPlaying] Property '{propName}' not found on {so.targetObject?.GetType().Name}");
        else
            prop.objectReferenceValue = value;
    }

    // ── Generic helpers ───────────────────────────────────────────────────────

    static GameObject MakeRect(Transform parent, string name)
    {
        var go = new GameObject(name);
        go.AddComponent<RectTransform>();
        go.transform.SetParent(parent, false);
        return go;
    }

    static void Anchor(GameObject go, float axMin, float ayMin, float axMax, float ayMax,
                       float oxMin, float oyMin, float oxMax, float oyMax)
    {
        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = new Vector2(axMin, ayMin);
        rt.anchorMax = new Vector2(axMax, ayMax);
        rt.offsetMin = new Vector2(oxMin, oyMin);
        rt.offsetMax = new Vector2(oxMax, oyMax);
    }

    static TextMeshProUGUI MakeTmp(Transform parent, string name, string text,
                                    Color color, float size,
                                    TextAlignmentOptions align, bool bold = false)
    {
        var go = new GameObject(name);
        var rt = go.AddComponent<RectTransform>();
        go.transform.SetParent(parent, false);
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = Vector2.zero;
        rt.offsetMax = Vector2.zero;

        var tmp = go.AddComponent<TextMeshProUGUI>();
        tmp.text      = text;
        tmp.font      = _font;
        tmp.fontSize  = size;
        tmp.color     = color;
        tmp.alignment = align;
        if (bold) tmp.fontStyle = FontStyles.Bold;
        return tmp;
    }

    // Button with full-fill rect (caller anchors after if needed).
    static GameObject MakeButton(Transform parent, string name, string label,
                                  Color bgColor, Color textColor, float fontSize)
    {
        var go = new GameObject(name);
        go.AddComponent<RectTransform>();
        go.transform.SetParent(parent, false);
        go.AddComponent<Image>().color = bgColor;
        go.AddComponent<Button>();
        MakeTmp(go.transform, "Label", label, textColor, fontSize, TextAlignmentOptions.Center, bold: true);
        return go;
    }

    static GameObject CreateScrollCard(Transform parent, string name, out RectTransform content)
    {
        var go = new GameObject(name);
        go.AddComponent<RectTransform>();
        go.transform.SetParent(parent, false);
        go.AddComponent<Image>().color = C_PANEL;

        var vp = new GameObject("Viewport");
        vp.AddComponent<RectTransform>();
        vp.transform.SetParent(go.transform, false);
        var vpRT = vp.GetComponent<RectTransform>();
        vpRT.anchorMin = Vector2.zero;
        vpRT.anchorMax = Vector2.one;
        vpRT.offsetMin = Vector2.zero;
        vpRT.offsetMax = Vector2.zero;
        vp.AddComponent<Image>().color = Color.clear;
        vp.AddComponent<Mask>().showMaskGraphic = false;

        var c = new GameObject("Content");
        c.AddComponent<RectTransform>();
        c.transform.SetParent(vp.transform, false);
        var cRT = c.GetComponent<RectTransform>();
        cRT.anchorMin = new Vector2(0f, 1f);
        cRT.anchorMax = new Vector2(1f, 1f);
        cRT.pivot     = new Vector2(0.5f, 1f);
        cRT.sizeDelta = Vector2.zero;
        cRT.anchoredPosition = Vector2.zero;

        var vlg = c.AddComponent<VerticalLayoutGroup>();
        vlg.spacing             = 2f;
        vlg.padding             = new RectOffset(4, 4, 4, 4);
        vlg.childForceExpandWidth  = true;
        vlg.childForceExpandHeight = false;
        vlg.childControlWidth      = true;
        vlg.childControlHeight     = true;
        c.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;

        var scroll = go.AddComponent<ScrollRect>();
        scroll.horizontal   = false;
        scroll.vertical     = true;
        scroll.movementType = ScrollRect.MovementType.Clamped;
        scroll.viewport = vpRT;
        scroll.content  = cRT;

        content = cRT;
        return go;
    }
}
