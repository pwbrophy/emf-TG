using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// Toggles FPV spectate mode on the web display page for the currently selected robot.
public class SpectateButtonController : MonoBehaviour
{
    [SerializeField] private Button         _fpvButton;
    [SerializeField] private TextMeshProUGUI _fpvLabel;
    [SerializeField] private RobotListPanel  _robotListPanel;

    static readonly Color C_OFF = new Color(0.100f, 0.180f, 0.280f);
    static readonly Color C_ON  = new Color(0.100f, 0.350f, 0.100f);

    private bool _fpvActive;

    private void Start()
    {
        // Wire here, not Awake — PlayingPanelBuilder sets _fpvButton via reflection
        // after AddComponent fires Awake, so the field is null during Awake.
        if (_fpvButton != null)
            _fpvButton.onClick.AddListener(OnClick);
        if (_robotListPanel != null)
            _robotListPanel.SelectionChanged += OnRobotSelectionChanged;
    }

    private void OnDestroy()
    {
        if (_robotListPanel != null)
            _robotListPanel.SelectionChanged -= OnRobotSelectionChanged;
    }

    private void OnEnable()
    {
        // Reset to off whenever the panel is shown (game start or panel re-enable),
        // and tell the display to return to info view.
        if (_fpvActive)
            ServiceLocator.PlayerServer?.SendSpectateUpdate(null, false);
        Apply(false);
    }

    private void OnRobotSelectionChanged(string robotId)
    {
        if (!_fpvActive) return;
        if (string.IsNullOrEmpty(robotId))
        {
            ServiceLocator.PlayerServer?.SendSpectateUpdate(null, false);
            Apply(false);
            return;
        }
        ServiceLocator.PlayerServer?.SendSpectateUpdate(robotId, true);
    }

    private void OnClick()
    {
        bool newState = !_fpvActive;

        if (newState)
        {
            string robotId = _robotListPanel != null ? _robotListPanel.CurrentRobotId : null;
            if (string.IsNullOrEmpty(robotId))
            {
                // No robot explicitly selected — fall back to the first connected robot
                var dir = ServiceLocator.RobotDirectory;
                if (dir != null)
                    foreach (var r in dir.GetAll())
                    { robotId = r.RobotId; break; }
            }
            if (string.IsNullOrEmpty(robotId))
            {
                Debug.LogWarning("[SpectateBtn] No robot selected or connected — cannot enable FPV.");
                return;
            }
            ServiceLocator.PlayerServer?.SendSpectateUpdate(robotId, true);
        }
        else
        {
            ServiceLocator.PlayerServer?.SendSpectateUpdate(null, false);
        }

        Apply(newState);
    }

    private void Apply(bool on)
    {
        _fpvActive = on;
        if (_fpvLabel != null)
            _fpvLabel.text = on ? "FPV: ON" : "FPV: OFF";

        if (_fpvButton != null)
        {
            var img = _fpvButton.GetComponent<Image>();
            if (img != null) img.color = on ? C_ON : C_OFF;
        }
    }
}
