using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Displays the live roster grouped by alliance:
///   Alliance 1 header
///     [PlayerName]  [RobotCallsign]  [===HP BAR===]  75
/// Replaces RobotHpPanel. Subscribes to GameService.OnHpChanged/OnRobotDied.
/// </summary>
public class TeamRosterPanel : MonoBehaviour
{
    [SerializeField] public RectTransform rowContainer;

    static readonly Color C_BLUE    = new Color(0.29f, 0.62f, 1.00f);
    static readonly Color C_RED     = new Color(1.00f, 0.42f, 0.21f);
    static readonly Color C_DIM     = new Color(0.40f, 0.40f, 0.40f);
    static readonly Color C_HDR_BG  = new Color(0.07f, 0.07f, 0.07f);

    private GameService     _game;
    private IRobotDirectory _dir;
    private PlayersService  _players;

    private readonly Dictionary<string, (RectTransform fillRT, Image fillImg, TextMeshProUGUI hpLbl)>
        _rows = new Dictionary<string, (RectTransform, Image, TextMeshProUGUI)>();

    private GameFlow _flow;

    private void OnEnable()
    {
        _game    = ServiceLocator.Game;
        _dir     = ServiceLocator.RobotDirectory;
        _players = ServiceLocator.Players;
        _flow    = ServiceLocator.GameFlow;

        if (_game != null)
        {
            _game.OnHpChanged += HandleHpChanged;
            _game.OnRobotDied += HandleRobotDied;
        }
        if (_flow != null)
            _flow.OnPhaseChanged += HandlePhaseChanged;

        RebuildRows();
    }

    private void OnDisable()
    {
        if (_game != null)
        {
            _game.OnHpChanged -= HandleHpChanged;
            _game.OnRobotDied -= HandleRobotDied;
        }
        if (_flow != null)
            _flow.OnPhaseChanged -= HandlePhaseChanged;
    }

    private void HandlePhaseChanged(GamePhase phase)
    {
        if (phase == GamePhase.Playing)
            RebuildRows();
    }

    public void RebuildRows()
    {
        _rows.Clear();

        if (rowContainer == null) return;

        for (int i = rowContainer.childCount - 1; i >= 0; i--)
            Destroy(rowContainer.GetChild(i).gameObject);

        var state    = _game?.State;
        var settings = ServiceLocator.GameSettings;
        int maxHp    = settings != null ? settings.MaxHp : 100;
        var players  = _players?.GetAll();

        if (players == null || players.Count == 0) return;

        var byAlliance = new Dictionary<int, List<PlayerInfo>>();
        foreach (var p in players)
        {
            if (!byAlliance.ContainsKey(p.AllianceIndex))
                byAlliance[p.AllianceIndex] = new List<PlayerInfo>();
            byAlliance[p.AllianceIndex].Add(p);
        }

        for (int a = 0; a < 2; a++)
        {
            if (!byAlliance.ContainsKey(a) || byAlliance[a].Count == 0) continue;
            AddAllianceHeader(a);
            foreach (var player in byAlliance[a])
            {
                var robot = FindRobotForPlayer(player.Name);
                int hp    = 0;
                bool dead = false;
                if (robot != null && state != null)
                {
                    hp   = state.RobotHp.GetValueOrDefault(robot.RobotId, maxHp);
                    dead = state.DeadRobots.Contains(robot.RobotId);
                }
                else if (state == null && robot != null)
                {
                    hp = maxHp;
                }
                AddPlayerRow(player, robot, hp, maxHp, dead, a);
            }
        }

        LayoutRebuilder.ForceRebuildLayoutImmediate(rowContainer);
    }

    private RobotInfo FindRobotForPlayer(string playerName)
    {
        if (_dir == null || string.IsNullOrEmpty(playerName)) return null;
        foreach (var r in _dir.GetAll())
            if (r.AssignedPlayer == playerName) return r;
        return null;
    }

    private void AddAllianceHeader(int a)
    {
        var go = new GameObject("AllianceHeader" + a);
        go.AddComponent<RectTransform>();
        go.transform.SetParent(rowContainer, false);
        go.AddComponent<Image>().color = C_HDR_BG;
        go.AddComponent<LayoutElement>().preferredHeight = 24f;

        var lbl = new GameObject("Label");
        lbl.AddComponent<RectTransform>();
        lbl.transform.SetParent(go.transform, false);

        var tmp = lbl.AddComponent<TextMeshProUGUI>();
        tmp.text             = a == 0 ? "ALLIANCE 1" : "ALLIANCE 2";
        tmp.color            = a == 0 ? C_BLUE : C_RED;
        tmp.fontSize         = 10f;
        tmp.fontStyle        = FontStyles.Bold;
        tmp.alignment        = TextAlignmentOptions.MidlineLeft;
        tmp.characterSpacing = 1.5f;

        var rt = lbl.GetComponent<RectTransform>();
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = new Vector2(8f, 0f);
        rt.offsetMax = Vector2.zero;
    }

