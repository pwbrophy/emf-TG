using UnityEngine;
using UnityEngine.UI;

// Toggles camera streaming for the robot currently selected in RobotListPanel.
// Attach to PlayingPanel; wire button and robotListPanel in the Inspector.
public class ToggleCameraButton : MonoBehaviour
{
    [SerializeField] private Button         button;
    [SerializeField] private RobotListPanel robotListPanel;

    private void OnEnable()
    {
        if (button != null)
            button.onClick.AddListener(OnClicked);
    }

    private void OnDisable()
    {
        if (button != null)
            button.onClick.RemoveListener(OnClicked);
    }

    private void OnClicked()
    {
        if (robotListPanel == null)
        {
            Debug.Log("[ToggleCamera] RobotListPanel not assigned.");
            return;
        }
        robotListPanel.ToggleCameraForSelected();
    }
}
