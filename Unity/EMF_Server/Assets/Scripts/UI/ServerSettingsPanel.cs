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
    }

    private void OnDisable()
    {
        if (twoPlayerToggle) twoPlayerToggle.onValueChanged.RemoveListener(OnTwoPlayerChanged);
    }

    private void OnTwoPlayerChanged(bool enabled)
    {
        if (_settings == null) return;
        _settings.TwoPlayerModeEnabled = enabled;
        _settings.SaveToDisk();
    }
}
