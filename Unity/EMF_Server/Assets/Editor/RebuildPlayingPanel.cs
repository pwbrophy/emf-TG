// RebuildPlayingPanel.cs — menu: Thundergeddon / 9 Rebuild Playing Panel
// Layout (top→bottom):
//   Header (58px)
//   Body (VLG):
//     TopArea (HLG, 130px)
//       VpAndLog (flex VLG)  — TeamPointsBar + EventLog
//       CaptureColumn (90px) — Capture-point buttons
//     Columns (HLG, flex)
//       LeftColumn (flex)    — label + 2-row ActionButtonsPanel + RosterScroll
//       GameSettingsColumn (210px)
//       ShotTimingColumn (210px)
//   Footer (54px)

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
    static readonly Color C_DKGRN  = new Color(0.100f, 0.350f, 0.100f);
    static readonly Color C_DKPUR  = new Color(0.150f, 0.150f, 0.300f);
    static readonly Color C_DKBLU2 = new Color(0.100f, 0.180f, 0.280f);
    static readonly Color C_CAPNTR = new Color(0.200f, 0.200f, 0.200f);

    static TMP_FontAsset _font;

    [MenuItem("Thundergeddon/9 Rebuild Playing Panel")]
    public static void Execute()
    {
        _font = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(
            "Assets/TextMesh Pro/Resources/Fonts & Materials/LiberationSans SDF.asset");

        var canvas = GameObject.Find("Canvas");
        if (canvas == null) { Debug.LogError("[RebuildPlaying] Canvas not found."); return; }
        var ppTransform = canvas.transform.Find("PlayingPanel");
        if (ppTransform == null) { Debug.LogError("[RebuildPlaying] PlayingPanel not found under Canvas."); return; }
        var pp = ppTransform.gameObject;

        // ── 1. Remove all old children ────────────────────────────────────────
        for (int i = pp.transform.childCount - 1; i >= 0; i--)
            Object.DestroyImmediate(pp.transform.GetChild(i).gameObject);

        // Remove old presenter components that are no longer needed
        RemoveIfPresent<PlayerInputMonitor>(pp);
        RemoveIfPresent<RobotHpPanel>(pp);
        RemoveIfPresent<TeamRosterPanel>(pp);
        RemoveIfPresent<HealButton>(pp);
        RemoveIfPresent<TestDamageButton>(pp);
        RemoveIfPresent<RobotPingButton>(pp);
        RemoveIfPresent<ToggleCameraButton>(pp);
        RemoveIfPresent<FlipVideoButtons>(pp);

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

        // ── 5. Body ───────────────────────────────────────────────────────────
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

        // ── 6. TopArea (TeamPointsBar + EventLog + CaptureColumn) ─────────────
        var topArea = MakeRect(body.transform, "TopArea");
        topArea.AddComponent<LayoutElement>().preferredHeight = 130f;

        var topHLG = topArea.AddComponent<HorizontalLayoutGroup>();
        topHLG.spacing             = 6f;
        topHLG.childForceExpandWidth  = false;
        topHLG.childForceExpandHeight = true;
        topHLG.childControlWidth      = true;
        topHLG.childControlHeight     = true;
        topHLG.childAlignment         = TextAnchor.UpperLeft;

        // ── 6a. VpAndLog — TeamPointsBar + EventLog ───────────────────────────
        var vpAndLog = MakeRect(topArea.transform, "VpAndLog");
        vpAndLog.AddComponent<LayoutElement>().flexibleWidth = 1f;

        var vpVLG = vpAndLog.AddComponent<VerticalLayoutGroup>();
        vpVLG.spacing             = 6f;
        vpVLG.childForceExpandWidth  = true;
        vpVLG.childForceExpandHeight = false;
        vpVLG.childControlWidth      = true;
        vpVLG.childControlHeight     = true;
        vpVLG.childAlignment         = TextAnchor.UpperLeft;

        // Team points bar
        var tpBar = MakeRect(vpAndLog.transform, "TeamPointsBar");
        tpBar.AddComponent<Image>().color = C_PANEL;
        tpBar.AddComponent<LayoutElement>().preferredHeight = 44f;

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

        var tpTrack = MakeRect(tpBar.transform, "Track");
        Anchor(tpTrack, 0, 0, 1, 0.5f, 10, 4, -10, 0);
        tpTrack.AddComponent<Image>().color = new Color(0.067f, 0.067f, 0.067f);

        var fill0 = MakeRect(tpTrack.transform, "Fill0");
        fill0.AddComponent<Image>().color = C_BLUE;
        Anchor(fill0, 0, 0, 0, 1, 0, 0, 0, 0);

        var fill1 = MakeRect(tpTrack.transform, "Fill1");
        fill1.AddComponent<Image>().color = C_RED;
        Anchor(fill1, 1, 0, 1, 1, 0, 0, 0, 0);

        var centreLine = MakeRect(tpTrack.transform, "Centre");
        Anchor(centreLine, 0.5f, 0, 0.5f, 1, -1, 0, 1, 0);
        centreLine.AddComponent<Image>().color = new Color(0.25f, 0.25f, 0.25f);

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

        // Event log
        var evtGo = MakeRect(vpAndLog.transform, "EventLog");
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

        // ── 6b. CaptureColumn ─────────────────────────────────────────────────
        var capCol = MakeRect(topArea.transform, "CaptureColumn");
        capCol.AddComponent<Image>().color = C_PANEL;
        var capColLE = capCol.AddComponent<LayoutElement>();
        capColLE.preferredWidth = 90f;
        capColLE.flexibleWidth  = 0f;
        capColLE.flexibleHeight = 1f;

        var capColVLG = capCol.AddComponent<VerticalLayoutGroup>();
        capColVLG.spacing             = 4f;
        capColVLG.padding             = new RectOffset(6, 6, 6, 6);
        capColVLG.childForceExpandWidth  = true;
        capColVLG.childForceExpandHeight = false;
        capColVLG.childControlWidth      = true;
        capColVLG.childControlHeight     = true;
        capColVLG.childAlignment         = TextAnchor.UpperCenter;

        var capLbl = MakeTmp(capCol.transform, "LblCapture", "CAPTURE POINTS",
                              C_CYAN, 8f, TextAlignmentOptions.Center, bold: true);
        capLbl.characterSpacing = 1f;
        capLbl.gameObject.AddComponent<LayoutElement>().preferredHeight = 14f;

        var capNorthBtn  = MakeCaptureButton(capCol.transform, "CaptureNorthBtn",  "NORTH");
        var capCentreBtn = MakeCaptureButton(capCol.transform, "CaptureCentreBtn", "CENTRE");
        var capSouthBtn  = MakeCaptureButton(capCol.transform, "CaptureSouthBtn",  "SOUTH");

        // ── 7. Columns area ───────────────────────────────────────────────────
        var columns = MakeRect(body.transform, "Columns");
        columns.AddComponent<LayoutElement>().flexibleHeight = 1f;

        var colHLG = columns.AddComponent<HorizontalLayoutGroup>();
        colHLG.spacing             = 8f;
        colHLG.childForceExpandWidth  = false;
        colHLG.childForceExpandHeight = true;
        colHLG.childControlWidth      = true;
        colHLG.childControlHeight     = true;
        colHLG.childAlignment         = TextAnchor.UpperLeft;

        // ── 7a. Left column — robot roster + action buttons ───────────────────
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

        var rosterLbl = MakeTmp(leftCol.transform, "LblRoster", "ROBOTS & PLAYERS",
                                 C_CYAN, 10f, TextAlignmentOptions.MidlineLeft, bold: true);
        rosterLbl.characterSpacing = 1.5f;
        rosterLbl.gameObject.AddComponent<LayoutElement>().preferredHeight = 22f;

        // Action buttons panel — 2 rows
        var actionPanel = MakeRect(leftCol.transform, "ActionButtonsPanel");
        actionPanel.AddComponent<LayoutElement>().preferredHeight = 76f;
        var apVLG = actionPanel.AddComponent<VerticalLayoutGroup>();
        apVLG.spacing             = 4f;
        apVLG.childForceExpandWidth  = true;
        apVLG.childForceExpandHeight = false;
        apVLG.childControlWidth      = true;
        apVLG.childControlHeight     = true;

        // Row 1: Damage group — Hit Front | Hit Side | Hit Rear | Heal
        var dmgRow = MakeRect(actionPanel.transform, "DamageRow");
        dmgRow.AddComponent<LayoutElement>().preferredHeight = 34f;
        var dmgHLG = dmgRow.AddComponent<HorizontalLayoutGroup>();
        dmgHLG.spacing             = 4f;
        dmgHLG.childForceExpandWidth  = true;
        dmgHLG.childForceExpandHeight = false;
        dmgHLG.childControlWidth      = true;
        dmgHLG.childControlHeight     = true;

        var hitFrontBtn = MakeButton(dmgRow.transform, "HitFrontBtn",  "HIT F",     C_DKRED,  C_TEXT, 11f);
        var hitSideBtn  = MakeButton(dmgRow.transform, "HitSideBtn",   "HIT S",     C_DKRED,  C_TEXT, 11f);
        var hitRearBtn  = MakeButton(dmgRow.transform, "HitRearBtn",   "HIT R×3",   new Color(0.7f, 0.05f, 0.05f), C_TEXT, 11f);
        var healBtn     = MakeButton(dmgRow.transform, "HealBtn",      "HEAL",      C_DKGRN,  C_TEXT, 11f);

        foreach (var b in new[] { hitFrontBtn, hitSideBtn, hitRearBtn, healBtn })
            b.AddComponent<LayoutElement>().preferredHeight = 34f;

        // Row 2: Drive + Camera groups
        var drvCamRow = MakeRect(actionPanel.transform, "DriveCamRow");
        drvCamRow.AddComponent<LayoutElement>().preferredHeight = 34f;
        var drvCamHLG = drvCamRow.AddComponent<HorizontalLayoutGroup>();
        drvCamHLG.spacing             = 4f;
        drvCamHLG.childForceExpandWidth  = true;
        drvCamHLG.childForceExpandHeight = false;
        drvCamHLG.childControlWidth      = true;
        drvCamHLG.childControlHeight     = true;

        var revThBtn  = MakeButton(drvCamRow.transform, "RevThrottleBtn", "REV THROTTLE: OFF", C_DKBLU2, C_TEXT, 9f);
        var revStBtn  = MakeButton(drvCamRow.transform, "RevSteerBtn",    "REV STEER: OFF",    C_DKBLU2, C_TEXT, 9f);
        var revTuBtn  = MakeButton(drvCamRow.transform, "RevTurretBtn",   "REV TURRET: OFF",   C_DKBLU2, C_TEXT, 9f);
        var flipHBtn  = MakeButton(drvCamRow.transform, "FlipHBtn",       "FLIP H",            C_DKPUR,  C_TEXT, 10f);
        var flipVBtn  = MakeButton(drvCamRow.transform, "FlipVBtn",       "FLIP V",            C_DKPUR,  C_TEXT, 10f);
        var camTogBtn = MakeButton(drvCamRow.transform, "CamToggleBtn",   "CAM ON/OFF",        new Color(0.10f, 0.25f, 0.35f), C_TEXT, 10f);

        foreach (var b in new[] { revThBtn, revStBtn, revTuBtn, flipHBtn, flipVBtn, camTogBtn })
            b.AddComponent<LayoutElement>().preferredHeight = 34f;

        // Roster scroll
        RectTransform rosterContent;
        var rosterScroll = CreateScrollCard(leftCol.transform, "RosterScroll", out rosterContent);
        rosterScroll.AddComponent<LayoutElement>().flexibleHeight = 1f;
        rosterScroll.GetComponent<Image>().color = C_PANEL;

        // RobotListPanel
        var stale = pp.GetComponent<TeamRosterPanel>();
        if (stale != null) Object.DestroyImmediate(stale);

        var rlp = pp.GetComponent<RobotListPanel>();
        if (rlp == null) rlp = pp.AddComponent<RobotListPanel>();
        {
            var so = new SerializedObject(rlp);
            SetProp(so, "rowContainer", rosterContent);
            so.ApplyModifiedProperties();
        }

        // ── 7b. Game settings column ──────────────────────────────────────────
        var gameSettingsCol = MakeSettingsColumn(columns.transform, "GameSettingsColumn", "GAME SETTINGS");
        RectTransform gameSettingsContent;
        var gameSettingsScroll = CreateScrollCard(gameSettingsCol.transform, "GameSettingsScroll", out gameSettingsContent);
        gameSettingsScroll.AddComponent<LayoutElement>().flexibleHeight = 1f;
        gameSettingsScroll.GetComponent<Image>().color = new Color(0.08f, 0.08f, 0.08f);

        var f_maxHp      = MakeSettingsRow(gameSettingsContent, "Max HP",      "Starting HP for every robot.");
        var f_damage     = MakeSettingsRow(gameSettingsContent, "Dmg/Hit",     "Base damage per IR hit (rear hits multiply).");
        var f_rearMult   = MakeSettingsRow(gameSettingsContent, "Rear Mult",   "Damage multiplier for S/SE/SW hits.");
        var f_duration   = MakeSettingsRow(gameSettingsContent, "Duration s",  "Match length in seconds.");
        var f_maxPl      = MakeSettingsRow(gameSettingsContent, "Max Players", "Max slots on public display page.");
        var f_teamPts    = MakeSettingsRow(gameSettingsContent, "Team Pts",    "Points needed for instant tug-of-war win.");
        var f_ptsKill    = MakeSettingsRow(gameSettingsContent, "Pts/Kill",    "Team points awarded per robot destroyed.");

        // ── 7c. Shot timing column ────────────────────────────────────────────
        var shotTimingCol = MakeSettingsColumn(columns.transform, "ShotTimingColumn", "SHOT TIMING");
        RectTransform shotTimingContent;
        var shotTimingScroll = CreateScrollCard(shotTimingCol.transform, "ShotTimingScroll", out shotTimingContent);
        shotTimingScroll.AddComponent<LayoutElement>().flexibleHeight = 1f;
        shotTimingScroll.GetComponent<Image>().color = new Color(0.08f, 0.08f, 0.08f);

        var f_cooldown   = MakeSettingsRow(shotTimingContent, "Cooldown s",  "Min seconds between shots per robot.");
        var f_slotFuture = MakeSettingsRow(shotTimingContent, "Slot Future", "ms shooter waits before emitting IR. Reduce to speed up; increase if enemies miss.");
        var f_listenDly  = MakeSettingsRow(shotTimingContent, "Listen Dly",  "Extra ms added to listener start time (usually 0).");
        var f_b1         = MakeSettingsRow(shotTimingContent, "Burst1 ms",   "IR burst 1 duration per rep. Longer = more detection chance.");
        var f_gap12      = MakeSettingsRow(shotTimingContent, "Gap 1-2 ms",  "Silent gap between burst 1 and burst 2.");
        var f_b2         = MakeSettingsRow(shotTimingContent, "Burst2 ms",   "IR burst 2 duration per rep.");
        var f_repGap     = MakeSettingsRow(shotTimingContent, "Rep Gap ms",  "Silent gap between repetitions.");
        var f_reps       = MakeSettingsRow(shotTimingContent, "Reps",        "Burst-pair repetitions per shot. More = reliable; longer total time.");
        var f_resBuf     = MakeSettingsRow(shotTimingContent, "Res Buf s",   "Seconds Unity waits for IR results after slot ends.");
        var t_disableCam    = MakeSettingsToggle(shotTimingContent, "Disable Cam",    "Pause camera streams during IR slot to reduce Wi-Fi congestion.");
        var t_disableMotors = MakeSettingsToggle(shotTimingContent, "Disable Motors", "Stop motors during IR slot to cut electrical noise on receivers.");

        var totalRow = MakeRect(shotTimingContent.transform, "TotalRow");
        totalRow.AddComponent<LayoutElement>().preferredHeight = 26f;
        var totalLbl = MakeTmp(totalRow.transform, "TotalLabel", "Total: --",
                                C_CYAN, 12f, TextAlignmentOptions.MidlineLeft, bold: true);
        Anchor(totalLbl.gameObject, 0, 0, 1, 1, 0, 0, 0, 0);

        // ── 8. Wire components ────────────────────────────────────────────────

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

        // Wire RobotActionButtons — the new unified component
        var rab = pp.GetComponent<RobotActionButtons>();
        if (rab == null) rab = pp.AddComponent<RobotActionButtons>();
        {
            var so = new SerializedObject(rab);
            SetProp(so, "robotListPanel",  rlp);
            SetProp(so, "hitFrontBtn",     hitFrontBtn.GetComponent<Button>());
            SetProp(so, "hitSideBtn",      hitSideBtn.GetComponent<Button>());
            SetProp(so, "hitRearBtn",      hitRearBtn.GetComponent<Button>());
            SetProp(so, "healBtn",         healBtn.GetComponent<Button>());
            SetProp(so, "revThrottleBtn",  revThBtn.GetComponent<Button>());
            SetProp(so, "revSteerBtn",     revStBtn.GetComponent<Button>());
            SetProp(so, "revTurretBtn",    revTuBtn.GetComponent<Button>());
            SetProp(so, "flipHBtn",        flipHBtn.GetComponent<Button>());
            SetProp(so, "flipVBtn",        flipVBtn.GetComponent<Button>());
            SetProp(so, "camToggleBtn",    camTogBtn.GetComponent<Button>());
            SetProp(so, "captureNorthBtn",  capNorthBtn.GetComponent<Button>());
            SetProp(so, "captureCentreBtn", capCentreBtn.GetComponent<Button>());
            SetProp(so, "captureSouthBtn",  capSouthBtn.GetComponent<Button>());
            so.ApplyModifiedProperties();
        }

        // Wire CapturePointsPanelUI for the circle indicators (still used by the
        // colour-only display logic, separate from the clickable buttons)
        System.Type cpType = null;
        foreach (var asm in System.AppDomain.CurrentDomain.GetAssemblies())
        {
            cpType = asm.GetType("CapturePointsPanelUI");
            if (cpType != null) break;
        }
        if (cpType != null)
        {
            // CapturePointsPanelUI driven from capture buttons' Image components
            var cpUI = pp.GetComponent(cpType);
            if (cpUI == null) cpUI = pp.AddComponent(cpType);
            var so = new SerializedObject(cpUI);
            SetProp(so, "circle0", capNorthBtn.GetComponent<Image>());
            SetProp(so, "circle1", capCentreBtn.GetComponent<Image>());
            SetProp(so, "circle2", capSouthBtn.GetComponent<Image>());
            so.ApplyModifiedProperties();
        }
        else Debug.LogWarning("[RebuildPlaying] CapturePointsPanelUI type not found.");

        // RobotControlsGroup — greys robot-specific buttons when nothing is selected.
        // Capture buttons are excluded (they work regardless of selection).
        var rcg = pp.GetComponent<RobotControlsGroup>();
        if (rcg == null) rcg = pp.AddComponent<RobotControlsGroup>();
        {
            var so = new SerializedObject(rcg);
            SetProp(so, "robotListPanel", rlp);
            var arr = so.FindProperty("buttons");
            var robotBtns = new[] {
                hitFrontBtn.GetComponent<Button>(),
                hitSideBtn.GetComponent<Button>(),
                hitRearBtn.GetComponent<Button>(),
                healBtn.GetComponent<Button>(),
                revThBtn.GetComponent<Button>(),
                revStBtn.GetComponent<Button>(),
                revTuBtn.GetComponent<Button>(),
                flipHBtn.GetComponent<Button>(),
                flipVBtn.GetComponent<Button>(),
                camTogBtn.GetComponent<Button>(),
            };
            arr.arraySize = robotBtns.Length;
            for (int i = 0; i < robotBtns.Length; i++)
                arr.GetArrayElementAtIndex(i).objectReferenceValue = robotBtns[i];
            so.ApplyModifiedProperties();
        }

        // Wire PlayingSettingsPanel
        var psp = pp.GetComponent<PlayingSettingsPanel>();
        if (psp == null) psp = pp.AddComponent<PlayingSettingsPanel>();
        {
            var so = new SerializedObject(psp);
            SetProp(so, "maxHpField",      f_maxHp);
            SetProp(so, "damageField",     f_damage);
            SetProp(so, "rearMultField",   f_rearMult);
            SetProp(so, "durationField",   f_duration);
            SetProp(so, "maxPlayersField", f_maxPl);
            SetProp(so, "maxTeamPtsField", f_teamPts);
            SetProp(so, "ptsPerKillField", f_ptsKill);
            SetProp(so, "cooldownField",   f_cooldown);
            SetProp(so, "slotFutureField", f_slotFuture);
            SetProp(so, "listenDelayField",f_listenDly);
            SetProp(so, "b1DurField",      f_b1);
            SetProp(so, "gap12Field",      f_gap12);
            SetProp(so, "b2DurField",      f_b2);
            SetProp(so, "repGapField",     f_repGap);
            SetProp(so, "repsField",       f_reps);
            SetProp(so, "resultBufField",       f_resBuf);
            SetProp(so, "disableCameraToggle",  t_disableCam);
            SetProp(so, "disableMotorsToggle",  t_disableMotors);
            var prop = so.FindProperty("totalTimeLabel");
            if (prop != null) prop.objectReferenceValue = totalLbl;
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

    // ── Capture button helper ─────────────────────────────────────────────────

    static GameObject MakeCaptureButton(Transform parent, string name, string label)
    {
        var go = new GameObject(name);
        go.AddComponent<RectTransform>();
        go.transform.SetParent(parent, false);
        var img = go.AddComponent<Image>();
        img.color = new Color(0.20f, 0.20f, 0.20f);
        go.AddComponent<Button>();
        var le = go.AddComponent<LayoutElement>();
        le.preferredHeight = 30f;

        var lbl = MakeTmp(go.transform, "Label", label + "\n—",
                           C_TEXT, 9f, TextAlignmentOptions.Center, bold: true);
        Anchor(lbl.gameObject, 0, 0, 1, 1, 2, 2, -2, -2);
        return go;
    }

    // ── Generic helpers ───────────────────────────────────────────────────────

    static void RemoveIfPresent<T>(GameObject go) where T : Component
    {
        var c = go.GetComponent<T>();
        if (c != null) Object.DestroyImmediate(c);
    }

    static void SetProp(SerializedObject so, string propName, UnityEngine.Object value)
    {
        var prop = so.FindProperty(propName);
        if (prop == null)
            Debug.LogWarning($"[RebuildPlaying] Property '{propName}' not found on {so.targetObject?.GetType().Name}");
        else
            prop.objectReferenceValue = value;
    }

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

    static GameObject MakeSettingsColumn(Transform parent, string name, string header)
    {
        var col = MakeRect(parent, name);
        col.AddComponent<Image>().color = C_PANEL;
        var le = col.AddComponent<LayoutElement>();
        le.preferredWidth = 210f;
        le.flexibleHeight = 1f;
        le.flexibleWidth  = 0f;

        var vlg = col.AddComponent<VerticalLayoutGroup>();
        vlg.spacing             = 4f;
        vlg.padding             = new RectOffset(8, 8, 8, 8);
        vlg.childForceExpandWidth  = true;
        vlg.childForceExpandHeight = false;
        vlg.childControlWidth      = true;
        vlg.childControlHeight     = true;
        vlg.childAlignment         = TextAnchor.UpperLeft;

        var hdr = MakeTmp(col.transform, "LblHeader", header,
                           C_CYAN, 12f, TextAlignmentOptions.MidlineLeft, bold: true);
        hdr.characterSpacing = 1f;
        hdr.gameObject.AddComponent<LayoutElement>().preferredHeight = 22f;

        return col;
    }

    static TMP_InputField MakeSettingsRow(RectTransform parent, string labelText, string description)
    {
        var row = MakeRect(parent.transform, "Row_" + labelText.Replace(" ", ""));
        var rowVLG = row.AddComponent<VerticalLayoutGroup>();
        rowVLG.spacing             = 1f;
        rowVLG.childForceExpandWidth  = true;
        rowVLG.childForceExpandHeight = false;
        rowVLG.childControlWidth      = true;
        rowVLG.childControlHeight     = true;
        row.AddComponent<LayoutElement>().preferredHeight = 50f;

        var topRow = MakeRect(row.transform, "TopRow");
        var topHLG = topRow.AddComponent<HorizontalLayoutGroup>();
        topHLG.spacing             = 4f;
        topHLG.childForceExpandWidth  = false;
        topHLG.childForceExpandHeight = true;
        topHLG.childControlWidth      = true;
        topHLG.childControlHeight     = true;
        topRow.AddComponent<LayoutElement>().preferredHeight = 28f;

        var lbl = MakeTmp(topRow.transform, "Lbl", labelText,
                           new Color(0.75f, 0.75f, 0.75f), 12f, TextAlignmentOptions.MidlineLeft);
        lbl.gameObject.AddComponent<LayoutElement>().preferredWidth = 96f;

        var inputField = MakeTmpInputField(topRow.transform, "Field");

        var desc = MakeTmp(row.transform, "Desc", description,
                            new Color(0.38f, 0.38f, 0.38f), 9f, TextAlignmentOptions.MidlineLeft);
        desc.enableWordWrapping = true;
        desc.gameObject.AddComponent<LayoutElement>().preferredHeight = 18f;

        return inputField;
    }

    static TMP_InputField MakeTmpInputField(Transform parent, string name)
    {
        var go = new GameObject(name);
        go.AddComponent<RectTransform>();
        go.transform.SetParent(parent, false);

        var bg = go.AddComponent<Image>();
        bg.color = new Color(0.14f, 0.14f, 0.14f);

        var field = go.AddComponent<TMP_InputField>();

        var textArea = new GameObject("Text Area");
        textArea.AddComponent<RectTransform>();
        textArea.transform.SetParent(go.transform, false);
        var taRT = textArea.GetComponent<RectTransform>();
        taRT.anchorMin = Vector2.zero;
        taRT.anchorMax = Vector2.one;
        taRT.offsetMin = new Vector2(4f, 0f);
        taRT.offsetMax = new Vector2(-4f, 0f);
        textArea.AddComponent<RectMask2D>();

        var textGo = new GameObject("Text");
        textGo.AddComponent<RectTransform>();
        textGo.transform.SetParent(textArea.transform, false);
        var textRT = textGo.GetComponent<RectTransform>();
        textRT.anchorMin = Vector2.zero;
        textRT.anchorMax = Vector2.one;
        textRT.offsetMin = Vector2.zero;
        textRT.offsetMax = Vector2.zero;

        var tmp = textGo.AddComponent<TextMeshProUGUI>();
        tmp.font      = _font;
        tmp.fontSize  = 12f;
        tmp.color     = new Color(0.91f, 0.91f, 0.91f);
        tmp.alignment = TextAlignmentOptions.MidlineLeft;

        field.textViewport  = taRT;
        field.textComponent = tmp;
        field.fontAsset     = _font;
        field.pointSize     = 12f;

        go.AddComponent<LayoutElement>().flexibleWidth = 1f;

        return field;
    }

    static Toggle MakeSettingsToggle(RectTransform parent, string labelText, string description)
    {
        var row = MakeRect(parent.transform, "Row_" + labelText.Replace(" ", ""));
        var rowVLG = row.AddComponent<VerticalLayoutGroup>();
        rowVLG.spacing             = 1f;
        rowVLG.childForceExpandWidth  = true;
        rowVLG.childForceExpandHeight = false;
        rowVLG.childControlWidth      = true;
        rowVLG.childControlHeight     = true;
        row.AddComponent<LayoutElement>().preferredHeight = 50f;

        var topRow = MakeRect(row.transform, "TopRow");
        var topHLG = topRow.AddComponent<HorizontalLayoutGroup>();
        topHLG.spacing             = 6f;
        topHLG.childForceExpandWidth  = false;
        topHLG.childForceExpandHeight = true;
        topHLG.childControlWidth      = true;
        topHLG.childControlHeight     = true;
        topRow.AddComponent<LayoutElement>().preferredHeight = 28f;

        var checkboxGo = MakeRect(topRow.transform, "Toggle");
        var checkboxLE = checkboxGo.AddComponent<LayoutElement>();
        checkboxLE.preferredWidth  = 24f;
        checkboxLE.preferredHeight = 24f;
        checkboxLE.flexibleWidth   = 0f;
        var bgImg = checkboxGo.AddComponent<Image>();
        bgImg.color = new Color(0.14f, 0.14f, 0.14f);
        var toggle = checkboxGo.AddComponent<Toggle>();
        toggle.targetGraphic = bgImg;

        var checkmarkGo = MakeRect(checkboxGo.transform, "Checkmark");
        var checkmarkRT = checkmarkGo.GetComponent<RectTransform>();
        checkmarkRT.anchorMin = new Vector2(0.15f, 0.15f);
        checkmarkRT.anchorMax = new Vector2(0.85f, 0.85f);
        checkmarkRT.offsetMin = Vector2.zero;
        checkmarkRT.offsetMax = Vector2.zero;
        var checkImg = checkmarkGo.AddComponent<Image>();
        checkImg.color = C_CYAN;
        toggle.graphic = checkImg;

        var lbl = MakeTmp(topRow.transform, "Lbl", labelText,
                           new Color(0.75f, 0.75f, 0.75f), 12f, TextAlignmentOptions.MidlineLeft);
        lbl.gameObject.AddComponent<LayoutElement>().flexibleWidth = 1f;

        var desc = MakeTmp(row.transform, "Desc", description,
                            new Color(0.38f, 0.38f, 0.38f), 9f, TextAlignmentOptions.MidlineLeft);
        desc.enableWordWrapping = true;
        desc.gameObject.AddComponent<LayoutElement>().preferredHeight = 18f;

        return toggle;
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
        vp.AddComponent<RectMask2D>();

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
