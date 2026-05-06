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

        var pingBtn  = MakeButton(ctrlRow.transform, "PingButton",    "PING",        C_DKBLUE, C_TEXT, 12f);
        pingBtn.AddComponent<LayoutElement>().preferredHeight = 34f;
        var dmgBtn   = MakeButton(ctrlRow.transform, "DamageButton",  "DAMAGE 10%",  C_DKRED,  C_TEXT, 12f);
        dmgBtn.AddComponent<LayoutElement>().preferredHeight = 34f;
        var healBtn  = MakeButton(ctrlRow.transform, "HealButton",    "HEAL",        new Color(0.10f, 0.35f, 0.10f), C_TEXT, 12f);
        healBtn.AddComponent<LayoutElement>().preferredHeight = 34f;
        var camBtn   = MakeButton(ctrlRow.transform, "ToggleCamButton", "TOGGLE CAM", new Color(0.10f, 0.25f, 0.35f), C_TEXT, 12f);
        camBtn.AddComponent<LayoutElement>().preferredHeight = 34f;
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

        // ── 8c. Game settings column ──────────────────────────────────────────
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

        // ── 8d. Shot timing column ────────────────────────────────────────────
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

        // Total time read-only label
        var totalRow = MakeRect(shotTimingContent.transform, "TotalRow");
        totalRow.AddComponent<LayoutElement>().preferredHeight = 26f;
        var totalLbl = MakeTmp(totalRow.transform, "TotalLabel", "Total: --",
                                C_CYAN, 12f, TextAlignmentOptions.MidlineLeft, bold: true);
        Anchor(totalLbl.gameObject, 0, 0, 1, 1, 0, 0, 0, 0);

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
            // Wire totalTimeLabel (TMP_Text, not TMP_InputField — use FindProperty directly)
            var prop = so.FindProperty("totalTimeLabel");
            if (prop != null) prop.objectReferenceValue = totalLbl;
            so.ApplyModifiedProperties();
        }

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
            SetProp(so, "resultLabel",    null);
            SetProp(so, "robotListPanel", rlp);
            so.ApplyModifiedProperties();
        }

        var tcb = pp.GetComponent<ToggleCameraButton>();
        if (tcb == null) tcb = pp.AddComponent<ToggleCameraButton>();
        {
            var so = new SerializedObject(tcb);
            SetProp(so, "button",         camBtn.GetComponent<Button>());
            SetProp(so, "robotListPanel", rlp);
            so.ApplyModifiedProperties();
        }

        var rcg = pp.GetComponent<RobotControlsGroup>();
        if (rcg == null) rcg = pp.AddComponent<RobotControlsGroup>();
        {
            var so = new SerializedObject(rcg);
            SetProp(so, "robotListPanel", rlp);
            var arr = so.FindProperty("buttons");
            arr.arraySize = 4;
            arr.GetArrayElementAtIndex(0).objectReferenceValue = pingBtn.GetComponent<Button>();
            arr.GetArrayElementAtIndex(1).objectReferenceValue = dmgBtn.GetComponent<Button>();
            arr.GetArrayElementAtIndex(2).objectReferenceValue = healBtn.GetComponent<Button>();
            arr.GetArrayElementAtIndex(3).objectReferenceValue = camBtn.GetComponent<Button>();
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

    // ── Settings column helper ────────────────────────────────────────────────────
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

    // ── Settings row helper ───────────────────────────────────────────────────────
    // Returns the TMP_InputField. Each row is a VLG block: top sub-row (label+input)
    // + description line below.
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

        // Top sub-row: label + input field
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

        // Description line
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

        // Text area child
        var textArea = new GameObject("Text Area");
        textArea.AddComponent<RectTransform>();
        textArea.transform.SetParent(go.transform, false);
        var taRT = textArea.GetComponent<RectTransform>();
        taRT.anchorMin = Vector2.zero;
        taRT.anchorMax = Vector2.one;
        taRT.offsetMin = new Vector2(4f, 0f);
        taRT.offsetMax = new Vector2(-4f, 0f);
        textArea.AddComponent<RectMask2D>();

        // Text child
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

    // ── Settings toggle row helper ────────────────────────────────────────────────
    // Returns the Toggle. Row layout matches MakeSettingsRow: top sub-row (checkbox+label)
    // + description line below.
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

        // Top sub-row: checkbox + label
        var topRow = MakeRect(row.transform, "TopRow");
        var topHLG = topRow.AddComponent<HorizontalLayoutGroup>();
        topHLG.spacing             = 6f;
        topHLG.childForceExpandWidth  = false;
        topHLG.childForceExpandHeight = true;
        topHLG.childControlWidth      = true;
        topHLG.childControlHeight     = true;
        topRow.AddComponent<LayoutElement>().preferredHeight = 28f;

        // Checkbox background
        var checkboxGo = MakeRect(topRow.transform, "Toggle");
        var checkboxLE = checkboxGo.AddComponent<LayoutElement>();
        checkboxLE.preferredWidth  = 24f;
        checkboxLE.preferredHeight = 24f;
        checkboxLE.flexibleWidth   = 0f;
        var bgImg = checkboxGo.AddComponent<Image>();
        bgImg.color = new Color(0.14f, 0.14f, 0.14f);
        var toggle = checkboxGo.AddComponent<Toggle>();
        toggle.targetGraphic = bgImg;

        // Checkmark (filled cyan square inset)
        var checkmarkGo = MakeRect(checkboxGo.transform, "Checkmark");
        var checkmarkRT = checkmarkGo.GetComponent<RectTransform>();
        checkmarkRT.anchorMin = new Vector2(0.15f, 0.15f);
        checkmarkRT.anchorMax = new Vector2(0.85f, 0.85f);
        checkmarkRT.offsetMin = Vector2.zero;
        checkmarkRT.offsetMax = Vector2.zero;
        var checkImg = checkmarkGo.AddComponent<Image>();
        checkImg.color = C_CYAN;
        toggle.graphic = checkImg;

        // Label
        var lbl = MakeTmp(topRow.transform, "Lbl", labelText,
                           new Color(0.75f, 0.75f, 0.75f), 12f, TextAlignmentOptions.MidlineLeft);
        lbl.gameObject.AddComponent<LayoutElement>().flexibleWidth = 1f;

        // Description line
        var desc = MakeTmp(row.transform, "Desc", description,
                            new Color(0.38f, 0.38f, 0.38f), 9f, TextAlignmentOptions.MidlineLeft);
        desc.enableWordWrapping = true;
        desc.gameObject.AddComponent<LayoutElement>().preferredHeight = 18f;

        return toggle;
    }

    static void MakeSectionLabel(RectTransform parent, string name, string text)
    {
        var lbl = MakeTmp(parent.transform, name, text,
                           new Color(0.45f, 0.45f, 0.45f), 8f, TextAlignmentOptions.MidlineLeft, bold: true);
        lbl.characterSpacing = 1f;
        lbl.gameObject.AddComponent<LayoutElement>().preferredHeight = 18f;
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
