using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// Controls FPV spectate mode on the web display page.
/// FPV×1: selected (or first) robot fullscreen. FPV×6: all-robot grid.
/// Buttons are wired by PlayingPanelBuilder via reflection.
public class SpectateButtonController : MonoBehaviour
{
    [SerializeField] private Button          _fpv1Button;
    [SerializeField] private TextMeshProUGUI _fpv1Label;
    [SerializeField] private Button          _fpv6Button;
    [SerializeField] private TextMeshProUGUI _fpv6Label;
    [SerializeField] private RobotListPanel  _robotListPanel;

    static readonly Color C_OFF = new Color(0.100f, 0.180f, 0.280f);
    static readonly Color C_ON  = new Color(0.100f, 0.350f, 0.100f);

    enum FpvMode { Off, Single, Grid }
    FpvMode _mode = FpvMode.Off;

    private void Start()
    {
        // Wire here, not Awake — PlayingPanelBuilder sets fields via reflection
        // after AddComponent fires Awake, so fields are null during Awake.
        if (_fpv1Button != null) _fpv1Button.onClick.AddListener(OnClickSingle);
        if (_fpv6Button != null) _fpv6Button.onClick.AddListener(OnClickGrid);
        if (_robotListPanel != null) _robotListPanel.SelectionChanged += OnRobotSelectionChanged;
    }

    private void OnDestroy()
    {
        if (_robotListPanel != null)
            _robotListPanel.SelectionChanged -= OnRobotSelectionChanged;
    }

    private void OnEnable()
    {
        // Reset to off whenever the panel is shown (game start or re-enable).
        if (_mode != FpvMode.Off)
            ServiceLocator.PlayerServer?.SendSpectateUpdate(null, false);
        Apply(FpvMode.Off);
    }

    private void OnRobotSelectionChanged(string robotId)
    {
        if (_mode != FpvMode.Single) return;
        if (string.IsNullOrEmpty(robotId))
        {
            ServiceLocator.PlayerServer?.SendSpectateUpdate(null, false);
            Apply(FpvMode.Off);
            return;
        }
        // Switch single-tank FPV to the newly selected robot.
        ServiceLocator.PlayerServer?.SendSpectateUpdate(robotId, true);
    }

    private void OnClickSingle()
    {
        if (_mode == FpvMode.Single) { TurnOff(); return; }

        string robotId = GetRobotId();
        if (string.IsNullOrEmpty(robotId)) return;

        Apply(FpvMode.Single);
        ServiceLocator.PlayerServer?.SendSpectateUpdate(robotId, true);
    }

    private void OnClickGrid()
    {
        if (_mode == FpvMode.Grid) { TurnOff(); return; }

        Apply(FpvMode.Grid);
        ServiceLocator.PlayerServer?.SendSpectateUpdateGrid();
    }

    private void TurnOff()
    {
        ServiceLocator.PlayerServer?.SendSpectateUpdate(null, false);
        Apply(FpvMode.Off);
    }

    private string GetRobotId()
    {
        string robotId = _robotListPanel != null ? _robotListPanel.CurrentRobotId : null;
        if (!string.IsNullOrEmpty(robotId)) return robotId;

        // Fall back to first assigned robot so FPV works without manual row selection.
        var dir = ServiceLocator.RobotDirectory;
        if (dir != null)
            foreach (var r in dir.GetAll())
                if (!string.IsNullOrEmpty(r.AssignedPlayer))
                    return r.RobotId;

        Debug.LogWarning("[SpectateBtn] No robot selected or connected.");
        return null;
    }

    private void Apply(FpvMode mode)
    {
        _mode = mode;
        bool single = (mode == FpvMode.Single);
        bool grid   = (mode == FpvMode.Grid);

        if (_fpv1Label != null) _fpv1Label.text = single ? "FPV: Selected ON" : "FPV: Selected";
        if (_fpv6Label != null) _fpv6Label.text = grid   ? "FPV: All ON"      : "FPV: All";
        SetColor(_fpv1Button, single);
        SetColor(_fpv6Button, grid);
    }

    static void SetColor(Button btn, bool on)
    {
        if (btn == null) return;
        var img = btn.GetComponent<Image>();
        if (img != null) img.color = on ? C_ON : C_OFF;
    }
}
