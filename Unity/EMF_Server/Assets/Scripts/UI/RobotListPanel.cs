using System;
using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;

/// <summary>
/// Robot-centric list for the Playing panel.  Replaces TeamRosterPanel.
/// Shows every robot in the current match with name, HP bar, and 8-node IR compass.
/// Click a row to select it; exposes CurrentRobotId + SelectionChanged (matching
/// RobotSelectionPanel's interface so existing buttons work without changes).
/// </summary>
public class RobotListPanel : MonoBehaviour
{
    [SerializeField] public RectTransform rowContainer;

    public string CurrentRobotId { get; private set; }
    public event Action<string> SelectionChanged;

    // ── Colours ───────────────────────────────────────────────────────────────
    static readonly Color C_SEL    = new Color(0f, 0.7f, 1f, 0.15f);
    static readonly Color C_UNSEL  = Color.clear;
    static readonly Color C_BLUE   = new Color(0.29f, 0.62f, 1.00f);
    static readonly Color C_RED    = new Color(1.00f, 0.42f, 0.21f);
    static readonly Color C_DIM    = new Color(0.40f, 0.40f, 0.40f);
    static readonly Color C_DEAD   = new Color(0.27f, 0.27f, 0.27f);

    // ── Per-row data ──────────────────────────────────────────────────────────
    private sealed class RowData
    {
        public string          RobotId;
        public RectTransform   FillRT;
        public Image           FillImg;
        public TextMeshProUGUI HpLabel;
        public IrCompassWidget Compass;
        public Image           Highlight;
        public int             Alliance; // 0 or 1 (resolved at build time)
    }
    private readonly List<RowData> _rows = new List<RowData>();

    // ── Services ──────────────────────────────────────────────────────────────
    private GameService     _game;
    private GameFlow        _flow;
    private IRobotDirectory _dir;
    private PlayersService  _players;

    // ─────────────────────────────────────────────────────────────────────────

    private void OnEnable()
    {
        _game    = ServiceLocator.Game;
        _flow    = ServiceLocator.GameFlow;
        _dir     = ServiceLocator.RobotDirectory;
        _players = ServiceLocator.Players;

        if (_game != null)
        {
            _game.OnHpChanged         += HandleHpChanged;
            _game.OnRobotDied         += HandleRobotDied;
            _game.OnRobotRespawned    += HandleRobotRespawned;
            _game.OnRobotHitDirection += HandleHitDirection;
        }
        if (_flow != null)
            _flow.OnPhaseChanged += HandlePhaseChanged;

        StartCoroutine(RebuildNextFrame());
    }

    private IEnumerator RebuildNextFrame()
    {
        yield return null;
        RebuildRows();
    }

    private void OnDisable()
    {
        if (_game != null)
        {
            _game.OnHpChanged         -= HandleHpChanged;
            _game.OnRobotDied         -= HandleRobotDied;
            _game.OnRobotRespawned    -= HandleRobotRespawned;
            _game.OnRobotHitDirection -= HandleHitDirection;
        }
        if (_flow != null)
            _flow.OnPhaseChanged -= HandlePhaseChanged;
    }

    // ── Rebuild ───────────────────────────────────────────────────────────────

    public void RebuildRows()
    {
        _rows.Clear();
        if (rowContainer == null) return;

        for (int i = rowContainer.childCount - 1; i >= 0; i--)
            Destroy(rowContainer.GetChild(i).gameObject);

        var state    = _game?.State;
        var settings = ServiceLocator.GameSettings;
        int maxHp    = settings != null ? settings.MaxHp : 100;

        if (state == null) return;

        foreach (var r in state.Robots)
        {
            int  hp      = state.RobotHp.GetValueOrDefault(r.RobotId, maxHp);
            bool dead    = state.DeadRobots.Contains(r.RobotId);
            int  alliance = ResolveAlliance(r);
            CreateRow(r, hp, maxHp, dead, alliance);
        }

        LayoutRebuilder.ForceRebuildLayoutImmediate(rowContainer);
        Canvas.ForceUpdateCanvases();
    }

