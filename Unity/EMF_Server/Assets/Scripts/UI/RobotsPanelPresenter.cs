using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

public class RobotsPanelPresenter : MonoBehaviour
{
    [Header("ScrollView wiring")]
    [SerializeField] private RectTransform content;
    [SerializeField] private GameObject rowPrefab;

    [Header("Optional: small modal to rename/assign")]
    [SerializeField] private RenamePopup renamePopup;

    private IRobotDirectory _dir;
    private bool _isSubscribed = false;

    private void OnEnable()
    {
        _dir = ServiceLocator.RobotDirectory;

        Debug.Log($"[RobotsPanelPresenter:{name}] OnEnable - Using RobotDirectory hash={_dir?.GetHashCode()}");

        if (_dir == null)
        {
            Debug.LogError("[RobotsPanelPresenter] RobotDirectory is null. Is AppBootstrap in the scene and enabled?");
            return;
        }

        if (content == null)
        {
            Debug.LogError("[RobotsPanelPresenter] 'content' is not assigned in the Inspector.");
            return;
        }
        if (rowPrefab == null)
        {
            Debug.LogError("[RobotsPanelPresenter] 'rowPrefab' is not assigned in the Inspector.");
            return;
        }

        RebuildFromDirectory();

        if (!_isSubscribed)
        {
            _dir.OnRobotAdded += HandleRobotAdded;
            _dir.OnRobotUpdated += HandleRobotUpdated;
            _dir.OnRobotRemoved += HandleRobotRemoved;
            _isSubscribed = true;
        }
    }

    private void OnDisable()
    {
        if (_dir != null && _isSubscribed)
        {
            _dir.OnRobotAdded -= HandleRobotAdded;
            _dir.OnRobotUpdated -= HandleRobotUpdated;
            _dir.OnRobotRemoved -= HandleRobotRemoved;
            _isSubscribed = false;
        }
    }

    private void RebuildFromDirectory()
    {
        ClearAllRows();

        List<RobotInfo> robots = new List<RobotInfo>(_dir.GetAll());
        robots.Sort((a, b) => string.Compare(a.Callsign, b.Callsign, StringComparison.OrdinalIgnoreCase));

        for (int i = 0; i < robots.Count; i++)
            CreateOrUpdateRow(robots[i], allowReuse: false);

        Debug.Log($"[RobotsPanelPresenter:{name}] Rebuild - Rendered {robots.Count} robots.");
    }

    private void ClearAllRows()
    {
        for (int i = content.childCount - 1; i >= 0; i--)
        {
            Transform child = content.GetChild(i);
            Destroy(child.gameObject);
        }
    }

    private void HandleRobotAdded(RobotInfo r)  { CreateOrUpdateRow(r); }
    private void HandleRobotUpdated(RobotInfo r) { CreateOrUpdateRow(r); }

    private void HandleRobotRemoved(string robotId)
    {
        Transform row = content.Find(robotId);
        if (row != null)
            Destroy(row.gameObject);
    }

    private void CreateOrUpdateRow(RobotInfo r, bool allowReuse = true)
    {
        GameObject rowGO;

        if (allowReuse)
        {
            Transform existing = content.Find(r.RobotId);
            rowGO = existing == null
                ? Instantiate(rowPrefab, content, false)
                : existing.gameObject;
        }
        else
        {
            rowGO = Instantiate(rowPrefab, content, false);
        }

        rowGO.name = r.RobotId;

        TextMeshProUGUI nameText   = rowGO.transform.Find("Name").GetComponent<TextMeshProUGUI>();
        TextMeshProUGUI ipText     = rowGO.transform.Find("Ip").GetComponent<TextMeshProUGUI>();
        TextMeshProUGUI playerText = rowGO.transform.Find("Player").GetComponent<TextMeshProUGUI>();
        Button editButton          = rowGO.transform.Find("EditButton").GetComponent<Button>();

        nameText.text   = r.Callsign;
        ipText.text     = string.IsNullOrEmpty(r.Ip) ? "IP: ?" : $"IP: {r.Ip}";
        playerText.text = string.IsNullOrEmpty(r.AssignedPlayer) ? "Unassigned" : r.AssignedPlayer;

        editButton.onClick.RemoveAllListeners();
        editButton.onClick.AddListener(() =>
        {
            if (renamePopup == null)
            {
                Debug.Log("[RobotsPanelPresenter] RenamePopup is not assigned. Skipping edit.");
                return;
            }

            renamePopup.Open(
                r.RobotId,
                r.Callsign,
                (string.IsNullOrWhiteSpace(r.AssignedPlayer) || r.AssignedPlayer == "Unassigned")
                    ? null
                    : r.AssignedPlayer,
                (id, newName, newPlayer) =>
                {
                    _dir.SetCallsign(id, newName);
                    _dir.SetAssignedPlayer(id, newPlayer);
                }
            );
        });
    }
}
