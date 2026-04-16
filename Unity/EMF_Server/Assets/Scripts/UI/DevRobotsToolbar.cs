using UnityEngine;
using UnityEngine.UI;

public class DevRobotsToolbar : MonoBehaviour
{
    [Header("Buttons")]
    [SerializeField] private Button addFakeRobotButton;
    [SerializeField] private Button removeLastButton;

    private IRobotDirectory _dir;
    private string _lastRobotId;

    private void Start()
    {
        _dir = ServiceLocator.RobotDirectory;

        if (addFakeRobotButton != null)
            addFakeRobotButton.onClick.AddListener(AddFake);

        if (removeLastButton != null)
            removeLastButton.onClick.AddListener(RemoveLast);
    }

    private void AddFake()
    {
        _lastRobotId = "esp32-" + Random.Range(100000, 999999);
        var ip = $"192.168.0.{Random.Range(20, 200)}";

        _dir.Upsert(_lastRobotId, callsign: "", ip: ip);
        Debug.Log($"[Dev] Added fake robot {_lastRobotId} @ {ip}");
    }

    private void RemoveLast()
    {
        bool removed = _dir.RemoveLast();
        if (!removed)
            Debug.Log("[Dev] No robots to remove.");
    }
}
