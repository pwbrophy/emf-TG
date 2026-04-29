using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Toggle button on PlayingPanel. Pauses/resumes the match timer and all robots.
/// Label shows "PAUSE" when game is running, "RESUME" when paused.
/// </summary>
public class PauseGameButton : MonoBehaviour
{
    [SerializeField] public Button            button;
    [SerializeField] public TextMeshProUGUI   label;

    private GameFlow _flow;

    private void OnEnable()
    {
        _flow = ServiceLocator.GameFlow;
        if (_flow != null) _flow.OnPausedChanged += OnPausedChanged;
        if (button != null) button.onClick.AddListener(OnClick);
        Refresh();
    }

    private void OnDisable()
    {
        if (_flow != null) _flow.OnPausedChanged -= OnPausedChanged;
        if (button != null) button.onClick.RemoveListener(OnClick);
    }

    private void OnClick()
    {
        if (_flow == null) return;
        if (_flow.IsPaused) _flow.ResumeGame();
        else                _flow.PauseGame();
    }

    private void OnPausedChanged(bool paused) => Refresh();

    private void Refresh()
    {
        if (label == null) return;
        bool paused = _flow?.IsPaused ?? false;
        label.text = paused ? "RESUME" : "PAUSE";
    }
}
