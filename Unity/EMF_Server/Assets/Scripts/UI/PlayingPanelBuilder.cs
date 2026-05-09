// PlayingPanelBuilder.cs
// Attach to the PlayingPanel GameObject.  Destroys all children and rebuilds
// the full UI hierarchy.  In Edit mode it runs once and auto-saves the scene
// so the layout is permanently stored in the scene file.

using System.Reflection;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.SceneManagement;
#endif

[ExecuteAlways]
public class PlayingPanelBuilder : MonoBehaviour
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

    TMP_FontAsset _font;

    // Bump this string whenever the layout changes to force a one-time edit-mode rebuild.
    const string BUILT_SENTINEL = "__ppb_v3";

    // ── Entry point ───────────────────────────────────────────────────────────

    private void Awake()
    {
        _font = Resources.Load<TMP_FontAsset>("Fonts & Materials/LiberationSans SDF");

#if UNITY_EDITOR
        if (!Application.isPlaying)
        {
            EditModeBuildIfNeeded();
            return;
        }
#endif
        Build();
    }

#if UNITY_EDITOR
    // Also fires on domain reload (recompile) and when the component is added.
    private void OnValidate()
    {
        if (Application.isPlaying) return;
        if (transform.Find(BUILT_SENTINEL) != null) return;
        EditorApplication.delayCall += EditModeBuildIfNeeded;
    }

    void EditModeBuildIfNeeded()
    {
        if (this == null || transform.Find(BUILT_SENTINEL) != null) return;
        _font = Resources.Load<TMP_FontAsset>("Fonts & Materials/LiberationSans SDF");
        Build();
        // Mark as built so we don't rebuild on every compile
        var sentinel = new GameObject(BUILT_SENTINEL);
        sentinel.transform.SetParent(transform, false);
        sentinel.hideFlags = HideFlags.HideInHierarchy;
        EditorSceneManager.MarkSceneDirty(gameObject.scene);
        var scene = gameObject.scene;
        EditorApplication.delayCall += () =>
        {
            if (gameObject != null) EditorSceneManager.SaveScene(scene);
        };
    }
