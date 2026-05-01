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
    [SerializeField] private TMP_InputField maxTeamPointsField;
    [SerializeField] private TMP_InputField alliance0BaseField;
    [SerializeField] private TMP_InputField alliance1BaseField;

    private GameSettings _settings;

    private void OnEnable()
    {
        _settings = ServiceLocator.GameSettings;
        if (_settings == null) return;

        if (maxHpField)          maxHpField.SetTextWithoutNotify(_settings.MaxHp.ToString());
        if (damageField)         damageField.SetTextWithoutNotify(_settings.DamagePerHit.ToString());
        if (durationField)       durationField.SetTextWithoutNotify(_settings.MatchDurationSeconds.ToString("F0"));
        if (maxTeamPointsField)  maxTeamPointsField.SetTextWithoutNotify(_settings.MaxTeamPoints.ToString());
        if (alliance0BaseField)  alliance0BaseField.SetTextWithoutNotify(_settings.Alliance0BaseUid ?? "");
        if (alliance1BaseField)  alliance1BaseField.SetTextWithoutNotify(_settings.Alliance1BaseUid ?? "");

        if (maxHpField)          maxHpField.onValueChanged.AddListener(OnMaxHpChanged);
        if (damageField)         damageField.onValueChanged.AddListener(OnDamageChanged);
        if (durationField)       durationField.onValueChanged.AddListener(OnDurationChanged);
        if (maxTeamPointsField)  maxTeamPointsField.onValueChanged.AddListener(OnMaxTeamPointsChanged);
        if (alliance0BaseField)  alliance0BaseField.onValueChanged.AddListener(OnAlliance0BaseChanged);
        if (alliance1BaseField)  alliance1BaseField.onValueChanged.AddListener(OnAlliance1BaseChanged);
    }

    private void OnDisable()
    {
        if (maxHpField)          maxHpField.onValueChanged.RemoveListener(OnMaxHpChanged);
        if (damageField)         damageField.onValueChanged.RemoveListener(OnDamageChanged);
        if (durationField)       durationField.onValueChanged.RemoveListener(OnDurationChanged);
        if (maxTeamPointsField)  maxTeamPointsField.onValueChanged.RemoveListener(OnMaxTeamPointsChanged);
        if (alliance0BaseField)  alliance0BaseField.onValueChanged.RemoveListener(OnAlliance0BaseChanged);
        if (alliance1BaseField)  alliance1BaseField.onValueChanged.RemoveListener(OnAlliance1BaseChanged);
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

    private void OnMaxTeamPointsChanged(string v)
    {
        if (_settings == null) return;
        if (int.TryParse(v, out int n) && n > 0)
            _settings.MaxTeamPoints = n;
    }

    private void OnAlliance0BaseChanged(string v)
    {
        if (_settings != null) _settings.Alliance0BaseUid = v.Trim();
    }

    private void OnAlliance1BaseChanged(string v)
    {
        if (_settings != null) _settings.Alliance1BaseUid = v.Trim();
    }
}
