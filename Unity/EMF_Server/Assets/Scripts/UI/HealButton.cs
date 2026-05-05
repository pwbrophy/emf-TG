using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Heals the selected robot — operator override, bypasses all phase guards.
/// Dead/explosion phase: force-transitions to respawning then fully revives.
/// Dead-walk phase: RespawnRobot (full revive).
/// Alive robots: RestoreHp (full HP restore).
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

        if (game.State.DeadRobots.Contains(robotId))
        {
            // Operator override: skip explosion phase and revive immediately
            game.TransitionToRespawning(robotId);
            game.RespawnRobot(robotId);
            Debug.Log($"[Heal] Operator revive: {robotId} force-respawned.");
        }
        else if (game.State.RespawningRobots.Contains(robotId))
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
