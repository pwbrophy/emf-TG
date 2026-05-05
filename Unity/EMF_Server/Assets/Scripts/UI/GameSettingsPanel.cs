using TMPro;
using UnityEngine;

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

    private GameSettings _settings;

    private void OnEnable()
    {
        _settings = ServiceLocator.GameSettings;
        if (_settings == null) return;

        if (maxHpField)         maxHpField.SetTextWithoutNotify(_settings.MaxHp.ToString());
        if (damageField)        damageField.SetTextWithoutNotify(_settings.DamagePerHit.ToString());
        if (durationField)      durationField.SetTextWithoutNotify(_settings.MatchDurationSeconds.ToString("F0"));
        if (maxTeamPointsField) maxTeamPointsField.SetTextWithoutNotify(_settings.MaxTeamPoints.ToString());

        if (maxHpField)         maxHpField.onValueChanged.AddListener(OnMaxHpChanged);
        if (damageField)        damageField.onValueChanged.AddListener(OnDamageChanged);
        if (durationField)      durationField.onValueChanged.AddListener(OnDurationChanged);
        if (maxTeamPointsField) maxTeamPointsField.onValueChanged.AddListener(OnMaxTeamPointsChanged);
    }

    private void OnDisable()
    {
        if (maxHpField)         maxHpField.onValueChanged.RemoveListener(OnMaxHpChanged);
        if (damageField)        damageField.onValueChanged.RemoveListener(OnDamageChanged);
        if (durationField)      durationField.onValueChanged.RemoveListener(OnDurationChanged);
        if (maxTeamPointsField) maxTeamPointsField.onValueChanged.RemoveListener(OnMaxTeamPointsChanged);
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
}
