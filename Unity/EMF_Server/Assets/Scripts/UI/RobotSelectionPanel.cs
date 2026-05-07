// RobotSelectionPanel.cs - selection + video + motors on/off on select/deselect.
using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

public class RobotSelectionPanel : MonoBehaviour
{
    [Header("Buttons")]
    [SerializeField] private Button prevButton;
    [SerializeField] private Button nextButton;
    [SerializeField] private Button clearButton;

    [Header("Info Labels")]
    [SerializeField] private TextMeshProUGUI nameLabel;
    [SerializeField] private TextMeshProUGUI ipLabel;
    [SerializeField] private TextMeshProUGUI playerLabel;
    [SerializeField] private TextMeshProUGUI allianceLabel;
    [SerializeField] private TextMeshProUGUI clientLabel;

    [Header("Video")]
    [SerializeField] private ESP32VideoReceiver video;

    [Header("Video Flip")]
    [SerializeField] private Button flipHButton;
    [SerializeField] private Button flipVButton;

    private IRobotDirectory _dir;
    private RobotWebSocketServer _ws;
    private readonly List<RobotInfo> _list = new();
    private int _index = -1;
    private string _selectedRobotId;
    private bool _hflip;
    private bool _vflip;

    private HashSet<string> _allowedSet;

    public string CurrentRobotId => _selectedRobotId;

    public event Action<string> SelectionChanged;

    private void Awake()
    {
        _dir = ServiceLocator.RobotDirectory;
        _ws = ServiceLocator.RobotServer;
        if (video == null)
            video = ESP32VideoReceiver.Instance;
    }

    private void OnEnable()
    {
        WireButtons();
        SubscribeDirectory();
        RebuildList();

        _index = -1;
        _selectedRobotId = null;

        EnsureValidSelectionAfterFilter(autoSelectFirstAllowed: true);

        if (_index >= 0)
            return;

        if (video) video.ClearActiveRobot();
        RefreshFlipButtons();
        RefreshUI();
        SelectionChanged?.Invoke(null);
    }

    private void OnDisable()
    {
        UnsubscribeDirectory();
    }

    private void WireButtons()
    {
        if (prevButton != null)
        {
            prevButton.onClick.RemoveAllListeners();
            prevButton.onClick.AddListener(Prev);
        }

        if (nextButton != null)
        {
            nextButton.onClick.RemoveAllListeners();
            nextButton.onClick.AddListener(Next);
        }

        if (clearButton != null)
        {
            clearButton.onClick.RemoveAllListeners();
            clearButton.onClick.AddListener(ClearSelection);
        }

        if (flipHButton != null)
        {
            flipHButton.onClick.RemoveAllListeners();
            flipHButton.onClick.AddListener(ToggleHFlip);
        }

        if (flipVButton != null)
        {
            flipVButton.onClick.RemoveAllListeners();
            flipVButton.onClick.AddListener(ToggleVFlip);
        }
    }

    private void SubscribeDirectory()
    {
        if (_dir == null) return;
        _dir.OnRobotAdded += OnRobotAdded;
        _dir.OnRobotUpdated += OnRobotUpdated;
        _dir.OnRobotRemoved += OnRobotRemoved;
    }

    private void UnsubscribeDirectory()
    {
        if (_dir == null) return;
        _dir.OnRobotAdded -= OnRobotAdded;
        _dir.OnRobotUpdated -= OnRobotUpdated;
        _dir.OnRobotRemoved -= OnRobotRemoved;
    }

    private void RebuildList()
    {
        _list.Clear();
        if (_dir != null)
            _list.AddRange(_dir.GetAll());
        ClampIndexAfterListChange();
    }

    private void ClampIndexAfterListChange()
    {
        if (_list.Count == 0)
        {
            _index = -1;
            return;
        }
        if (_index >= _list.Count) _index = -1;
        if (_index < -1) _index = -1;
    }

    private void OnRobotAdded(RobotInfo r)
    {
        _list.Add(r);
        ClampIndexAfterListChange();
        RefreshUI();
    }

    private void OnRobotUpdated(RobotInfo r)
    {
        for (int i = 0; i < _list.Count; i++)
        {
            if (_list[i].RobotId == r.RobotId)
            {
                _list[i] = r;
                break;
            }
        }
        RefreshUI();
    }

    private void OnRobotRemoved(string robotId)
    {
        int idx = _list.FindIndex(x => x.RobotId == robotId);
        if (idx >= 0)
            _list.RemoveAt(idx);

        if (_selectedRobotId == robotId)
        {
            _index = -1;
            _selectedRobotId = null;
            if (video) video.ClearActiveRobot();
        }

        ClampIndexAfterListChange();
        RefreshUI();

        _ws = ServiceLocator.RobotServer;
        _ws?.SendMotorsOff(robotId);

        SelectionChanged?.Invoke(null);
    }

    private bool IsAllowed(string robotId)
    {
        if (string.IsNullOrEmpty(robotId)) return false;
        if (_allowedSet == null || _allowedSet.Count == 0) return true;
        return _allowedSet.Contains(robotId);
    }

    private void Prev()
    {
        if (_list.Count == 0) return;

        int startIndex = _index;
        if (startIndex < 0) startIndex = 0;

        for (int i = 0; i < _list.Count; i++)
        {
            int next = (startIndex - 1 - i + _list.Count) % _list.Count;
            string id = _list[next].RobotId;
            if (IsAllowed(id))
            {
                SelectIndex(next);
                return;
            }
        }

        ClearSelection();
    }

