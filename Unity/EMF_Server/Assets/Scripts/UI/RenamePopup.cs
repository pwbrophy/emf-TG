using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

// RenamePopup - small modal to edit a robot's name and assigned player.
public class RenamePopup : MonoBehaviour
{
    [SerializeField] private TMP_InputField nameInput;
    [SerializeField] private TMP_Dropdown playerDropdown;
    [SerializeField] private Button okButton;
    [SerializeField] private Button cancelButton;

    private string _robotId;
    private Action<string, string> _onApplyLegacy;
    private Action<string, string, string> _onApplyWithName;
    private PlayersService _players;
    private bool _updating = false;

    // ------------------------------------------------------------------------
    // PUBLIC API (NEW preferred)
    // ------------------------------------------------------------------------

    public void Show(string robotId, string currentName, string currentAssignedPlayer, Action<string, string, string> onApply)
    {
        _robotId = robotId;
        _onApplyWithName = onApply;
        _onApplyLegacy = null;

        if (ServiceLocator.Players == null) ServiceLocator.Players = new PlayersService();
        _players = ServiceLocator.Players;

        _players.OnChanged -= HandlePlayersChanged;
        _players.OnChanged += HandlePlayersChanged;

        okButton.onClick.RemoveListener(OnOkClicked);
        okButton.onClick.AddListener(OnOkClicked);
        cancelButton.onClick.RemoveListener(OnCancelClicked);
        cancelButton.onClick.AddListener(OnCancelClicked);

        _updating = true;
        BuildPlayersDropdown();
        nameInput.SetTextWithoutNotify(currentName ?? "");
        SelectPlayerByNameWithoutNotify(currentAssignedPlayer);
        _updating = false;

        gameObject.SetActive(true);
    }

    public void Open(string robotId, string currentName, string currentAssignedPlayer, Action<string, string, string> onApply)
    {
        Show(robotId, currentName, currentAssignedPlayer, onApply);
    }

    // ------------------------------------------------------------------------
    // PUBLIC API (LEGACY compatibility)
    // ------------------------------------------------------------------------

    public void Show(string robotId, string currentName, string currentAssignedPlayer, Action<string, string> onApplyLegacy)
    {
        _robotId = robotId;
        _onApplyLegacy = onApplyLegacy;
        _onApplyWithName = null;

        if (ServiceLocator.Players == null) ServiceLocator.Players = new PlayersService();
        _players = ServiceLocator.Players;

        _players.OnChanged -= HandlePlayersChanged;
        _players.OnChanged += HandlePlayersChanged;

        okButton.onClick.RemoveListener(OnOkClicked);
        okButton.onClick.AddListener(OnOkClicked);
        cancelButton.onClick.RemoveListener(OnCancelClicked);
        cancelButton.onClick.AddListener(OnCancelClicked);

        _updating = true;
        BuildPlayersDropdown();
        nameInput.SetTextWithoutNotify(currentName ?? "");
        SelectPlayerByNameWithoutNotify(currentAssignedPlayer);
        _updating = false;

        gameObject.SetActive(true);
    }

    public void Open(string robotId, string currentName, string currentAssignedPlayer, Action<string, string> onApplyLegacy)
    {
        Show(robotId, currentName, currentAssignedPlayer, onApplyLegacy);
    }

    public void Open(RobotInfo info, Action<string, string, string> onApply)
    {
        if (info == null) return;
        string id = info.RobotId;
        string name = string.IsNullOrEmpty(info.Callsign) ? info.RobotId : info.Callsign;
        string assignedPlayer = string.IsNullOrEmpty(info.AssignedPlayer) ? null : info.AssignedPlayer;
        Show(id, name, assignedPlayer, onApply);
    }

    // ------------------------------------------------------------------------
    // Close / cleanup
    // ------------------------------------------------------------------------

    public void Hide()
    {
        if (_players != null) _players.OnChanged -= HandlePlayersChanged;
        gameObject.SetActive(false);
    }

    // ------------------------------------------------------------------------
    // Internals
    // ------------------------------------------------------------------------

    private void BuildPlayersDropdown()
    {
        var options = new List<TMP_Dropdown.OptionData>();
        options.Add(new TMP_Dropdown.OptionData("Unassigned"));

        var list = _players != null ? _players.GetAll() : new List<PlayerInfo>();

        for (int i = 0; i < list.Count; i++)
        {
            string label = string.IsNullOrEmpty(list[i].Name) ? $"Player{i + 1}" : list[i].Name;
            options.Add(new TMP_Dropdown.OptionData(label));
        }

        playerDropdown.ClearOptions();
        playerDropdown.AddOptions(options);
        playerDropdown.SetValueWithoutNotify(0);
        playerDropdown.RefreshShownValue();
    }

    private void SelectPlayerByNameWithoutNotify(string playerNameOrNull)
    {
        if (string.IsNullOrEmpty(playerNameOrNull))
        {
            playerDropdown.SetValueWithoutNotify(0);
            playerDropdown.RefreshShownValue();
            return;
        }

        for (int i = 1; i < playerDropdown.options.Count; i++)
        {
            if (string.Equals(playerDropdown.options[i].text, playerNameOrNull, StringComparison.Ordinal))
            {
                playerDropdown.SetValueWithoutNotify(i);
                playerDropdown.RefreshShownValue();
                return;
            }
        }

        playerDropdown.SetValueWithoutNotify(0);
        playerDropdown.RefreshShownValue();
    }

    private string GetSelectedPlayerNameOrNull()
    {
        if (playerDropdown.value == 0) return null;
        return playerDropdown.options[playerDropdown.value].text;
    }

    private void HandlePlayersChanged()
    {
        _updating = true;
        string selected = GetSelectedPlayerNameOrNull();
        BuildPlayersDropdown();
        SelectPlayerByNameWithoutNotify(selected);
        _updating = false;
    }

    private void OnOkClicked()
    {
        if (_updating) return;

        string newName = nameInput != null ? nameInput.text.Trim() : "";
        string playerOrNull = GetSelectedPlayerNameOrNull();

        if (_onApplyWithName != null)
            _onApplyWithName.Invoke(_robotId, newName, playerOrNull);
        else
            _onApplyLegacy?.Invoke(_robotId, playerOrNull);

        Hide();
    }

    private void OnCancelClicked()
    {
        if (_updating) return;
        Hide();
    }
}
