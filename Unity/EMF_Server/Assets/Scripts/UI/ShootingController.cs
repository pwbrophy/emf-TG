using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

// ShootingController — manages per-robot fire cooldown and routes fire requests
// to IrSlotScheduler.  The actual IR sequence runs in IrSlotScheduler.
public class ShootingController : MonoBehaviour
{
    [Header("UI wiring")]
    [SerializeField] private Button shootButton;
    [SerializeField] private TextMeshProUGUI cooldownLabel;
    [SerializeField] private RobotSelectionPanel selectionPanel;

    // Per-robot cooldown: robotId -> time when next shot is allowed
    private readonly Dictionary<string, float> _nextFireTime = new Dictionary<string, float>();

    private void Awake()
    {
        ServiceLocator.Shooting = this;
    }

    private void OnEnable()
    {
        if (shootButton != null)
            shootButton.onClick.AddListener(OnShootClicked);
    }

    private void OnDisable()
    {
        if (shootButton != null)
            shootButton.onClick.RemoveListener(OnShootClicked);
    }

    private void Update()
    {
        var robotId = selectionPanel?.CurrentRobotId;
        if (string.IsNullOrEmpty(robotId))
        {
            if (shootButton) shootButton.interactable = false;
            if (cooldownLabel) cooldownLabel.text = "";
            return;
        }

        float remaining = CooldownRemaining(robotId);
        bool ready = remaining <= 0f;

        if (shootButton) shootButton.interactable = ready;

        if (cooldownLabel)
            cooldownLabel.text = remaining > 0f ? $"Cooldown: {remaining:F1}s" : "";
    }

    // Called by PlayerWebSocketServer for phone fire requests, and OnShootClicked
    // for operator button presses.
    public void RequestFire(string robotId)
    {
        if (string.IsNullOrEmpty(robotId)) return;
        if (CooldownRemaining(robotId) > 0f) return;

        // Block fire while dead (explosion) or in dead walk
        var state = ServiceLocator.Game?.State;
        if (state != null && (state.DeadRobots.Contains(robotId) || state.RespawningRobots.Contains(robotId))) return;

        Debug.Log("[Shooting] RequestFire: " + robotId);
        float cooldown = ServiceLocator.GameSettings?.FireCooldownSeconds ?? 3f;
        _nextFireTime[robotId] = Time.time + cooldown;

        var server = ServiceLocator.RobotServer;
        if (server != null) server.SendFlashFire(robotId);

        var scheduler = ServiceLocator.IrSlotScheduler;
        if (scheduler != null)
            scheduler.EnqueueFire(robotId);
        else
            Debug.LogWarning("[Shooting] IrSlotScheduler not available.");
    }

    public float CooldownRemaining(string robotId)
    {
        if (_nextFireTime.TryGetValue(robotId, out float t))
            return Mathf.Max(0f, t - Time.time);
        return 0f;
    }

    private void OnShootClicked()
    {
        if (selectionPanel == null)
        {
            Debug.LogWarning("[Shooting] No RobotSelectionPanel wired.");
            return;
        }

        string shooterId = selectionPanel.CurrentRobotId;
        if (string.IsNullOrEmpty(shooterId))
        {
            Debug.Log("[Shooting] No robot selected.");
            return;
        }

        if (CooldownRemaining(shooterId) > 0f)
        {
            Debug.Log("[Shooting] Still on cooldown.");
            return;
        }


        RequestFire(shooterId);
    }
}