    private void CreateRow(RobotInfo r, int hp, int maxHp, bool dead, int alliance)
    {
        var font = Resources.Load<TMPro.TMP_FontAsset>("Fonts & Materials/LiberationSans SDF");

        // ── Row root (Button + transparent Image for raycast) ─────────────────
        var row = new GameObject("Row_" + r.RobotId);
        var rowRT = row.AddComponent<RectTransform>();
        row.transform.SetParent(rowContainer, false);
        row.AddComponent<LayoutElement>().preferredHeight = 42f;

        // Subtle background — also required by Button.targetGraphic
        var rowBg = row.AddComponent<Image>();
        rowBg.color = new Color(0.12f, 0.12f, 0.12f);

        var btn = row.AddComponent<Button>();
        btn.targetGraphic = rowBg;
        var nav = btn.navigation;
        nav.mode = UnityEngine.UI.Navigation.Mode.None;
        btn.navigation = nav;

        var hlg = row.AddComponent<HorizontalLayoutGroup>();
        hlg.spacing             = 5f;
        hlg.padding             = new RectOffset(6, 6, 2, 2);
        hlg.childForceExpandWidth  = false;
        hlg.childForceExpandHeight = true;
        hlg.childControlWidth      = true;
        hlg.childControlHeight     = true;
        hlg.childAlignment         = TextAnchor.MiddleLeft;

        // ── Highlight overlay (full-fill, rendered under HLG items) ──────────
        var hlGO = new GameObject("Highlight");
        var hlRT = hlGO.AddComponent<RectTransform>();
        hlGO.transform.SetParent(row.transform, false);
        hlGO.transform.SetSiblingIndex(0);   // behind HLG content
        var hlImg = hlGO.AddComponent<Image>();
        hlImg.color         = C_UNSEL;
        hlImg.raycastTarget = false;
        hlRT.anchorMin  = Vector2.zero;
        hlRT.anchorMax  = Vector2.one;
        hlRT.offsetMin  = Vector2.zero;
        hlRT.offsetMax  = Vector2.zero;
        var hlLE = hlGO.AddComponent<LayoutElement>();
        hlLE.ignoreLayout = true;

        // ── Callsign label ────────────────────────────────────────────────────
        string name = string.IsNullOrEmpty(r.Callsign)
            ? r.RobotId.Substring(0, Mathf.Min(8, r.RobotId.Length))
            : r.Callsign;

        var nameGO = new GameObject("Name");
        nameGO.AddComponent<RectTransform>();
        nameGO.transform.SetParent(row.transform, false);
        var nameTmp = nameGO.AddComponent<TextMeshProUGUI>();
        nameTmp.text      = name;
        nameTmp.font      = font;
        nameTmp.fontSize  = 12f;
        nameTmp.fontStyle = FontStyles.Bold;
        nameTmp.color     = dead ? C_DIM : Color.white;
        nameTmp.alignment = TextAlignmentOptions.MidlineLeft;
        nameGO.AddComponent<LayoutElement>().preferredWidth = 80f;

        // ── HP bar ────────────────────────────────────────────────────────────
        var barBG = new GameObject("BarBG");
        barBG.AddComponent<RectTransform>();
        barBG.transform.SetParent(row.transform, false);
        barBG.AddComponent<Image>().color = new Color(0.07f, 0.07f, 0.07f);
        barBG.AddComponent<LayoutElement>().flexibleWidth = 1f;

        var fill = new GameObject("Fill");
        var fillRT = fill.AddComponent<RectTransform>();
        fill.transform.SetParent(barBG.transform, false);
        var fillImg = fill.AddComponent<Image>();

        float pct = maxHp > 0 ? Mathf.Clamp01((float)hp / maxHp) : 0f;
        fillImg.color = dead ? C_DEAD : BarColor(pct, alliance);

        fillRT.anchorMin = Vector2.zero;
        fillRT.anchorMax = new Vector2(pct, 1f);
        fillRT.pivot     = new Vector2(0f, 0.5f);
        fillRT.offsetMin = Vector2.zero;
        fillRT.offsetMax = Vector2.zero;

        // ── HP number ─────────────────────────────────────────────────────────
        var hpGO = new GameObject("HpNum");
        hpGO.AddComponent<RectTransform>();
        hpGO.transform.SetParent(row.transform, false);
        var hpTmp = hpGO.AddComponent<TextMeshProUGUI>();
        hpTmp.text      = dead ? "DEAD" : $"{hp}";
        hpTmp.font      = font;
        hpTmp.fontSize  = 11f;
        hpTmp.color     = C_DIM;
        hpTmp.alignment = TextAlignmentOptions.MidlineRight;
        var hpLE = hpGO.AddComponent<LayoutElement>();
        hpLE.preferredWidth = 36f;

        // ── IR compass widget ─────────────────────────────────────────────────
        var compassGO = new GameObject("IrCompass");
        compassGO.AddComponent<RectTransform>();
        compassGO.transform.SetParent(row.transform, false);
        var widget = compassGO.AddComponent<IrCompassWidget>();
        var compassLE = compassGO.AddComponent<LayoutElement>();
        compassLE.preferredWidth  = 36f;
        compassLE.preferredHeight = 36f;
        compassLE.flexibleWidth   = 0f;

        // ── Store row data ────────────────────────────────────────────────────
        var data = new RowData
        {
            RobotId   = r.RobotId,
            FillRT    = fillRT,
            FillImg   = fillImg,
            HpLabel   = hpTmp,
            Compass   = widget,
            Highlight = hlImg,
            Alliance  = alliance,
        };
        _rows.Add(data);

        // Wire click — capture robotId by value
        string robotId = r.RobotId;
        btn.onClick.AddListener(() => SelectRobot(robotId));
    }

