using TMPro;
using UnityEngine;

/// <summary>
/// Drives a TextMeshProUGUI label from MatchTimer.OnTick.
/// Place on PlayingPanel.
/// </summary>
public class MatchTimerDisplay : MonoBehaviour
{
    [SerializeField] private TextMeshProUGUI timerLabel;

    private MatchTimer _timer;

    private void OnEnable()
    {
        _timer = ServiceLocator.MatchTimer;
        if (_timer != null)
            _timer.OnTick += HandleTick;

        if (timerLabel) timerLabel.text = "";
    }

    private void OnDisable()
    {
        if (_timer != null)
            _timer.OnTick -= HandleTick;
    }

    private void HandleTick(float remaining)
    {
        if (timerLabel == null) return;
        int mins = Mathf.FloorToInt(remaining / 60f);
        int secs = Mathf.FloorToInt(remaining % 60f);
        timerLabel.text = $"{mins:D1}:{secs:D2}";

        // Flash red in last 10 seconds
        timerLabel.color = remaining <= 10f
            ? (Mathf.FloorToInt(remaining * 2) % 2 == 0 ? Color.red : Color.white)
            : Color.white;
    }
}
