using System.Collections.Generic;
using TMPro;
using UnityEngine;

public class ServerPanelPresenter : MonoBehaviour
{
    [Header("Robots Number Text")]
    [SerializeField] private GameObject NumRobots;

    private IRobotDirectory _dir;
    private bool _isSubscribed = false;

    private void OnEnable()
    {
        _dir = ServiceLocator.RobotDirectory;
        UpdateRobotsText();

        if (!_isSubscribed)
        {
            _dir.OnRobotAdded += HandleRobotAdded;
            _dir.OnRobotRemoved += HandleRobotRemoved;
            _isSubscribed = true;
        }
    }

    private void OnDisable()
    {
        if (_dir != null && _isSubscribed)
        {
            _dir.OnRobotAdded -= HandleRobotAdded;
            _dir.OnRobotRemoved -= HandleRobotRemoved;
            _isSubscribed = false;
        }
    }

    private void HandleRobotAdded(RobotInfo r)    { UpdateRobotsText(); }
    private void HandleRobotRemoved(string robotId) { UpdateRobotsText(); }

    private void UpdateRobotsText()
    {
        List<RobotInfo> robots = new List<RobotInfo>(_dir.GetAll());
        TextMeshProUGUI NumRobotsText = NumRobots.GetComponent<TextMeshProUGUI>();
        NumRobotsText.text = robots.Count.ToString();
    }
}
