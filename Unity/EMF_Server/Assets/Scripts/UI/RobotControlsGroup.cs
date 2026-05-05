using UnityEngine;
using UnityEngine.UI;

// Greys out all robot-action buttons when no robot is selected in RobotListPanel.
// Attach to PlayingPanel; wire robotListPanel and buttons in Inspector (via RebuildPlayingPanel).
public class RobotControlsGroup : MonoBehaviour
{
    [SerializeField] private RobotListPanel robotListPanel;
    [SerializeField] private Button[] buttons;

    private void OnEnable()
    {
        if (robotListPanel != null)
            robotListPanel.SelectionChanged += OnSelectionChanged;
        Refresh();
    }

    private void OnDisable()
    {
        if (robotListPanel != null)
            robotListPanel.SelectionChanged -= OnSelectionChanged;
    }

    private void OnSelectionChanged(string _) => Refresh();

    private void Refresh()
    {
        bool has = robotListPanel != null && !string.IsNullOrEmpty(robotListPanel.CurrentRobotId);
        foreach (var btn in buttons)
            if (btn != null) btn.interactable = has;
    }
}
