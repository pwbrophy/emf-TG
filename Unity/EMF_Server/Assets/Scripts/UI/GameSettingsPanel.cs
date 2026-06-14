using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Lets the operator adjust match parameters in the Lobby before starting.
/// All fields write directly to ServiceLocator.GameSettings.
/// </summary>
public class GameSettingsPanel : MonoBehaviour
{
    [Header("Fields")]
    [SerializeField] private TMP_InputField maxHpField;
    [SerializeField] private TMP_InputField damageField;
    [SerializeField] private TMP_InputField durationField;
    [SerializeField] private TMP_InputField maxTeamPointsField;
    [SerializeField] private TMP_InputField killPointsField;
    [SerializeField] private TMP_InputField countdownField;

    [Header("Audio")]
    [SerializeField] private Toggle buzzerToggle;

    [Header("Two-Player Mode")]
    [SerializeField] private Toggle twoPlayerToggle;

    private GameSettings _settings;

    private void OnEnable()
    {
        _settings = ServiceLocator.GameSettings;
        if (_settings == null) return;

        if (maxHpField)         maxHpField.SetTextWithoutNotify(_settings.MaxHp.ToString());
        if (damageField)        damageField.SetTextWithoutNotify(_settings.DamagePerHit.ToString());
        if (durationField)      durationField.SetTextWithoutNotify(_settings.MatchDurationSeconds.ToString("F0"));
        if (maxTeamPointsField) maxTeamPointsField.SetTextWithoutNotify(_settings.MaxTeamPoints.ToString());
        if (killPointsField)    killPointsField.SetTextWithoutNotify(_settings.TeamPointsPerKill.ToString());
        if (countdownField)     countdownField.SetTextWithoutNotify(_settings.CountdownDuration.ToString());

        if (maxHpField)         maxHpField.onValueChanged.AddListener(OnMaxHpChanged);
        if (damageField)        damageField.onValueChanged.AddListener(OnDamageChanged);
        if (durationField)      durationField.onValueChanged.AddListener(OnDurationChanged);
        if (maxTeamPointsField) maxTeamPointsField.onValueChanged.AddListener(OnMaxTeamPointsChanged);
        if (killPointsField)    killPointsField.onValueChanged.AddListener(OnKillPointsChanged);
        if (countdownField)     countdownField.onValueChanged.AddListener(OnCountdownChanged);
        if (buzzerToggle)       buzzerToggle.SetIsOnWithoutNotify(_settings.BuzzerEnabled);
        if (buzzerToggle)       buzzerToggle.onValueChanged.AddListener(OnBuzzerChanged);
        if (twoPlayerToggle)    twoPlayerToggle.SetIsOnWithoutNotify(_settings.TwoPlayerModeEnabled);
        if (twoPlayerToggle)    twoPlayerToggle.onValueChanged.AddListener(OnTwoPlayerChanged);
    }

    private void OnDisable()
    {
        if (maxHpField)         maxHpField.onValueChanged.RemoveListener(OnMaxHpChanged);
        if (damageField)        damageField.onValueChanged.RemoveListener(OnDamageChanged);
        if (durationField)      durationField.onValueChanged.RemoveListener(OnDurationChanged);
        if (maxTeamPointsField) maxTeamPointsField.onValueChanged.RemoveListener(OnMaxTeamPointsChanged);
        if (killPointsField)    killPointsField.onValueChanged.RemoveListener(OnKillPointsChanged);
        if (countdownField)     countdownField.onValueChanged.RemoveListener(OnCountdownChanged);
        if (buzzerToggle)       buzzerToggle.onValueChanged.RemoveListener(OnBuzzerChanged);
        if (twoPlayerToggle)    twoPlayerToggle.onValueChanged.RemoveListener(OnTwoPlayerChanged);
    }

    private void OnMaxHpChanged(string v)
    {
        if (_settings == null) return;
        if (int.TryParse(v, out int n) && n > 0)
        {
            _settings.MaxHp = n;
            _settings.SaveToDisk();
        }
    }

    private void OnDamageChanged(string v)
    {
        if (_settings == null) return;
        if (int.TryParse(v, out int n) && n > 0)
        {
            _settings.DamagePerHit = n;
            _settings.SaveToDisk();
        }
    }

    private void OnDurationChanged(string v)
    {
        if (_settings == null) return;
        if (float.TryParse(v, out float n) && n > 0)
        {
            _settings.MatchDurationSeconds = n;
            _settings.SaveToDisk();
        }
    }

    private void OnMaxTeamPointsChanged(string v)
    {
        if (_settings == null) return;
        if (int.TryParse(v, out int n) && n > 0)
        {
            _settings.MaxTeamPoints = n;
            _settings.SaveToDisk();
        }
    }

    private void OnKillPointsChanged(string v)
    {
        if (_settings == null) return;
        if (int.TryParse(v, out int n) && n >= 0)
        {
            _settings.TeamPointsPerKill = n;
            _settings.SaveToDisk();
        }
    }

    private void OnCountdownChanged(string v)
    {
        if (_settings == null) return;
        if (int.TryParse(v, out int n) && n >= 1 && n <= 60)
        {
            _settings.CountdownDuration = n;
            _settings.SaveToDisk();
        }
    }

    private void OnBuzzerChanged(bool enabled)
    {
        if (_settings == null) return;
        _settings.BuzzerEnabled = enabled;
        _settings.SaveToDisk();
        ServiceLocator.RobotServer?.BroadcastBuzzerToAll(enabled);
    }

    private void OnTwoPlayerChanged(bool enabled)
    {
        if (_settings == null) return;
        _settings.TwoPlayerModeEnabled = enabled;
        _settings.SaveToDisk();
        ServiceLocator.PlayerServer?.BroadcastTwoPlayerModeChanged(enabled);
    }
}
