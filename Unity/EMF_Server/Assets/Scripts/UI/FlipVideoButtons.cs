using TMPro;
using UnityEngine;
using UnityEngine.UI;

// Wires H FLIP and V FLIP buttons to RobotListPanel.
// Updates button labels from RobotDirectory whenever selection or flip state changes.
public class FlipVideoButtons : MonoBehaviour
{
    [SerializeField] private RobotListPanel robotListPanel;
    [SerializeField] private Button         hFlipButton;
    [SerializeField] private Button         vFlipButton;

    private IRobotDirectory _dir;

    private void OnEnable()
    {
        _dir = ServiceLocator.RobotDirectory;
        if (hFlipButton != null) hFlipButton.onClick.AddListener(OnHFlip);
        if (vFlipButton != null) vFlipButton.onClick.AddListener(OnVFlip);
        if (robotListPanel != null) robotListPanel.SelectionChanged += OnSelectionChanged;
        if (_dir != null) _dir.OnRobotUpdated += OnRobotUpdated;
        RefreshLabels();
    }

    private void OnDisable()
    {
        if (hFlipButton != null) hFlipButton.onClick.RemoveListener(OnHFlip);
        if (vFlipButton != null) vFlipButton.onClick.RemoveListener(OnVFlip);
        if (robotListPanel != null) robotListPanel.SelectionChanged -= OnSelectionChanged;
        if (_dir != null) _dir.OnRobotUpdated -= OnRobotUpdated;
    }

    private void OnHFlip() => robotListPanel?.FlipHForSelected();
    private void OnVFlip() => robotListPanel?.FlipVForSelected();

    private void OnSelectionChanged(string _) => RefreshLabels();
    private void OnRobotUpdated(RobotInfo _)  => RefreshLabels();

    private void RefreshLabels()
    {
        var dir = _dir ?? ServiceLocator.RobotDirectory;
        string id = robotListPanel != null ? robotListPanel.CurrentRobotId : null;
        if (dir != null && !string.IsNullOrEmpty(id) && dir.TryGet(id, out var info))
        {
            SetLabel(hFlipButton, info.HFlip ? "H FLIP: ON" : "H FLIP: OFF");
            SetLabel(vFlipButton, info.VFlip ? "V FLIP: ON" : "V FLIP: OFF");
        }
        else
        {
            SetLabel(hFlipButton, "H FLIP");
            SetLabel(vFlipButton, "V FLIP");
        }
    }

    private static void SetLabel(Button btn, string text)
    {
        if (btn == null) return;
        var lbl = btn.GetComponentInChildren<TextMeshProUGUI>();
        if (lbl != null) lbl.text = text;
    }
}