    private void Next()
    {
        if (_list.Count == 0) return;

        int startIndex = _index;
        if (startIndex < 0) startIndex = 0;

        for (int i = 0; i < _list.Count; i++)
        {
            int next = (startIndex + 1 + i) % _list.Count;
            string id = _list[next].RobotId;
            if (IsAllowed(id))
            {
                SelectIndex(next);
                return;
            }
        }

        ClearSelection();
    }

    private void ClearSelection()
    {
        _ws = ServiceLocator.RobotServer;

        if (!string.IsNullOrEmpty(_selectedRobotId))
        {
            _ws?.SendStreamOff(_selectedRobotId);
            _ws?.SendMotorsOff(_selectedRobotId);
        }

        _index = -1;
        _selectedRobotId = null;
        _hflip = false;
        _vflip = false;

        if (video) video.ClearActiveRobot();
        RefreshFlipButtons();
        RefreshUI();
        SelectionChanged?.Invoke(null);
    }

    private void SelectIndex(int newIndex)
    {
        if (newIndex < 0 || newIndex >= _list.Count)
        {
            ClearSelection();
            return;
        }

        var r = _list[newIndex];

        if (!IsAllowed(r.RobotId))
        {
            ClearSelection();
            return;
        }

        _ws = ServiceLocator.RobotServer;

        if (!string.IsNullOrEmpty(_selectedRobotId))
        {
            _ws?.SendStreamOff(_selectedRobotId);
            _ws?.SendMotorsOff(_selectedRobotId);
        }

        _index = newIndex;
        _selectedRobotId = r.RobotId;

        if (video)
            video.SetActiveRobot(_selectedRobotId);

        _hflip = r.HFlip;
        _vflip = r.VFlip;
        video?.SetFlip(_hflip, _vflip);
        RefreshFlipButtons();

        _ws?.SendStreamOn(_selectedRobotId);
        _ws?.SendMotorsOn(_selectedRobotId);

        RefreshUI();
        SelectionChanged?.Invoke(_selectedRobotId);
    }

    private void RefreshUI()
    {
        if (_index < 0 || _index >= _list.Count)
        {
            if (nameLabel) nameLabel.text = "(no robot selected)";
            if (ipLabel) ipLabel.text = "";
            if (playerLabel) playerLabel.text = "Player: -";
            if (allianceLabel) allianceLabel.text = "Alliance: -";
            if (clientLabel) clientLabel.text = "Client: -";
            return;
        }

        var r = _list[_index];
        string display = string.IsNullOrEmpty(r.Callsign) ? r.RobotId : r.Callsign;

        if (nameLabel) nameLabel.text = display;
        if (ipLabel)
            ipLabel.text = string.IsNullOrEmpty(r.Ip) ? "(no ip)" : r.Ip;

        if (playerLabel)
            playerLabel.text = "Player: " +
                (string.IsNullOrEmpty(r.AssignedPlayer) ? "Unassigned" : r.AssignedPlayer);

        if (allianceLabel) allianceLabel.text = "Alliance: TBD";
        if (clientLabel) clientLabel.text = "Client: TBD";
    }

    public void SetAllowedFilter(IReadOnlyList<string> allowedIds)
    {
        if (allowedIds == null || allowedIds.Count == 0)
        {
            _allowedSet = null;
            return;
        }

        if (_allowedSet == null)
            _allowedSet = new HashSet<string>();

        _allowedSet.Clear();

        for (int i = 0; i < allowedIds.Count; i++)
        {
            string id = allowedIds[i];
            if (!string.IsNullOrEmpty(id))
                _allowedSet.Add(id);
        }
    }

    private void ToggleHFlip()
    {
        _hflip = !_hflip;
        video?.SetFlip(_hflip, _vflip);
        _ws = ServiceLocator.RobotServer;
        _ws?.SendVideoFlip(_selectedRobotId, _hflip, _vflip);
        _dir?.SetFlip(_selectedRobotId, _hflip, _vflip);
        RefreshFlipButtons();
    }

    private void ToggleVFlip()
    {
        _vflip = !_vflip;
        video?.SetFlip(_hflip, _vflip);
        _ws = ServiceLocator.RobotServer;
        _ws?.SendVideoFlip(_selectedRobotId, _hflip, _vflip);
        _dir?.SetFlip(_selectedRobotId, _hflip, _vflip);
        RefreshFlipButtons();
    }

    private void RefreshFlipButtons()
    {
        bool hasRobot = !string.IsNullOrEmpty(_selectedRobotId);

        if (flipHButton != null)
        {
            var label = flipHButton.GetComponentInChildren<TextMeshProUGUI>();
            if (label != null) label.text = _hflip ? "H Flip: ON" : "H Flip: OFF";
            flipHButton.interactable = hasRobot;
        }

        if (flipVButton != null)
        {
            var label = flipVButton.GetComponentInChildren<TextMeshProUGUI>();
            if (label != null) label.text = _vflip ? "V Flip: ON" : "V Flip: OFF";
            flipVButton.interactable = hasRobot;
        }
    }

    public void EnsureValidSelectionAfterFilter(bool autoSelectFirstAllowed)
    {
        if (!string.IsNullOrEmpty(_selectedRobotId) && !IsAllowed(_selectedRobotId))
            ClearSelection();

        if (autoSelectFirstAllowed && string.IsNullOrEmpty(_selectedRobotId))
        {
            for (int i = 0; i < _list.Count; i++)
            {
                string id = _list[i].RobotId;
                if (IsAllowed(id))
                {
                    SelectIndex(i);
                    return;
                }
            }
        }
    }
}