#endif

    // ── Build ─────────────────────────────────────────────────────────────────

    void Build()
    {
        var pp = gameObject;

        // Remove all existing children
        // Use DestroyImmediate in Edit mode so children are gone before new ones are added.
        for (int i = pp.transform.childCount - 1; i >= 0; i--)
        {
#if UNITY_EDITOR
            if (!Application.isPlaying)
                DestroyImmediate(pp.transform.GetChild(i).gameObject);
            else
#endif
                Destroy(pp.transform.GetChild(i).gameObject);
        }

        // Remove stale components
        RemoveIfPresent<PlayerInputMonitor>(pp);
        RemoveIfPresent<RobotHpPanel>(pp);
        RemoveIfPresent<TeamRosterPanel>(pp);
        RemoveIfPresent<HealButton>(pp);
        RemoveIfPresent<TestDamageButton>(pp);
        RemoveIfPresent<RobotPingButton>(pp);
        RemoveIfPresent<ToggleCameraButton>(pp);
        RemoveIfPresent<FlipVideoButtons>(pp);

        var bgImg = pp.GetComponent<Image>() ?? pp.AddComponent<Image>();
        bgImg.color = C_BG;

        // ── Header ────────────────────────────────────────────────────────────
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

        // ── Footer ────────────────────────────────────────────────────────────
        var footer = MakeRect(pp.transform, "Footer");
        Anchor(footer, 0, 0, 1, 0, 0, 0, 0, 54);
        footer.AddComponent<Image>().color = C_PANEL;

        var sep = MakeRect(footer.transform, "Sep");
        Anchor(sep, 0, 1, 1, 1, 0, -1, 0, 0);
        sep.AddComponent<Image>().color = new Color(0.165f, 0.165f, 0.165f);

        var pauseBtn   = MakeButton(footer.transform, "PauseButton",   "PAUSE",    C_DKBLUE, C_TEXT, 13f);
        Anchor(pauseBtn, 0.25f, 0, 0.25f, 1, -90, 7, 90, -7);

        var endGameBtn = MakeButton(footer.transform, "EndGameButton", "END GAME", C_DKRED, C_TEXT, 13f);
        Anchor(endGameBtn, 0.75f, 0, 0.75f, 1, -90, 7, 90, -7);

        // ── Body ──────────────────────────────────────────────────────────────
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

        // ── Team points bar (full width) ──────────────────────────────────────
        var tpBar = MakeRect(body.transform, "TeamPointsBar");
        tpBar.AddComponent<Image>().color = C_PANEL;
        tpBar.AddComponent<LayoutElement>().preferredHeight = 44f;

        var tpLabels = MakeRect(tpBar.transform, "Labels");
        Anchor(tpLabels, 0, 0.5f, 1, 1, 10, 0, -10, 0);
        var tpLblHLG = tpLabels.AddComponent<HorizontalLayoutGroup>();
        tpLblHLG.childForceExpandWidth = tpLblHLG.childForceExpandHeight = true;
        tpLblHLG.childControlWidth = tpLblHLG.childControlHeight = true;

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

        var centre = MakeRect(tpTrack.transform, "Centre");
        Anchor(centre, 0.5f, 0, 0.5f, 1, -1, 0, 1, 0);
        centre.AddComponent<Image>().color = new Color(0.25f, 0.25f, 0.25f);

        var tpBarUI = GetOrAdd<TeamPointsBarUI>(pp);
        Wire(tpBarUI, "fill0",  fill0.GetComponent<RectTransform>());
        Wire(tpBarUI, "fill1",  fill1.GetComponent<RectTransform>());
        Wire(tpBarUI, "label0", tpLabel0);
        Wire(tpBarUI, "label1", tpLabel1);

        // ── Event log (full width) ────────────────────────────────────────────
        var evtGo = MakeRect(body.transform, "EventLog");
        evtGo.AddComponent<Image>().color = C_PANEL;
        evtGo.AddComponent<LayoutElement>().preferredHeight = 82f;

        var evtContainer = MakeRect(evtGo.transform, "Container");
        Anchor(evtContainer, 0, 0, 1, 1, 10, 6, -10, -6);
        var evtVLG = evtContainer.AddComponent<VerticalLayoutGroup>();
        evtVLG.spacing = 2f;
        evtVLG.childForceExpandWidth  = true;
        evtVLG.childForceExpandHeight = false;
        evtVLG.childControlWidth = evtVLG.childControlHeight = true;
        evtVLG.childAlignment = TextAnchor.UpperLeft;

        var evtPanel = GetOrAdd<EventLogPanelUI>(pp);
        Wire(evtPanel, "container", evtContainer.GetComponent<RectTransform>());

        // ── Columns area ──────────────────────────────────────────────────────
        var columns = MakeRect(body.transform, "Columns");
        columns.AddComponent<LayoutElement>().flexibleHeight = 1f;

        var colHLG = columns.AddComponent<HorizontalLayoutGroup>();
        colHLG.spacing = 8f;
        colHLG.childForceExpandWidth  = false;
        colHLG.childForceExpandHeight = true;
        colHLG.childControlWidth = colHLG.childControlHeight = true;
        colHLG.childAlignment = TextAnchor.UpperLeft;

        // ── Left column ───────────────────────────────────────────────────────
        var leftCol = MakeRect(columns.transform, "LeftColumn");
        var leftLE  = leftCol.AddComponent<LayoutElement>();
        leftLE.flexibleWidth = leftLE.flexibleHeight = 1f;

        var leftVLG = leftCol.AddComponent<VerticalLayoutGroup>();
        leftVLG.spacing = 4f;
        leftVLG.childForceExpandWidth  = true;
        leftVLG.childForceExpandHeight = false;
        leftVLG.childControlWidth = leftVLG.childControlHeight = true;
        leftVLG.childAlignment = TextAnchor.UpperLeft;

        var rosterLbl = MakeTmp(leftCol.transform, "LblRoster", "ROBOTS & PLAYERS",
                                 C_CYAN, 10f, TextAlignmentOptions.MidlineLeft, bold: true);
        rosterLbl.characterSpacing = 1.5f;
        rosterLbl.gameObject.AddComponent<LayoutElement>().preferredHeight = 22f;

        // Action buttons — 3 rows
        var actionPanel = MakeRect(leftCol.transform, "ActionButtonsPanel");
        actionPanel.AddComponent<LayoutElement>().preferredHeight = 114f;
        var apVLG = actionPanel.AddComponent<VerticalLayoutGroup>();
        apVLG.spacing = 4f;
        apVLG.childForceExpandWidth  = true;
        apVLG.childForceExpandHeight = false;
        apVLG.childControlWidth = apVLG.childControlHeight = true;

        // Row 1: Damage
        var dmgRow = MakeActionRow(actionPanel.transform, "DamageRow");
        var hitFrontBtn = MakeButton(dmgRow.transform, "HitFrontBtn", "HIT F",   C_DKRED,  C_TEXT, 11f);
        var hitSideBtn  = MakeButton(dmgRow.transform, "HitSideBtn",  "HIT S",   C_DKRED,  C_TEXT, 11f);
        var hitRearBtn  = MakeButton(dmgRow.transform, "HitRearBtn",  "HIT R×3", new Color(0.7f, 0.05f, 0.05f), C_TEXT, 11f);
        var healBtn     = MakeButton(dmgRow.transform, "HealBtn",     "HEAL",    C_DKGRN,  C_TEXT, 11f);
        SetRowHeights(34f, hitFrontBtn, hitSideBtn, hitRearBtn, healBtn);

        // Row 2: Drive + Camera
        var drvRow = MakeActionRow(actionPanel.transform, "DriveCamRow");
        var revThBtn  = MakeButton(drvRow.transform, "RevThrottleBtn", "REV THROTTLE: OFF", C_DKBLU2, C_TEXT, 9f);
        var revStBtn  = MakeButton(drvRow.transform, "RevSteerBtn",    "REV STEER: OFF",    C_DKBLU2, C_TEXT, 9f);
        var revTuBtn  = MakeButton(drvRow.transform, "RevTurretBtn",   "REV TURRET: OFF",   C_DKBLU2, C_TEXT, 9f);
        var flipHBtn  = MakeButton(drvRow.transform, "FlipHBtn",       "FLIP H",            C_DKPUR,  C_TEXT, 10f);
        var flipVBtn  = MakeButton(drvRow.transform, "FlipVBtn",       "FLIP V",            C_DKPUR,  C_TEXT, 10f);
        var camTogBtn = MakeButton(drvRow.transform, "CamToggleBtn",   "CAM ON/OFF",        new Color(0.10f, 0.25f, 0.35f), C_TEXT, 10f);
        SetRowHeights(34f, revThBtn, revStBtn, revTuBtn, flipHBtn, flipVBtn, camTogBtn);

        // Row 3: Capture
        var capRow = MakeActionRow(actionPanel.transform, "CaptureRow");
        var capNorthBtn  = MakeButton(capRow.transform, "CaptureNorthBtn",  "NORTH\n—",  C_CAPNTR, C_TEXT, 9f);
        var capCentreBtn = MakeButton(capRow.transform, "CaptureCentreBtn", "CENTRE\n—", C_CAPNTR, C_TEXT, 9f);
        var capSouthBtn  = MakeButton(capRow.transform, "CaptureSouthBtn",  "SOUTH\n—",  C_CAPNTR, C_TEXT, 9f);
        SetRowHeights(34f, capNorthBtn, capCentreBtn, capSouthBtn);

        // Roster scroll
        RectTransform rosterContent;
        var rosterScroll = CreateScrollCard(leftCol.transform, "RosterScroll", out rosterContent);
        rosterScroll.AddComponent<LayoutElement>().flexibleHeight = 1f;
        rosterScroll.GetComponent<Image>().color = C_PANEL;

        RemoveIfPresent<TeamRosterPanel>(pp);
        var rlp = GetOrAdd<RobotListPanel>(pp);
        Wire(rlp, "rowContainer", rosterContent);

        // ── Game settings column ──────────────────────────────────────────────
        var gameSettingsCol = MakeSettingsColumn(columns.transform, "GameSettingsColumn", "GAME SETTINGS");
        RectTransform gameSettingsContent;
        var gameSettingsScroll = CreateScrollCard(gameSettingsCol.transform, "GameSettingsScroll", out gameSettingsContent);
        gameSettingsScroll.AddComponent<LayoutElement>().flexibleHeight = 1f;
        gameSettingsScroll.GetComponent<Image>().color = new Color(0.08f, 0.08f, 0.08f);

        var f_maxHp    = MakeSettingsRow(gameSettingsContent, "Max HP",      "Starting HP for every robot.");
        var f_damage   = MakeSettingsRow(gameSettingsContent, "Dmg/Hit",     "Base damage per IR hit (rear hits multiply).");
        var f_rearMult = MakeSettingsRow(gameSettingsContent, "Rear Mult",   "Damage multiplier for S/SE/SW hits.");
        var f_duration = MakeSettingsRow(gameSettingsContent, "Duration s",  "Match length in seconds.");
        var f_maxPl    = MakeSettingsRow(gameSettingsContent, "Max Players", "Max slots on public display page.");
        var f_teamPts  = MakeSettingsRow(gameSettingsContent, "Team Pts",    "Points needed for instant tug-of-war win.");
        var f_ptsKill  = MakeSettingsRow(gameSettingsContent, "Pts/Kill",    "Team points awarded per robot destroyed.");

        // ── Shot timing column ────────────────────────────────────────────────
        var shotTimingCol = MakeSettingsColumn(columns.transform, "ShotTimingColumn", "SHOT TIMING");
        RectTransform shotTimingContent;
        var shotTimingScroll = CreateScrollCard(shotTimingCol.transform, "ShotTimingScroll", out shotTimingContent);
        shotTimingScroll.AddComponent<LayoutElement>().flexibleHeight = 1f;
        shotTimingScroll.GetComponent<Image>().color = new Color(0.08f, 0.08f, 0.08f);

        var f_cooldown   = MakeSettingsRow(shotTimingContent, "Cooldown s",  "Min seconds between shots per robot.");
        var f_slotFuture = MakeSettingsRow(shotTimingContent, "Slot Future", "ms shooter waits before emitting IR.");
        var f_listenDly  = MakeSettingsRow(shotTimingContent, "Listen Dly",  "Extra ms added to listener start time.");
        var f_b1         = MakeSettingsRow(shotTimingContent, "Burst1 ms",   "IR burst 1 duration per rep.");
        var f_gap12      = MakeSettingsRow(shotTimingContent, "Gap 1-2 ms",  "Silent gap between burst 1 and burst 2.");
        var f_b2         = MakeSettingsRow(shotTimingContent, "Burst2 ms",   "IR burst 2 duration per rep.");
        var f_repGap     = MakeSettingsRow(shotTimingContent, "Rep Gap ms",  "Silent gap between repetitions.");
        var f_reps       = MakeSettingsRow(shotTimingContent, "Reps",        "Burst-pair repetitions per shot.");
        var f_resBuf     = MakeSettingsRow(shotTimingContent, "Res Buf s",   "Seconds Unity waits for IR results.");
        var t_disableCam    = MakeSettingsToggle(shotTimingContent, "Disable Cam",    "Pause camera streams during IR slot.");
        var t_disableMotors = MakeSettingsToggle(shotTimingContent, "Disable Motors", "Stop motors during IR slot.");
        var t_handshakeIr   = MakeSettingsToggle(shotTimingContent, "Handshake IR",   "ACK-driven: no clock sync, eliminates ping-spike misses.");

        var totalRow = MakeRect(shotTimingContent.transform, "TotalRow");
        totalRow.AddComponent<LayoutElement>().preferredHeight = 26f;
        var totalLbl = MakeTmp(totalRow.transform, "TotalLabel", "Total: --",
                                C_CYAN, 12f, TextAlignmentOptions.MidlineLeft, bold: true);
        Anchor(totalLbl.gameObject, 0, 0, 1, 1, 0, 0, 0, 0);

        // ── Wire components ───────────────────────────────────────────────────

        var pgb = GetOrAdd<PauseGameButton>(pp);
        Wire(pgb, "button", pauseBtn.GetComponent<Button>());
        Wire(pgb, "label",  pauseBtn.GetComponentInChildren<TextMeshProUGUI>());

        var mtd = pp.GetComponent<MatchTimerDisplay>();
        if (mtd != null) Wire(mtd, "timerLabel", timerLbl);

        var gpp = pp.GetComponent<GamePanelPresenter>();
        if (gpp != null) Wire(gpp, "endGameButton", endGameBtn.GetComponent<Button>());

        var sc = pp.GetComponent<ShootingController>();
        if (sc != null) { Wire(sc, "shootButton", null); Wire(sc, "cooldownLabel", null); }

        var rab = GetOrAdd<RobotActionButtons>(pp);
        Wire(rab, "robotListPanel",  rlp);
        Wire(rab, "hitFrontBtn",     hitFrontBtn.GetComponent<Button>());
        Wire(rab, "hitSideBtn",      hitSideBtn.GetComponent<Button>());
        Wire(rab, "hitRearBtn",      hitRearBtn.GetComponent<Button>());
        Wire(rab, "healBtn",         healBtn.GetComponent<Button>());
        Wire(rab, "revThrottleBtn",  revThBtn.GetComponent<Button>());
        Wire(rab, "revSteerBtn",     revStBtn.GetComponent<Button>());
        Wire(rab, "revTurretBtn",    revTuBtn.GetComponent<Button>());
        Wire(rab, "flipHBtn",        flipHBtn.GetComponent<Button>());
        Wire(rab, "flipVBtn",        flipVBtn.GetComponent<Button>());
        Wire(rab, "camToggleBtn",    camTogBtn.GetComponent<Button>());
        Wire(rab, "captureNorthBtn",  capNorthBtn.GetComponent<Button>());
        Wire(rab, "captureCentreBtn", capCentreBtn.GetComponent<Button>());
        Wire(rab, "captureSouthBtn",  capSouthBtn.GetComponent<Button>());

        var rcg = GetOrAdd<RobotControlsGroup>(pp);
        Wire(rcg, "robotListPanel", rlp);
        WireArray(rcg, "buttons", new Button[] {
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
        });

        var psp = GetOrAdd<PlayingSettingsPanel>(pp);
        Wire(psp, "maxHpField",          f_maxHp);
        Wire(psp, "damageField",         f_damage);
        Wire(psp, "rearMultField",       f_rearMult);
        Wire(psp, "durationField",       f_duration);
        Wire(psp, "maxPlayersField",     f_maxPl);
        Wire(psp, "maxTeamPtsField",     f_teamPts);
        Wire(psp, "ptsPerKillField",     f_ptsKill);
        Wire(psp, "cooldownField",       f_cooldown);
        Wire(psp, "slotFutureField",     f_slotFuture);
        Wire(psp, "listenDelayField",    f_listenDly);
        Wire(psp, "b1DurField",          f_b1);
        Wire(psp, "gap12Field",          f_gap12);
        Wire(psp, "b2DurField",          f_b2);
        Wire(psp, "repGapField",         f_repGap);
        Wire(psp, "repsField",           f_reps);
        Wire(psp, "resultBufField",      f_resBuf);
        Wire(psp, "disableCameraToggle",  t_disableCam);
        Wire(psp, "disableMotorsToggle",  t_disableMotors);
        Wire(psp, "useHandshakeIrToggle", t_handshakeIr);
        Wire(psp, "totalTimeLabel",       totalLbl);

        var rsp = pp.GetComponent<RobotSelectionPanel>();
        if (rsp != null)
        {
            Wire(rsp, "prevButton",    null);
            Wire(rsp, "nextButton",    null);
            Wire(rsp, "clearButton",   null);
            Wire(rsp, "nameLabel",     null);
            Wire(rsp, "ipLabel",       null);
            Wire(rsp, "playerLabel",   null);
            Wire(rsp, "allianceLabel", null);
            Wire(rsp, "clientLabel",   null);
        }
    }

    // ── Reflection helpers ────────────────────────────────────────────────────

    static void Wire(object comp, string fieldName, object value)
    {
        if (comp == null) return;
        var f = comp.GetType().GetField(fieldName,
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (f != null) f.SetValue(comp, value);
        else Debug.LogWarning($"[PlayingPanelBuilder] Field '{fieldName}' not found on {comp.GetType().Name}");
    }

    static void WireArray<T>(object comp, string fieldName, T[] values)
    {
        if (comp == null) return;
        var f = comp.GetType().GetField(fieldName,
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (f != null) f.SetValue(comp, values);
        else Debug.LogWarning($"[PlayingPanelBuilder] Field '{fieldName}' not found on {comp.GetType().Name}");
    }

    static T GetOrAdd<T>(GameObject go) where T : Component
        => go.GetComponent<T>() ?? go.AddComponent<T>();

    static void RemoveIfPresent<T>(GameObject go) where T : Component
    {
        var c = go.GetComponent<T>();
        if (c == null) return;
#if UNITY_EDITOR
        if (!Application.isPlaying) DestroyImmediate(c);
        else
#endif
            Destroy(c);
    }

    // ── Layout helpers ────────────────────────────────────────────────────────

    static GameObject MakeActionRow(Transform parent, string name)
    {
        var go = MakeRect(parent, name);
        go.AddComponent<LayoutElement>().preferredHeight = 34f;
        var hlg = go.AddComponent<HorizontalLayoutGroup>();
        hlg.spacing = 4f;
        hlg.childForceExpandWidth  = true;
        hlg.childForceExpandHeight = false;
        hlg.childControlWidth = hlg.childControlHeight = true;
        return go;
    }

    static void SetRowHeights(float h, params GameObject[] btns)
    {
        foreach (var b in btns)
            b.AddComponent<LayoutElement>().preferredHeight = h;
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

    TextMeshProUGUI MakeTmp(Transform parent, string name, string text,
                             Color color, float size,
                             TextAlignmentOptions align, bool bold = false)
    {
        var go = new GameObject(name);
        var rt = go.AddComponent<RectTransform>();
        go.transform.SetParent(parent, false);
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = rt.offsetMax = Vector2.zero;
        var tmp = go.AddComponent<TextMeshProUGUI>();
        tmp.text = text; tmp.font = _font; tmp.fontSize = size;
        tmp.color = color; tmp.alignment = align;
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
        var lbl = new GameObject("Label");
        lbl.AddComponent<RectTransform>();
        lbl.transform.SetParent(go.transform, false);
        var rt = lbl.GetComponent<RectTransform>();
        rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
        rt.offsetMin = rt.offsetMax = Vector2.zero;
        // MakeTmp equivalent inline (needs _font — but MakeButton is static, so we use a workaround)
        var tmp = lbl.AddComponent<TextMeshProUGUI>();
        tmp.text = label; tmp.fontSize = fontSize; tmp.color = textColor;
        tmp.alignment = TextAlignmentOptions.Center; tmp.fontStyle = FontStyles.Bold;
        // Note: _font wired separately below since this is static
        return go;
    }

    // Wire fonts on all TMP components in the built hierarchy after build
    void LateUpdate()
    {
        if (_font == null) return;
        foreach (var tmp in GetComponentsInChildren<TextMeshProUGUI>())
            if (tmp.font == null) tmp.font = _font;
        enabled = false; // run once
    }

    static GameObject MakeSettingsColumn(Transform parent, string name, string header)
    {
        var col = MakeRect(parent, name);
        col.AddComponent<Image>().color = new Color(0.102f, 0.102f, 0.102f);
        var le = col.AddComponent<LayoutElement>();
        le.preferredWidth = 210f; le.flexibleHeight = 1f; le.flexibleWidth = 0f;
        var vlg = col.AddComponent<VerticalLayoutGroup>();
        vlg.spacing = 4f;
        vlg.padding = new RectOffset(8, 8, 8, 8);
        vlg.childForceExpandWidth = true; vlg.childForceExpandHeight = false;
        vlg.childControlWidth = vlg.childControlHeight = true;
        vlg.childAlignment = TextAnchor.UpperLeft;
        return col;
    }

    TMP_InputField MakeSettingsRow(RectTransform parent, string labelText, string description)
    {
        var row = MakeRect(parent.transform, "Row_" + labelText.Replace(" ", ""));
        var rowVLG = row.AddComponent<VerticalLayoutGroup>();
        rowVLG.spacing = 1f;
        rowVLG.childForceExpandWidth = true; rowVLG.childForceExpandHeight = false;
        rowVLG.childControlWidth = rowVLG.childControlHeight = true;
        row.AddComponent<LayoutElement>().preferredHeight = 50f;

        var topRow = MakeRect(row.transform, "TopRow");
        var topHLG = topRow.AddComponent<HorizontalLayoutGroup>();
        topHLG.spacing = 4f;
        topHLG.childForceExpandWidth = false; topHLG.childForceExpandHeight = true;
        topHLG.childControlWidth = topHLG.childControlHeight = true;
        topRow.AddComponent<LayoutElement>().preferredHeight = 28f;

        var lbl = MakeTmp(topRow.transform, "Lbl", labelText,
                           new Color(0.75f, 0.75f, 0.75f), 12f, TextAlignmentOptions.MidlineLeft);
        lbl.gameObject.AddComponent<LayoutElement>().preferredWidth = 96f;

        var field = MakeInputField(topRow.transform, "Field");

        var desc = MakeTmp(row.transform, "Desc", description,
                            new Color(0.38f, 0.38f, 0.38f), 9f, TextAlignmentOptions.MidlineLeft);
        desc.enableWordWrapping = true;
        desc.gameObject.AddComponent<LayoutElement>().preferredHeight = 18f;

        return field;
    }

    TMP_InputField MakeInputField(Transform parent, string name)
    {
        var go = new GameObject(name);
        go.AddComponent<RectTransform>();
        go.transform.SetParent(parent, false);
        go.AddComponent<Image>().color = new Color(0.14f, 0.14f, 0.14f);
        var field = go.AddComponent<TMP_InputField>();

        var textArea = new GameObject("Text Area");
        textArea.AddComponent<RectTransform>();
        textArea.transform.SetParent(go.transform, false);
        var taRT = textArea.GetComponent<RectTransform>();
        taRT.anchorMin = Vector2.zero; taRT.anchorMax = Vector2.one;
        taRT.offsetMin = new Vector2(4f, 0f); taRT.offsetMax = new Vector2(-4f, 0f);
        textArea.AddComponent<RectMask2D>();

        var textGo = new GameObject("Text");
        textGo.AddComponent<RectTransform>();
        textGo.transform.SetParent(textArea.transform, false);
        var textRT = textGo.GetComponent<RectTransform>();
        textRT.anchorMin = Vector2.zero; textRT.anchorMax = Vector2.one;
        textRT.offsetMin = textRT.offsetMax = Vector2.zero;

        var tmp = textGo.AddComponent<TextMeshProUGUI>();
        tmp.font = _font; tmp.fontSize = 12f;
        tmp.color = new Color(0.91f, 0.91f, 0.91f);
        tmp.alignment = TextAlignmentOptions.MidlineLeft;

        field.textViewport = taRT; field.textComponent = tmp;
        field.fontAsset = _font; field.pointSize = 12f;

        go.AddComponent<LayoutElement>().flexibleWidth = 1f;
        return field;
    }

    Toggle MakeSettingsToggle(RectTransform parent, string labelText, string description)
    {
        var row = MakeRect(parent.transform, "Row_" + labelText.Replace(" ", ""));
        var rowVLG = row.AddComponent<VerticalLayoutGroup>();
        rowVLG.spacing = 1f;
        rowVLG.childForceExpandWidth = true; rowVLG.childForceExpandHeight = false;
        rowVLG.childControlWidth = rowVLG.childControlHeight = true;
        row.AddComponent<LayoutElement>().preferredHeight = 50f;

        var topRow = MakeRect(row.transform, "TopRow");
        var topHLG = topRow.AddComponent<HorizontalLayoutGroup>();
        topHLG.spacing = 6f;
        topHLG.childForceExpandWidth = false; topHLG.childForceExpandHeight = true;
        topHLG.childControlWidth = topHLG.childControlHeight = true;
        topRow.AddComponent<LayoutElement>().preferredHeight = 28f;

        var cbGo = MakeRect(topRow.transform, "Toggle");
        var cbLE = cbGo.AddComponent<LayoutElement>();
        cbLE.preferredWidth = cbLE.preferredHeight = 24f; cbLE.flexibleWidth = 0f;
        var bgImg = cbGo.AddComponent<Image>();
        bgImg.color = new Color(0.14f, 0.14f, 0.14f);
        var toggle = cbGo.AddComponent<Toggle>();
        toggle.targetGraphic = bgImg;

        var checkGo = MakeRect(cbGo.transform, "Checkmark");
        var checkRT = checkGo.GetComponent<RectTransform>();
        checkRT.anchorMin = new Vector2(0.15f, 0.15f);
        checkRT.anchorMax = new Vector2(0.85f, 0.85f);
        checkRT.offsetMin = checkRT.offsetMax = Vector2.zero;
        var checkImg = checkGo.AddComponent<Image>();
        checkImg.color = new Color(0.000f, 0.835f, 1.000f);
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
        go.AddComponent<Image>().color = new Color(0.102f, 0.102f, 0.102f);

        var vp = new GameObject("Viewport");
        vp.AddComponent<RectTransform>();
        vp.transform.SetParent(go.transform, false);
        var vpRT = vp.GetComponent<RectTransform>();
        vpRT.anchorMin = Vector2.zero; vpRT.anchorMax = Vector2.one;
        vpRT.offsetMin = vpRT.offsetMax = Vector2.zero;
        vp.AddComponent<RectMask2D>();

        var c = new GameObject("Content");
        c.AddComponent<RectTransform>();
        c.transform.SetParent(vp.transform, false);
        var cRT = c.GetComponent<RectTransform>();
        cRT.anchorMin = new Vector2(0f, 1f); cRT.anchorMax = new Vector2(1f, 1f);
        cRT.pivot = new Vector2(0.5f, 1f);
        cRT.sizeDelta = cRT.anchoredPosition = Vector2.zero;

        var vlg = c.AddComponent<VerticalLayoutGroup>();
        vlg.spacing = 2f; vlg.padding = new RectOffset(4, 4, 4, 4);
        vlg.childForceExpandWidth = true; vlg.childForceExpandHeight = false;
        vlg.childControlWidth = vlg.childControlHeight = true;
        c.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;

        var scroll = go.AddComponent<ScrollRect>();
        scroll.horizontal = false; scroll.vertical = true;
        scroll.movementType = ScrollRect.MovementType.Clamped;
        scroll.viewport = vpRT; scroll.content = cRT;

        content = cRT;
        return go;
    }
}
