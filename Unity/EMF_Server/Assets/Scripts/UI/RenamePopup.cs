using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

// RenamePopup — modal for renaming a robot.
// playerDropdown is optional; if null the popup is rename-only.
public class RenamePopup : MonoBehaviour
{
    [SerializeField] private TMP_InputField nameInput;
    [SerializeField] private TMP_Dropdown   playerDropdown; // optional
    [SerializeField] private Button okButton;
    [SerializeField] private Button cancelButton;

    private string _robotId;
    private Action<string, string, string> _onApply;

    // ------------------------------------------------------------------------
    // PUBLIC API
    // ------------------------------------------------------------------------

    public void Open(string robotId, string currentName, string currentAssignedPlayer,
                     Action<string, string, string> onApply)
    {
        _robotId  = robotId;
        _onApply  = onApply;

        nameInput.SetTextWithoutNotify(currentName ?? "");

        if (playerDropdown != null)
        {
            BuildPlayersDropdown();
            SelectPlayerByNameWithoutNotify(currentAssignedPlayer);
        }

        okButton.onClick.RemoveAllListeners();
        okButton.onClick.AddListener(OnOkClicked);

        cancelButton.onClick.RemoveAllListeners();
        cancelButton.onClick.AddListener(OnCancelClicked);

        nameInput.onSubmit.RemoveAllListeners();
        nameInput.onSubmit.AddListener(_ => OnOkClicked());

        gameObject.SetActive(true);
        nameInput.ActivateInputField();
        nameInput.Select();
    }

    // Legacy overloads kept for any existing callers.
    public void Open(string robotId, string currentName, string currentAssignedPlayer,
                     Action<string, string> onApplyLegacy)
    {
        Open(robotId, currentName, currentAssignedPlayer,
             (id, name, player) => onApplyLegacy?.Invoke(id, player));
    }

    public void Open(RobotInfo info, Action<string, string, string> onApply)
    {
        if (info == null) return;
        string name   = string.IsNullOrEmpty(info.Callsign) ? info.RobotId : info.Callsign;
        string player = string.IsNullOrEmpty(info.AssignedPlayer) ? null : info.AssignedPlayer;
        Open(info.RobotId, name, player, onApply);
    }

    public void Show(string robotId, string currentName, string currentAssignedPlayer,
                     Action<string, string, string> onApply)
        => Open(robotId, currentName, currentAssignedPlayer, onApply);

    public void Show(string robotId, string currentName, string currentAssignedPlayer,
                     Action<string, string> onApplyLegacy)
        => Open(robotId, currentName, currentAssignedPlayer, onApplyLegacy);

    public void Hide()
    {
        gameObject.SetActive(false);
    }

    // ------------------------------------------------------------------------
    // Internals
    // ------------------------------------------------------------------------

    private void BuildPlayersDropdown()
    {
        if (playerDropdown == null) return;
        var players = ServiceLocator.Players?.GetAll();
        var options = new System.Collections.Generic.List<TMP_Dropdown.OptionData>();
        options.Add(new TMP_Dropdown.OptionData("Unassigned"));
        if (players != null)
            foreach (var p in players)
                options.Add(new TMP_Dropdown.OptionData(
                    string.IsNullOrEmpty(p.Name) ? "Player" : p.Name));
        playerDropdown.ClearOptions();
        playerDropdown.AddOptions(options);
        playerDropdown.SetValueWithoutNotify(0);
        playerDropdown.RefreshShownValue();
    }

    private void SelectPlayerByNameWithoutNotify(string playerName)
    {
        if (playerDropdown == null || string.IsNullOrEmpty(playerName)) return;
        for (int i = 1; i < playerDropdown.options.Count; i++)
        {
            if (string.Equals(playerDropdown.options[i].text, playerName, StringComparison.Ordinal))
            {
                playerDropdown.SetValueWithoutNotify(i);
                playerDropdown.RefreshShownValue();
                return;
            }
        }
    }

    private string GetSelectedPlayer()
    {
        if (playerDropdown == null || playerDropdown.value == 0) return null;
        return playerDropdown.options[playerDropdown.value].text;
    }

    private void OnOkClicked()
    {
        string newName = nameInput != null ? nameInput.text.Trim() : "";
        if (string.IsNullOrEmpty(newName)) return;
        _onApply?.Invoke(_robotId, newName, GetSelectedPlayer());
        Hide();
    }

    private void OnCancelClicked()
    {
        Hide();
    }
}
