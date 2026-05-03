using UnityEngine;
using UnityEngine.UI;

// Applies 10% of MaxHp as direct damage to the currently selected robot.
// Attach to PlayingPanel; wire selectionPanel and button in the Inspector.
public class TestDamageButton : MonoBehaviour
{
    [SerializeField] private RobotSelectionPanel selectionPanel;
    [SerializeField] private RobotListPanel robotListPanel;
    [SerializeField] private Button damageButton;

    private void OnEnable()
    {
        if (damageButton != null)
            damageButton.onClick.AddListener(OnDamageClicked);
    }

    private void OnDisable()
    {
        if (damageButton != null)
            damageButton.onClick.RemoveListener(OnDamageClicked);
    }

    private void OnDamageClicked()
    {
        string robotId = robotListPanel?.CurrentRobotId ?? selectionPanel?.CurrentRobotId;
        if (string.IsNullOrEmpty(robotId))
        {
            Debug.Log("[TestDamage] No robot selected.");
            return;
        }

        var game     = ServiceLocator.Game;
        var server   = ServiceLocator.RobotServer;
        var settings = ServiceLocator.GameSettings;

        int maxHp  = settings != null ? settings.MaxHp : 100;
        int amount = Mathf.Max(1, maxHp / 10);

        int newHp = game != null ? game.ApplyDirectDamage(robotId, amount) : -1;
        if (newHp < 0)
        {
            Debug.Log("[TestDamage] Robot not in game or already dead.");
            return;
        }

        server?.SendFlashHit(robotId);
        server?.SendSetHp(robotId, newHp, maxHp);

        Debug.Log($"[TestDamage] -{amount} HP to {robotId}. Now {newHp}/{maxHp}");
    }
}
