using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Server-level settings shown on the Main Menu screen.
/// These must be configured before entering the lobby — changing them
/// mid-session could break in-progress assignments (e.g. 2-player crews).
/// </summary>
public class ServerSettingsPanel : MonoBehaviour
{
    [SerializeField] private Toggle twoPlayerToggle;

    // Built at runtime by cloning the 2-player row — see EnsureDebugConsoleRow.
    // The debug console toggle is safe to change at any time (it only affects
    // what phones draw on top of their own UI), so unlike the 2-player toggle
    // it stays live once a game is running.
    private Toggle _debugConsoleToggle;

    private const string DebugRowName   = "DebugConsoleRow";
    private const string DebugRowLabel  = "Phone Debug Console (DBG panel)";

    private GameSettings _settings;

    private void OnEnable()
    {
        _settings = ServiceLocator.GameSettings;
        if (_settings == null) return;
        if (twoPlayerToggle)
        {
            twoPlayerToggle.SetIsOnWithoutNotify(_settings.TwoPlayerModeEnabled);
            twoPlayerToggle.onValueChanged.AddListener(OnTwoPlayerChanged);
        }

        EnsureDebugConsoleRow();
        if (_debugConsoleToggle)
        {
            _debugConsoleToggle.SetIsOnWithoutNotify(_settings.DebugConsoleEnabled);
            _debugConsoleToggle.onValueChanged.AddListener(OnDebugConsoleChanged);
        }
    }

    private void OnDisable()
    {
        if (twoPlayerToggle) twoPlayerToggle.onValueChanged.RemoveListener(OnTwoPlayerChanged);
        if (_debugConsoleToggle) _debugConsoleToggle.onValueChanged.RemoveListener(OnDebugConsoleChanged);
    }

    private void OnTwoPlayerChanged(bool enabled)
    {
        if (_settings == null) return;
        _settings.TwoPlayerModeEnabled = enabled;
        _settings.SaveToDisk();
    }

    private void OnDebugConsoleChanged(bool enabled)
    {
        if (_settings == null) return;
        _settings.DebugConsoleEnabled = enabled;
        _settings.SaveToDisk();
        ServiceLocator.PlayerServer?.BroadcastDebugConsoleEnabled(enabled);
    }

    /// <summary>
    /// Adds the debug-console row by duplicating the 2-player row that is wired in
    /// the scene, so the new row inherits its exact styling and layout without
    /// needing a second hand-authored scene hierarchy. Runs once; later calls find
    /// the existing clone and just re-grab its Toggle.
    /// </summary>
    private void EnsureDebugConsoleRow()
    {
        if (_debugConsoleToggle) return;

        var existing = transform.Find(DebugRowName);
        if (existing)
        {
            _debugConsoleToggle = existing.GetComponentInChildren<Toggle>(true);
            return;
        }

        if (twoPlayerToggle == null) return;

        // The 2-player row is the toggle's row parent: panel > row > (toggle, label).
        var sourceRow = twoPlayerToggle.transform.parent as RectTransform;
        if (sourceRow == null || sourceRow.parent != transform) return;

        var row = Instantiate(sourceRow.gameObject, transform);
        row.name = DebugRowName;

        _debugConsoleToggle = row.GetComponentInChildren<Toggle>(true);
        if (_debugConsoleToggle == null) { Destroy(row); return; }
        // Instantiate copies the source toggle's listeners; this row drives a
        // different setting, so start from a clean slate.
        _debugConsoleToggle.onValueChanged.RemoveAllListeners();

        // Relabel the row's caption — the toggle's own children (background,
        // checkmark) carry no text, so the first TMP outside it is the label.
        foreach (var text in row.GetComponentsInChildren<TMP_Text>(true))
        {
            if (text.transform.IsChildOf(_debugConsoleToggle.transform)) continue;
            text.text = DebugRowLabel;
            break;
        }

        // The panel has a fixed height (no ContentSizeFitter), so grow it by the
        // new row plus the VerticalLayoutGroup's spacing or the row is clipped.
        var panelRt = transform as RectTransform;
        var vlg     = GetComponent<VerticalLayoutGroup>();
        var le      = sourceRow.GetComponent<LayoutElement>();
        if (panelRt != null)
        {
            float rowHeight = le != null && le.preferredHeight > 0 ? le.preferredHeight : sourceRow.rect.height;
            float spacing   = vlg != null ? vlg.spacing : 0f;
            panelRt.sizeDelta = new Vector2(panelRt.sizeDelta.x, panelRt.sizeDelta.y + rowHeight + spacing);
        }
    }
}
