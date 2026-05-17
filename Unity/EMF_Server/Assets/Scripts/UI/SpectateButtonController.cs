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
    }

    private void OnEnable()
    {
        // Reset to off whenever the panel is shown (game start or panel re-enable).
        Apply(false);
    }

    private void OnClick()
    {
        bool newState = !_fpvActive;

        if (newState)
        {
            string robotId = _robotListPanel != null ? _robotListPanel.CurrentRobotId : null;
            if (string.IsNullOrEmpty(robotId))
            {
                Debug.LogWarning("[SpectateBtn] No robot selected — cannot enable FPV.");
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
