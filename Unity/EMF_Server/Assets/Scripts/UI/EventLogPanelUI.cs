using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Scrolling event log for the PlayingPanel. Shows the last 4 game events
/// (robot deaths, captures, game over). Newest line is white+bold; older lines dim.
/// </summary>
public class EventLogPanelUI : MonoBehaviour
{
    [SerializeField] public RectTransform container;

    static readonly Color C_NEW = Color.white;
    static readonly Color C_OLD = new Color(0.55f, 0.55f, 0.55f);

    private GameService         _game;
    private CapturePointService _cp;

    private readonly List<string>     _lines  = new List<string>();
    private readonly List<GameObject> _lineGOs = new List<GameObject>();

    private void OnEnable()
    {
        _game = ServiceLocator.Game;
        _cp   = ServiceLocator.CapturePoints;

        if (_game != null)
        {
            _game.OnRobotDied += HandleRobotDied;
            _game.OnGameWon   += HandleGameWon;
        }
        if (_cp != null)
            _cp.OnPointCaptured += HandlePointCaptured;

        _lines.Clear();
        AddEvent("Waiting for action\u2026");
    }

    private void OnDisable()
    {
        if (_game != null)
        {
            _game.OnRobotDied -= HandleRobotDied;
            _game.OnGameWon   -= HandleGameWon;
        }
        if (_cp != null)
            _cp.OnPointCaptured -= HandlePointCaptured;
    }

    public void AddEvent(string text)
    {
        _lines.Insert(0, text);
        if (_lines.Count > 4) _lines.RemoveAt(_lines.Count - 1);
        RefreshUI();
    }

    private void RefreshUI()
    {
        if (container == null) return;

        foreach (var go in _lineGOs)
            if (go != null) Destroy(go);
        _lineGOs.Clear();

        for (int i = 0; i < _lines.Count; i++)
        {
            var go = new GameObject("Line" + i);
            go.AddComponent<RectTransform>();
            go.transform.SetParent(container, false);
            go.AddComponent<LayoutElement>().preferredHeight = 18f;

            var tmp = go.AddComponent<TextMeshProUGUI>();
            tmp.text         = _lines[i];
            tmp.fontSize     = 11f;
            tmp.color        = i == 0 ? C_NEW : C_OLD;
            tmp.fontStyle    = i == 0 ? FontStyles.Bold : FontStyles.Normal;
            tmp.alignment    = TextAlignmentOptions.MidlineLeft;
            tmp.overflowMode = TextOverflowModes.Ellipsis;

            _lineGOs.Add(go);
        }
    }

    private void HandleRobotDied(string robotId)
    {
        string callsign = robotId;
        var dir = ServiceLocator.RobotDirectory;
        if (dir != null && dir.TryGet(robotId, out var info) && !string.IsNullOrEmpty(info.Callsign))
            callsign = info.Callsign;
        AddEvent($"{callsign} destroyed!");
    }

    private void HandleGameWon(int allianceIndex, string reason)
    {
        string winner = allianceIndex >= 0 ? $"Alliance {allianceIndex + 1}" : "Nobody";
        AddEvent($"{winner} wins! ({reason})");
    }

    private void HandlePointCaptured(int pointIndex, int allianceIndex, string pointName)
    {
        AddEvent($"Alliance {allianceIndex + 1} captures {pointName}!");
    }
}