    private void AddPlayerRow(PlayerInfo player, RobotInfo robot,
                               int hp, int maxHp, bool dead, int allianceIndex)
    {
        var row = new GameObject("Row_" + player.Name);
        row.AddComponent<RectTransform>();
        row.transform.SetParent(rowContainer, false);
        row.AddComponent<LayoutElement>().preferredHeight = 30f;

        var hlg = row.AddComponent<HorizontalLayoutGroup>();
        hlg.spacing             = 6f;
        hlg.padding             = new RectOffset(8, 8, 2, 2);
        hlg.childForceExpandWidth  = false;
        hlg.childForceExpandHeight = true;
        hlg.childControlWidth      = true;
        hlg.childControlHeight     = true;
        hlg.childAlignment         = TextAnchor.MiddleLeft;

        // Player name
        var playerLE = MakeTextCell(row.transform, "PlayerName", player.Name,
                                    dead ? C_DIM : Color.white, 13f, bold: true);
        playerLE.preferredWidth = 90f;

        // Robot callsign
        string cs = robot != null
            ? (string.IsNullOrEmpty(robot.Callsign) ? robot.RobotId.Substring(0, Mathf.Min(6, robot.RobotId.Length)) : robot.Callsign)
            : "—";
        var csLE = MakeTextCell(row.transform, "Callsign", cs, C_DIM, 11f);
        csLE.preferredWidth = 80f;

        // HP bar background
        var barBG = new GameObject("BarBG");
        barBG.AddComponent<RectTransform>();
        barBG.transform.SetParent(row.transform, false);
        barBG.AddComponent<Image>().color = new Color(0.07f, 0.07f, 0.07f);
        barBG.AddComponent<LayoutElement>().flexibleWidth = 1f;

        // HP bar fill
        var fill = new GameObject("Fill");
        var fillRT = fill.AddComponent<RectTransform>();
        fill.transform.SetParent(barBG.transform, false);
        var fillImg = fill.AddComponent<Image>();

        float pct = maxHp > 0 ? Mathf.Clamp01((float)hp / maxHp) : 0f;
        fillImg.color = dead ? new Color(0.27f, 0.27f, 0.27f) : BarColor(pct, allianceIndex);

        fillRT.anchorMin = Vector2.zero;
        fillRT.anchorMax = new Vector2(pct, 1f);
        fillRT.pivot     = new Vector2(0f, 0.5f);
        fillRT.offsetMin = Vector2.zero;
        fillRT.offsetMax = Vector2.zero;

        // HP number
        string hpText = dead ? "DEAD" : $"{hp}";
        var hpLE = MakeTextCell(row.transform, "HpNum", hpText, C_DIM, 11f);
        hpLE.GetComponentInChildren<TextMeshProUGUI>().alignment = TextAlignmentOptions.MidlineRight;
        hpLE.preferredWidth = 50f;

        if (robot != null)
            _rows[robot.RobotId] = (fillRT, fillImg, hpLE.GetComponentInChildren<TextMeshProUGUI>());
    }

    // Returns the LayoutElement on the new text cell parent
    private LayoutElement MakeTextCell(Transform parent, string name, string text,
                                        Color color, float size, bool bold = false)
    {
        var go = new GameObject(name);
        go.AddComponent<RectTransform>();
        go.transform.SetParent(parent, false);
        var le = go.AddComponent<LayoutElement>();

        var tmp = go.AddComponent<TextMeshProUGUI>();
        tmp.text      = text;
        tmp.color     = color;
        tmp.fontSize  = size;
        tmp.alignment = TextAlignmentOptions.MidlineLeft;
        if (bold) tmp.fontStyle = FontStyles.Bold;

        return le;
    }

    private static Color BarColor(float pct, int alliance)
    {
        Color full = alliance == 0 ? C_BLUE : C_RED;
        return Color.Lerp(new Color(1f, 0.2f, 0.2f), full, pct);
    }

    private void HandleHpChanged(string robotId, int newHp)
    {
        if (!_rows.TryGetValue(robotId, out var row)) return;
        var settings = ServiceLocator.GameSettings;
        int maxHp    = settings != null ? settings.MaxHp : 100;
        float pct = maxHp > 0 ? Mathf.Clamp01((float)newHp / maxHp) : 0f;

        int alliance = 0;
        if (_dir != null && _dir.TryGet(robotId, out var info) && _players != null)
            foreach (var p in _players.GetAll())
                if (p.Name == info.AssignedPlayer) { alliance = p.AllianceIndex; break; }

        row.fillRT.anchorMax  = new Vector2(pct, 1f);
        row.fillImg.color     = BarColor(pct, alliance);
        row.hpLbl.text        = $"{newHp}";
    }

    private void HandleRobotDied(string robotId)
    {
        if (!_rows.TryGetValue(robotId, out var row)) return;
        row.fillRT.anchorMax  = new Vector2(0f, 1f);
        row.fillImg.color     = new Color(0.27f, 0.27f, 0.27f);
        row.hpLbl.text        = "DEAD";
        row.hpLbl.color       = C_DIM;
    }
}
