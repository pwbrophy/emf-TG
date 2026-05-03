// RobotPingButton.cs
// Sends a ping to the currently-selected robot and displays the round-trip result.
// Attach to any GameObject in the PlayingPanel hierarchy.
// Wire pingButton, resultLabel, and selectionPanel in the Inspector.
using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

public class RobotPingButton : MonoBehaviour
{
    [SerializeField] private Button pingButton;
    [SerializeField] private TextMeshProUGUI resultLabel;
    [SerializeField] private RobotSelectionPanel selectionPanel;
    [SerializeField] private RobotListPanel robotListPanel;

    private RobotWebSocketServer _ws;
    private float _sentAt;

    private void Awake()
    {
        _ws = ServiceLocator.RobotServer;
    }

    private void OnEnable()
    {
        if (_ws == null) _ws = ServiceLocator.RobotServer;
        if (_ws != null) _ws.OnPong += OnPong;

        if (pingButton != null)
        {
            pingButton.onClick.RemoveAllListeners();
            pingButton.onClick.AddListener(SendPing);
        }

        if (resultLabel != null) resultLabel.text = "—";
    }

    private void OnDisable()
    {
        if (_ws != null) _ws.OnPong -= OnPong;
    }

    private void SendPing()
    {
        if (_ws == null) _ws = ServiceLocator.RobotServer;

        string robotId = robotListPanel?.CurrentRobotId
                      ?? (selectionPanel != null ? selectionPanel.CurrentRobotId : null);

        if (string.IsNullOrEmpty(robotId))
        {
            if (resultLabel) resultLabel.text = "No robot selected";
            return;
        }

        _sentAt = Time.realtimeSinceStartup;
        bool ok = _ws != null && _ws.SendPing(robotId);

        if (resultLabel)
            resultLabel.text = ok ? $"Ping sent to {robotId.Substring(0, 6)}…" : "Send FAILED";
    }

    private void OnPong(string robotId)
    {
        float rtt = (Time.realtimeSinceStartup - _sentAt) * 1000f;
        if (resultLabel)
            resultLabel.text = $"Pong from {robotId.Substring(0, 6)}… ({rtt:F0} ms)";
        Debug.Log($"[PING] pong from {robotId}, RTT={rtt:F0} ms");
    }
}
