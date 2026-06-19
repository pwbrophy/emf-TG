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

    private void HandleRobotAdded(RobotInfo r)  { RebuildFromDirectory(); }
    private void HandleRobotUpdated(RobotInfo r) { RebuildFromDirectory(); }

    private void HandleRobotRemoved(string robotId)
    {
        Transform row = content.Find(robotId);
        if (row != null)
            Destroy(row.gameObject);
    }

    private static readonly Color AllianceColorDesert = new Color(0.72f, 0.56f, 0.35f); // tan
    private static readonly Color AllianceColorJungle = new Color(0.29f, 0.48f, 0.29f); // green
    private static readonly Color AllianceColorNone   = new Color(0.35f, 0.35f, 0.35f); // grey

    private static void RefreshAllianceButton(Button btn, int alliance)
    {
        var img = btn.GetComponent<UnityEngine.UI.Image>();
        var tmp = btn.GetComponentInChildren<TMPro.TextMeshProUGUI>();
        if (alliance == 0)      { if (img) img.color = AllianceColorDesert; if (tmp) { tmp.text = "D"; tmp.color = Color.white; } }
        else if (alliance == 1) { if (img) img.color = AllianceColorJungle; if (tmp) { tmp.text = "J"; tmp.color = Color.white; } }
        else                    { if (img) img.color = AllianceColorNone;   if (tmp) { tmp.text = "?"; tmp.color = new Color(0.7f,0.7f,0.7f); } }
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
        Button allianceButton      = rowGO.transform.Find("AllianceButton")?.GetComponent<Button>();
        Button editButton          = rowGO.transform.Find("EditButton").GetComponent<Button>();
        HoldButton turretLeft      = rowGO.transform.Find("TurretLeft")?.GetComponent<HoldButton>();
        HoldButton turretRight     = rowGO.transform.Find("TurretRight")?.GetComponent<HoldButton>();

        nameText.text   = r.Callsign;
        ipText.text     = string.IsNullOrEmpty(r.Ip) ? "IP: ?" : $"IP: {r.Ip}";
        playerText.text = string.IsNullOrEmpty(r.AssignedPlayer) ? "Unassigned" : r.AssignedPlayer;

        // Alliance cycle button: ? → Desert (D) → Jungle (J) → ?
        if (allianceButton != null)
        {
            RefreshAllianceButton(allianceButton, r.PreferredAlliance);
            allianceButton.onClick.RemoveAllListeners();
            allianceButton.onClick.AddListener(() =>
            {
                // Current alliance from the live RobotInfo (may have changed since row was built)
                int current = r.PreferredAlliance;
                int next = current == -1 ? 0 : current == 0 ? 1 : -1;
                _dir.SetPreferredAlliance(r.RobotId, next);
                RefreshAllianceButton(allianceButton, next);
            });
        }

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
                    if (!string.IsNullOrWhiteSpace(newName))
                        _dir.SetCallsign(id, newName);
                    if (!string.IsNullOrWhiteSpace(newPlayer))
                        _dir.SetAssignedPlayer(id, newPlayer);
                }
            );
        });

        string rid = r.RobotId;
        if (turretLeft != null)
        {
            turretLeft.OnPressed  = () => { ServiceLocator.RobotServer?.SendMotorsOn(rid); ServiceLocator.RobotServer?.SendTurret(rid, 1f); };
            turretLeft.OnReleased = () => ServiceLocator.RobotServer?.SendTurret(rid, 0f);
        }
        if (turretRight != null)
        {
            turretRight.OnPressed  = () => { ServiceLocator.RobotServer?.SendMotorsOn(rid); ServiceLocator.RobotServer?.SendTurret(rid, -1f); };
            turretRight.OnReleased = () => ServiceLocator.RobotServer?.SendTurret(rid, 0f);
        }
    }
}
