using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Displays a live HP row for every robot in the current match.
/// Place inside PlayingPanel. Subscribe to GameService.OnHpChanged.
/// </summary>
public class RobotHpPanel : MonoBehaviour
{
    [SerializeField] private RectTransform rowContainer;  // Vertical layout group parent

    private GameService _game;
    private IRobotDirectory _dir;

    // robotId → (row root, fill image, label)
    private readonly Dictionary<string, (GameObject root, Image fill, TextMeshProUGUI label)> _rows
        = new Dictionary<string, (GameObject, Image, TextMeshProUGUI)>();

    private void OnEnable()
    {
        _game = ServiceLocator.Game;
        _dir  = ServiceLocator.RobotDirectory;

        if (_game != null)
        {
            _game.OnHpChanged  += HandleHpChanged;
            _game.OnRobotDied  += HandleRobotDied;
        }

        RebuildRows();
    }

    private void OnDisable()
    {
        if (_game != null)
        {
            _game.OnHpChanged  -= HandleHpChanged;
            _game.OnRobotDied  -= HandleRobotDied;
        }
    }

    private void RebuildRows()
    {
        ClearRows();

        if (_game?.State == null) return;

        var settings = ServiceLocator.GameSettings;
        int maxHp    = settings != null ? settings.MaxHp : 100;

        foreach (var r in _game.State.Robots)
        {
            int hp = _game.State.RobotHp.GetValueOrDefault(r.RobotId, maxHp);
            CreateRow(r.RobotId, r.Callsign, hp, maxHp);
        }
    }

    private void CreateRow(string robotId, string callsign, int hp, int maxHp)
    {
        if (rowContainer == null) return;

        // Row root
        var row = new GameObject(robotId, typeof(RectTransform), typeof(HorizontalLayoutGroup));
        row.transform.SetParent(rowContainer, false);
        var hlg = row.GetComponent<HorizontalLayoutGroup>();
        hlg.spacing = 6;
        hlg.childForceExpandWidth  = false;
        hlg.childForceExpandHeight = true;
        hlg.childControlHeight     = true;
        var rowLE = row.AddComponent<LayoutElement>();
        rowLE.preferredHeight = 28;

        // Name label
        var nameGO = new GameObject("Name", typeof(RectTransform), typeof(CanvasRenderer), typeof(TextMeshProUGUI));
        nameGO.transform.SetParent(row.transform, false);
        var nameLabel = nameGO.GetComponent<TextMeshProUGUI>();
        nameLabel.text      = string.IsNullOrEmpty(callsign) ? robotId : callsign;
        nameLabel.fontSize   = 14;
        nameLabel.color      = Color.white;
        var nameLE = nameGO.AddComponent<LayoutElement>();
        nameLE.preferredWidth = 80;

        // HP bar background
        var barBG = new GameObject("BarBG", typeof(RectTransform), typeof(Image));
        barBG.transform.SetParent(row.transform, false);
        barBG.GetComponent<Image>().color = new Color(0.2f, 0.2f, 0.2f, 1f);
        var barLE = barBG.AddComponent<LayoutElement>();
        barLE.preferredWidth  = 120;
        barLE.flexibleWidth   = 1;

        // HP bar fill
        var fill = new GameObject("Fill", typeof(RectTransform), typeof(Image));
        fill.transform.SetParent(barBG.transform, false);
        var fillImg = fill.GetComponent<Image>();
        fillImg.color = new Color(0.2f, 0.4f, 1f, 1f);  // blue
        var fillRT = fill.GetComponent<RectTransform>();
        fillRT.anchorMin = new Vector2(0, 0);
        fillRT.anchorMax = new Vector2(0, 1);
        fillRT.pivot     = new Vector2(0, 0.5f);
        fillRT.offsetMin = Vector2.zero;
        fillRT.offsetMax = Vector2.zero;

        // HP text
        var hpGO = new GameObject("HpText", typeof(RectTransform), typeof(CanvasRenderer), typeof(TextMeshProUGUI));
        hpGO.transform.SetParent(row.transform, false);
        var hpLabel = hpGO.GetComponent<TextMeshProUGUI>();
        hpLabel.text     = $"{hp}/{maxHp}";
        hpLabel.fontSize  = 13;
        hpLabel.color     = Color.white;
        var hpLE = hpGO.AddComponent<LayoutElement>();
        hpLE.preferredWidth = 55;

        _rows[robotId] = (row, fillImg, hpLabel);
        UpdateRow(robotId, hp, maxHp);
    }

    private void UpdateRow(string robotId, int hp, int maxHp)
    {
        if (!_rows.TryGetValue(robotId, out var row)) return;

        var settings = ServiceLocator.GameSettings;
        if (maxHp <= 0) maxHp = settings != null ? settings.MaxHp : 100;

        float fraction = maxHp > 0 ? Mathf.Clamp01((float)hp / maxHp) : 0f;

        // Scale fill horizontally
        var fillRT = row.fill.GetComponent<RectTransform>();
        var bgRT   = row.fill.transform.parent.GetComponent<RectTransform>();
        fillRT.anchorMax = new Vector2(fraction, 1f);
        fillRT.offsetMax = Vector2.zero;

        row.label.text = $"{hp}/{maxHp}";

        // Colour shift: blue → red as HP drops
        row.fill.color = Color.Lerp(new Color(1f, 0.2f, 0.2f), new Color(0.2f, 0.4f, 1f), fraction);

        // Grey out dead robots
        if (hp <= 0)
            row.root.GetComponentInChildren<TextMeshProUGUI>().color = Color.grey;
    }

    private void HandleHpChanged(string robotId, int newHp)
    {
        var settings = ServiceLocator.GameSettings;
        int maxHp    = settings != null ? settings.MaxHp : 100;
        UpdateRow(robotId, newHp, maxHp);
    }

    private void HandleRobotDied(string robotId)
    {
        UpdateRow(robotId, 0, ServiceLocator.GameSettings?.MaxHp ?? 100);
    }

    private void ClearRows()
    {
        foreach (var kv in _rows)
            if (kv.Value.root != null) Destroy(kv.Value.root);
        _rows.Clear();
    }
}
