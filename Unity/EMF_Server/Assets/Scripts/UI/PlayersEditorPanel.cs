using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Displays a scrollable list of player rows. Each row has:
///   - Editable name field
///   - Alliance dropdown (hardcoded: Alliance 1 / Alliance 2)
///   - Robot dropdown (None + connected robots; exclusive — one player per robot)
///   - Remove button
///
/// Requires a PlayerRow.prefab (with PlayerRowUI component) assigned to rowPrefab,
/// and a scroll-view Content RectTransform assigned to rowContainer.
/// Run the "Thundergeddon / 4 Wire Players Panel" editor menu item to set these up.
/// </summary>
public class PlayersEditorPanel : MonoBehaviour
{
    [SerializeField] private RectTransform rowContainer;
    [SerializeField] private GameObject    rowPrefab;
    [SerializeField] private Button        addButton;

    private PlayersService   _players;
    private IRobotDirectory  _robots;

    private readonly List<PlayerRowUI> _rows = new List<PlayerRowUI>();
    private bool _rebuilding;
    private bool _suppressRobotEvents;

    // ── Lifecycle ────────────────────────────────────────────────────────────────

    void OnEnable()
    {
        _players = ServiceLocator.Players;
        _robots  = ServiceLocator.RobotDirectory;

        if (_players == null)
        {
            Debug.LogWarning("[PlayersEditorPanel] PlayersService is null; creating defaults.");
            _players = new PlayersService();
            ServiceLocator.Players = _players;
        }
        _players.EnsureDefaults();

        _players.OnChanged -= RebuildRows;
        _players.OnChanged += RebuildRows;

        if (_robots != null)
        {
            _robots.OnRobotAdded   -= OnRobotChanged;
            _robots.OnRobotUpdated -= OnRobotUpdatedHandler;
            _robots.OnRobotRemoved -= OnRobotRemovedHandler;
            _robots.OnRobotAdded   += OnRobotChanged;
            _robots.OnRobotUpdated += OnRobotUpdatedHandler;
            _robots.OnRobotRemoved += OnRobotRemovedHandler;
        }

        if (addButton) addButton.onClick.AddListener(OnAddPlayer);

        RebuildRows();
    }

    void OnDisable()
    {
        if (_players != null) _players.OnChanged -= RebuildRows;

        if (_robots != null)
        {
            _robots.OnRobotAdded   -= OnRobotChanged;
            _robots.OnRobotUpdated -= OnRobotUpdatedHandler;
            _robots.OnRobotRemoved -= OnRobotRemovedHandler;
        }

        if (addButton) addButton.onClick.RemoveListener(OnAddPlayer);
    }

    // ── Robot directory event handlers ───────────────────────────────────────────

    void OnRobotChanged(RobotInfo _)        { if (!_suppressRobotEvents) RefreshAllRobotDropdowns(); }
    void OnRobotUpdatedHandler(RobotInfo _) { if (!_suppressRobotEvents) RefreshAllRobotDropdowns(); }
    void OnRobotRemovedHandler(string _)    { if (!_suppressRobotEvents) RefreshAllRobotDropdowns(); }

    // ── Row management ───────────────────────────────────────────────────────────

    void OnAddPlayer()
    {
        _players.AddPlayer(null, 0);
        // RebuildRows fires automatically via OnChanged
    }

    void RebuildRows()
    {
        if (_rebuilding) return;
        _rebuilding = true;

        foreach (var row in _rows)
            if (row != null && row.gameObject != null) Destroy(row.gameObject);
        _rows.Clear();

        if (rowContainer == null || rowPrefab == null)
        {
            Debug.LogWarning("[PlayersEditorPanel] rowContainer or rowPrefab not set.");
            _rebuilding = false;
            return;
        }

        var players = _players.GetAll();
        for (int i = 0; i < players.Count; i++)
            CreateRow(i, players[i]);

        _rebuilding = false;
    }

