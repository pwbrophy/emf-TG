using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

// ShootingController - realtime shooting with 3-second per-robot cooldown.
public class ShootingController : MonoBehaviour
{
    [Header("UI wiring")]
    [SerializeField] private Button shootButton;
    [SerializeField] private TextMeshProUGUI resultLabel;
    [SerializeField] private TextMeshProUGUI cooldownLabel;
    [SerializeField] private RobotSelectionPanel selectionPanel;

    [Header("Timings")]
    [SerializeField] private float fireCooldownSeconds = 3f;

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

    /// <summary>
    /// Called by PlayerWebSocketServer to fire from a phone player's request.
    /// </summary>
    public void RequestFire(string robotId)
    {
        if (string.IsNullOrEmpty(robotId)) return;
        if (CooldownRemaining(robotId) > 0f) return;

        Debug.Log("[Shooting] RequestFire: " + robotId);
        _nextFireTime[robotId] = Time.time + fireCooldownSeconds;

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

        RequestFire(shooterId);
    }
}
