using TMPro;
using UnityEngine;
using UnityEngine.UI;

// Exposes GameSettings (match params) as editable fields on the Playing screen.
// Values auto-save to disk on each change via GameSettings.SaveToDisk().
public class PlayingSettingsPanel : MonoBehaviour
{
    [Header("Game Settings fields")]
    [SerializeField] private TMP_InputField maxHpField;
    [SerializeField] private TMP_InputField damageField;
    [SerializeField] private TMP_InputField rearMultField;
    [SerializeField] private TMP_InputField durationField;
    [SerializeField] private TMP_InputField maxPlayersField;
    [SerializeField] private TMP_InputField maxTeamPtsField;
    [SerializeField] private TMP_InputField ptsPerKillField;
    [SerializeField] private TMP_InputField slowTurretSpeedField;

    [Header("Audio")]
    [SerializeField] private Toggle buzzerToggle;

    private GameSettings _settings;

    private void OnEnable()
    {
        _settings = ServiceLocator.GameSettings;
        if (_settings == null) return;
        PopulateAll();
        AddListeners();
    }

    private void OnDisable()
    {
        RemoveListeners();
    }

    private void PopulateAll()
    {
        Set(maxHpField,      _settings.MaxHp.ToString());
        Set(damageField,     _settings.DamagePerHit.ToString());
        Set(rearMultField,   _settings.RearMultiplier.ToString("F1"));
        Set(durationField,   _settings.MatchDurationSeconds.ToString("F0"));
        Set(maxPlayersField, _settings.MaxPlayers.ToString());
        Set(maxTeamPtsField,       _settings.MaxTeamPoints.ToString());
        Set(ptsPerKillField,       _settings.TeamPointsPerKill.ToString());
        Set(slowTurretSpeedField,  _settings.SlowTurretSpeed.ToString("F2"));
        if (buzzerToggle != null) buzzerToggle.SetIsOnWithoutNotify(_settings.BuzzerEnabled);
    }

    private static void Set(TMP_InputField f, string v)
    {
        if (f != null) f.SetTextWithoutNotify(v);
    }

    private void AddListeners()
    {
        Reg(maxHpField,      OnMaxHpChanged);
        Reg(damageField,     OnDamageChanged);
        Reg(rearMultField,   OnRearMultChanged);
        Reg(durationField,   OnDurationChanged);
        Reg(maxPlayersField, OnMaxPlayersChanged);
        Reg(maxTeamPtsField, OnMaxTeamPtsChanged);
        Reg(ptsPerKillField,      OnPtsPerKillChanged);
        Reg(slowTurretSpeedField, OnSlowTurretSpeedChanged);
        if (buzzerToggle != null) buzzerToggle.onValueChanged.AddListener(OnBuzzerChanged);
    }

    private void RemoveListeners()
    {
        Unreg(maxHpField,      OnMaxHpChanged);
        Unreg(damageField,     OnDamageChanged);
        Unreg(rearMultField,   OnRearMultChanged);
        Unreg(durationField,   OnDurationChanged);
        Unreg(maxPlayersField, OnMaxPlayersChanged);
        Unreg(maxTeamPtsField, OnMaxTeamPtsChanged);
        Unreg(ptsPerKillField,      OnPtsPerKillChanged);
        Unreg(slowTurretSpeedField, OnSlowTurretSpeedChanged);
        if (buzzerToggle != null) buzzerToggle.onValueChanged.RemoveListener(OnBuzzerChanged);
    }

    private static void Reg(TMP_InputField f, UnityEngine.Events.UnityAction<string> cb)
    {
        if (f != null) f.onValueChanged.AddListener(cb);
    }

    private static void Unreg(TMP_InputField f, UnityEngine.Events.UnityAction<string> cb)
    {
        if (f != null) f.onValueChanged.RemoveListener(cb);
    }

    private void OnMaxHpChanged(string v)
    {
        if (_settings == null) return;
        if (int.TryParse(v, out int n) && n > 0) { _settings.MaxHp = n; Save(); }
    }

    private void OnDamageChanged(string v)
    {
        if (_settings == null) return;
        if (int.TryParse(v, out int n) && n > 0) { _settings.DamagePerHit = n; Save(); }
    }

    private void OnRearMultChanged(string v)
    {
        if (_settings == null) return;
        if (float.TryParse(v, out float n) && n > 0) { _settings.RearMultiplier = n; Save(); }
    }

    private void OnDurationChanged(string v)
    {
        if (_settings == null) return;
        if (float.TryParse(v, out float n) && n > 0) { _settings.MatchDurationSeconds = n; Save(); }
    }

    private void OnMaxPlayersChanged(string v)
    {
        if (_settings == null) return;
        if (int.TryParse(v, out int n) && n > 0) { _settings.MaxPlayers = n; Save(); }
    }

    private void OnMaxTeamPtsChanged(string v)
    {
        if (_settings == null) return;
        if (int.TryParse(v, out int n) && n > 0) { _settings.MaxTeamPoints = n; Save(); }
    }

    private void OnPtsPerKillChanged(string v)
    {
        if (_settings == null) return;
        if (int.TryParse(v, out int n) && n > 0) { _settings.TeamPointsPerKill = n; Save(); }
    }

    private void OnSlowTurretSpeedChanged(string v)
    {
        if (_settings == null) return;
        if (float.TryParse(v, out float n))
        {
            n = Mathf.Clamp(n, 0.1f, 0.9f);
            _settings.SlowTurretSpeed = n;
            Save();
            ServiceLocator.PlayerServer?.BroadcastTurretSettings();
        }
    }

    private void OnBuzzerChanged(bool enabled)
    {
        if (_settings == null) return;
        _settings.BuzzerEnabled = enabled;
        Save();
        ServiceLocator.RobotServer?.BroadcastBuzzerToAll(enabled);
    }

    private void Save() => _settings.SaveToDisk();
}
