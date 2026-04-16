using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Lets the operator adjust MaxHp, DamagePerHit, and MatchDurationSeconds before starting.
/// All fields write directly to ServiceLocator.GameSettings.
/// </summary>
public class GameSettingsPanel : MonoBehaviour
{
    [Header("Fields")]
    [SerializeField] private TMP_InputField maxHpField;
    [SerializeField] private TMP_InputField damageField;
    [SerializeField] private TMP_InputField durationField;

    private GameSettings _settings;

    private void OnEnable()
    {
        _settings = ServiceLocator.GameSettings;
        if (_settings == null) return;

        if (maxHpField)    maxHpField.SetTextWithoutNotify(_settings.MaxHp.ToString());
        if (damageField)   damageField.SetTextWithoutNotify(_settings.DamagePerHit.ToString());
        if (durationField) durationField.SetTextWithoutNotify(_settings.MatchDurationSeconds.ToString("F0"));

        if (maxHpField)    maxHpField.onValueChanged.AddListener(OnMaxHpChanged);
        if (damageField)   damageField.onValueChanged.AddListener(OnDamageChanged);
        if (durationField) durationField.onValueChanged.AddListener(OnDurationChanged);
    }

    private void OnDisable()
    {
        if (maxHpField)    maxHpField.onValueChanged.RemoveListener(OnMaxHpChanged);
        if (damageField)   damageField.onValueChanged.RemoveListener(OnDamageChanged);
        if (durationField) durationField.onValueChanged.RemoveListener(OnDurationChanged);
    }

    private void OnMaxHpChanged(string v)
    {
        if (_settings == null) return;
        if (int.TryParse(v, out int n) && n > 0)
            _settings.MaxHp = n;
    }

    private void OnDamageChanged(string v)
    {
        if (_settings == null) return;
        if (int.TryParse(v, out int n) && n > 0)
            _settings.DamagePerHit = n;
    }

    private void OnDurationChanged(string v)
    {
        if (_settings == null) return;
        if (float.TryParse(v, out float n) && n > 0)
            _settings.MatchDurationSeconds = n;
    }
}
