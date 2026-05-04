using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Heals the selected robot — mirrors the RFID base-tag heal logic.
/// Alive robots: RestoreHp (full HP restore).
/// Respawning robots: RespawnRobot (full revive from dead-walk).
/// Dead robots in the explosion phase cannot be healed yet.
/// </summary>
public class HealButton : MonoBehaviour
{
    [SerializeField] private Button              healButton;
    [SerializeField] private RobotListPanel      robotListPanel;
    [SerializeField] private RobotSelectionPanel selectionPanel; // optional fallback

    private void OnEnable()
    {
        if (healButton != null)
            healButton.onClick.AddListener(OnHealClicked);
    }

    private void OnDisable()
    {
        if (healButton != null)
            healButton.onClick.RemoveListener(OnHealClicked);
    }

    private void OnHealClicked()
    {
        string robotId = robotListPanel?.CurrentRobotId
                      ?? selectionPanel?.CurrentRobotId;

        if (string.IsNullOrEmpty(robotId))
        {
            Debug.Log("[Heal] No robot selected.");
            return;
        }

        var game     = ServiceLocator.Game;
        var server   = ServiceLocator.RobotServer;
        var settings = ServiceLocator.GameSettings;

        if (game?.State == null)
        {
            Debug.Log("[Heal] No active game state.");
            return;
        }

        int maxHp = settings != null ? settings.MaxHp : 100;

        // Explosion phase — robot is dead but hasn't transitioned to dead-walk yet
        if (game.State.DeadRobots.Contains(robotId) && !game.State.RespawningRobots.Contains(robotId))
        {
            Debug.Log($"[Heal] {robotId} is in explosion phase — cannot heal yet.");
            return;
        }

        if (game.State.RespawningRobots.Contains(robotId))
        {
            game.RespawnRobot(robotId);
            Debug.Log($"[Heal] Respawned {robotId} at base.");
        }
        else
        {
            game.RestoreHp(robotId);
        }

        server?.SendFlashHeal(robotId);
        server?.SendSetHp(robotId, maxHp, maxHp);
    }
}
