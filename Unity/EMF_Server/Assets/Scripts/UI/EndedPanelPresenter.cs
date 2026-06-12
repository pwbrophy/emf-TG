using TMPro;
using UnityEngine;

/// <summary>
/// Shows match result in the Ended panel.
/// Subscribes to GameFlow.OnPhaseChanged and reads GameState for winner info.
/// </summary>
public class EndedPanelPresenter : MonoBehaviour
{
    [SerializeField] private TextMeshProUGUI resultLabel;

    private GameFlow _flow;

    private void OnEnable()
    {
        _flow = ServiceLocator.GameFlow;
        if (_flow != null)
            _flow.OnPhaseChanged += HandlePhaseChanged;

        // Refresh immediately in case we enable after Ended phase was set
        if (_flow != null && _flow.Phase == GamePhase.Ended)
            ShowResult();
    }

    private void OnDisable()
    {
        if (_flow != null)
            _flow.OnPhaseChanged -= HandlePhaseChanged;
    }

    private void HandlePhaseChanged(GamePhase phase)
    {
        if (phase == GamePhase.Ended)
            ShowResult();
    }

    private void ShowResult()
    {
        if (resultLabel == null) return;

        var state = ServiceLocator.Game?.State;
        if (state == null)
        {
            resultLabel.text = "Game Over";
            return;
        }

        int winner = state.WinnerAllianceIndex;
        string reason = state.EndReason;

        if (winner < 0)
        {
            resultLabel.text = "Game Over\n(No winner determined)";
            return;
        }

        string teamName = $"Team {winner + 1}";

        string reasonText = reason switch
        {
            "points" => "by reaching the victory point limit!",
            "time"   => "on time — most points wins!",
            _        => ""
        };

        resultLabel.text = $"{teamName} wins!\n{reasonText}";
    }
}