    // ── Selection ─────────────────────────────────────────────────────────────

    public void SelectRobot(string robotId)
    {
        if (CurrentRobotId == robotId) return;
        foreach (var r in _rows) r.Highlight.color = C_UNSEL;
        CurrentRobotId = robotId;
        foreach (var r in _rows)
            if (r.RobotId == robotId) r.Highlight.color = C_SEL;
        SelectionChanged?.Invoke(robotId);
    }

    // ── Event handlers ────────────────────────────────────────────────────────

    private void HandleHpChanged(string robotId, int newHp)
    {
        var row = FindRow(robotId);
        if (row == null) return;

        var settings = ServiceLocator.GameSettings;
        int maxHp = settings != null ? settings.MaxHp : 100;
        float pct = maxHp > 0 ? Mathf.Clamp01((float)newHp / maxHp) : 0f;

        row.FillRT.anchorMax = new Vector2(pct, 1f);
        row.FillImg.color    = BarColor(pct, row.Alliance);
        row.HpLabel.text     = $"{newHp}";
        row.HpLabel.color    = C_DIM;
    }

    private void HandleRobotDied(string robotId)
    {
        var row = FindRow(robotId);
        if (row == null) return;
        row.FillRT.anchorMax = new Vector2(0f, 1f);
        row.FillImg.color    = C_DEAD;
        row.HpLabel.text     = "DEAD";
    }

    private void HandleRobotRespawned(string robotId)
    {
        var settings = ServiceLocator.GameSettings;
        int maxHp = settings != null ? settings.MaxHp : 100;

        // Re-resolve alliance in case player assignment changed
        var row = FindRow(robotId);
        if (row != null && _dir != null && _dir.TryGet(robotId, out var info))
            row.Alliance = ResolveAllianceFromName(info.AssignedPlayer);

        HandleHpChanged(robotId, maxHp);
    }

    private void HandleHitDirection(string robotId, string direction)
    {
        FindRow(robotId)?.Compass.Flash(direction);
    }

    private void HandlePhaseChanged(GamePhase phase)
    {
        if (phase == GamePhase.Playing)
            StartCoroutine(RebuildNextFrame());
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private RowData FindRow(string robotId)
    {
        foreach (var r in _rows)
            if (r.RobotId == robotId) return r;
        return null;
    }

    private int ResolveAlliance(RobotInfo robot)
        => ResolveAllianceFromName(robot.AssignedPlayer);

    private int ResolveAllianceFromName(string playerName)
    {
        if (_players == null || string.IsNullOrEmpty(playerName)) return 0;
        foreach (var p in _players.GetAll())
            if (p.Name == playerName) return p.AllianceIndex;
        return 0;
    }

    private static Color BarColor(float pct, int alliance)
    {
        Color full = alliance == 0 ? C_BLUE : C_RED;
        return Color.Lerp(new Color(1f, 0.2f, 0.2f), full, pct);
    }
}