    void CreateRow(int index, PlayerInfo player)
    {
        var go = Instantiate(rowPrefab, rowContainer, false);
        go.name = "PlayerRow_" + index;

        var row = go.GetComponent<PlayerRowUI>();
        if (row == null)
        {
            Debug.LogError("[PlayersEditorPanel] PlayerRow prefab is missing a PlayerRowUI component.");
            return;
        }

        // Zebra stripe
        var img = go.GetComponent<Image>();
        if (img) img.color = index % 2 == 0
            ? new Color(0.12f, 0.12f, 0.18f)
            : new Color(0.09f, 0.09f, 0.14f);

        // Populate
        if (row.nameField)
            row.nameField.SetTextWithoutNotify(player.Name);

        if (row.allianceDropdown)
            row.allianceDropdown.SetValueWithoutNotify(Mathf.Clamp(player.AllianceIndex, 0, 1));

        if (row.robotDropdown)
        {
            var opts = BuildRobotOptions(player.Name, out int robotIdx);
            row.robotDropdown.ClearOptions();
            row.robotDropdown.AddOptions(opts);
            row.robotDropdown.SetValueWithoutNotify(robotIdx);
            row.robotDropdown.RefreshShownValue();
        }

        _rows.Add(row);

        // Wire callbacks with captured index
        int ci = index;

        if (row.nameField)
            row.nameField.onValueChanged.AddListener(name =>
            {
                if (_rebuilding) return;
                _players.RenamePlayer(ci, name);
            });

        if (row.allianceDropdown)
            row.allianceDropdown.onValueChanged.AddListener(alliance =>
            {
                if (_rebuilding) return;
                _players.SetPlayerAlliance(ci, alliance, 2);
            });

        if (row.robotDropdown)
            row.robotDropdown.onValueChanged.AddListener(robotIdx =>
            {
                if (_rebuilding) return;
                OnRobotAssigned(ci, robotIdx);
            });

        if (row.removeButton)
            row.removeButton.onClick.AddListener(() =>
            {
                if (_rebuilding) return;
                OnRemovePlayer(ci);
            });
    }

    // ── Robot assignment ─────────────────────────────────────────────────────────

    /// <param name="robotDropdownIndex">0 = "None"; 1+ = robots[index-1]</param>
    void OnRobotAssigned(int playerIndex, int robotDropdownIndex)
    {
        if (_robots == null) return;
        var players = _players.GetAll();
        if (playerIndex >= players.Count) return;

        string playerName = players[playerIndex].Name;

        _suppressRobotEvents = true;

        // Clear every robot currently assigned to this player (ensures 1-robot-per-player)
        foreach (var r in _robots.GetAll())
            if (r.AssignedPlayer == playerName)
                _robots.ClearAssignedPlayer(r.RobotId);

        // Assign the newly selected robot (if not "None")
        if (robotDropdownIndex > 0)
        {
            var allRobots = _robots.GetAll();
            int listIndex = robotDropdownIndex - 1;
            if (listIndex < allRobots.Count)
                _robots.SetAssignedPlayer(allRobots[listIndex].RobotId, playerName);
        }

        _suppressRobotEvents = false;
        RefreshAllRobotDropdowns();
    }

    void OnRemovePlayer(int playerIndex)
    {
        // Free any robot the player was driving
        if (_robots != null)
        {
            var players = _players.GetAll();
            if (playerIndex < players.Count)
            {
                string playerName = players[playerIndex].Name;
                foreach (var r in _robots.GetAll())
                    if (r.AssignedPlayer == playerName)
                        _robots.ClearAssignedPlayer(r.RobotId);
            }
        }
        _players.RemovePlayerAt(playerIndex);
        // RebuildRows fires automatically via OnChanged
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    /// Builds the option list for a robot dropdown.
    /// "None" is always index 0; robots follow in RobotDirectory order.
    List<string> BuildRobotOptions(string playerName, out int selectedIndex)
    {
        var options = new List<string> { "None" };
        selectedIndex = 0;

        if (_robots == null) return options;

        var robots = _robots.GetAll();
        for (int i = 0; i < robots.Count; i++)
        {
            string label = string.IsNullOrEmpty(robots[i].Callsign)
                ? robots[i].RobotId
                : robots[i].Callsign;
            options.Add(label);

            if (robots[i].AssignedPlayer == playerName)
                selectedIndex = i + 1;
        }

        return options;
    }

    void RefreshAllRobotDropdowns()
    {
        var players = _players.GetAll();
        bool prev = _rebuilding;
        _rebuilding = true;

        for (int i = 0; i < _rows.Count && i < players.Count; i++)
        {
            var dd = _rows[i]?.robotDropdown;
            if (dd == null) continue;

            var opts = BuildRobotOptions(players[i].Name, out int sel);
            dd.ClearOptions();
            dd.AddOptions(opts);
            dd.SetValueWithoutNotify(sel);
            dd.RefreshShownValue();
        }

        _rebuilding = prev;
    }

    public void Show() { gameObject.SetActive(true); }
    public void Hide() { gameObject.SetActive(false); }
}
