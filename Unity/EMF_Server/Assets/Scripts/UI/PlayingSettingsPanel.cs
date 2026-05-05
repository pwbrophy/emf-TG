using TMPro;
using UnityEngine;

// Exposes all GameSettings (match params + shot timing) as editable fields on the
// Playing screen. Values auto-save to disk on each change via GameSettings.SaveToDisk().
// The IrSlotScheduler reads GameSettings live on each shot, so changes take effect
// immediately without restarting.
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

    [Header("Shot Timing fields")]
    [SerializeField] private TMP_InputField cooldownField;
    [SerializeField] private TMP_InputField slotFutureField;
    [SerializeField] private TMP_InputField listenDelayField;
    [SerializeField] private TMP_InputField b1DurField;
    [SerializeField] private TMP_InputField gap12Field;
    [SerializeField] private TMP_InputField b2DurField;
    [SerializeField] private TMP_InputField repGapField;
    [SerializeField] private TMP_InputField repsField;
    [SerializeField] private TMP_InputField resultBufField;

    [Header("Computed display")]
    [SerializeField] private TMP_Text totalTimeLabel;

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

    // ── Population ────────────────────────────────────────────────────────────────

    private void PopulateAll()
    {
        Set(maxHpField,      _settings.MaxHp.ToString());
        Set(damageField,     _settings.DamagePerHit.ToString());
        Set(rearMultField,   _settings.RearMultiplier.ToString("F1"));
        Set(durationField,   _settings.MatchDurationSeconds.ToString("F0"));
        Set(maxPlayersField, _settings.MaxPlayers.ToString());
        Set(maxTeamPtsField, _settings.MaxTeamPoints.ToString());
        Set(ptsPerKillField, _settings.TeamPointsPerKill.ToString());

        Set(cooldownField,   _settings.FireCooldownSeconds.ToString("F1"));
        Set(slotFutureField, _settings.SlotFutureMs.ToString());
        Set(listenDelayField,_settings.ListenDelayMs.ToString());
        Set(b1DurField,      _settings.B1DurMs.ToString());
        Set(gap12Field,      _settings.Gap12Ms.ToString());
        Set(b2DurField,      _settings.B2DurMs.ToString());
        Set(repGapField,     _settings.RepGapMs.ToString());
        Set(repsField,       _settings.Reps.ToString());
        Set(resultBufField,  _settings.ResultBufferSeconds.ToString("F2"));

        UpdateTotal();
    }

    private static void Set(TMP_InputField f, string v)
    {
        if (f != null) f.SetTextWithoutNotify(v);
    }

    // ── Total time computation ────────────────────────────────────────────────────

    private void UpdateTotal()
    {
        if (totalTimeLabel == null || _settings == null) return;
        int   perRep   = _settings.B1DurMs + _settings.Gap12Ms + _settings.B2DurMs + _settings.RepGapMs;
        int   slotMs   = _settings.Reps * perRep - _settings.RepGapMs;
        float totalSec = (_settings.SlotFutureMs + slotMs) / 1000f + _settings.ResultBufferSeconds;
        totalTimeLabel.text = $"Total: {totalSec:F2}s";
    }

    // ── Listeners ────────────────────────────────────────────────────────────────

    private void AddListeners()
    {
        Reg(maxHpField,       OnMaxHpChanged);
        Reg(damageField,      OnDamageChanged);
        Reg(rearMultField,    OnRearMultChanged);
        Reg(durationField,    OnDurationChanged);
        Reg(maxPlayersField,  OnMaxPlayersChanged);
        Reg(maxTeamPtsField,  OnMaxTeamPtsChanged);
        Reg(ptsPerKillField,  OnPtsPerKillChanged);
        Reg(cooldownField,    OnCooldownChanged);
        Reg(slotFutureField,  OnSlotFutureChanged);
        Reg(listenDelayField, OnListenDelayChanged);
        Reg(b1DurField,       OnB1DurChanged);
        Reg(gap12Field,       OnGap12Changed);
        Reg(b2DurField,       OnB2DurChanged);
        Reg(repGapField,      OnRepGapChanged);
        Reg(repsField,        OnRepsChanged);
        Reg(resultBufField,   OnResultBufChanged);
    }

    private void RemoveListeners()
    {
        Unreg(maxHpField,       OnMaxHpChanged);
        Unreg(damageField,      OnDamageChanged);
        Unreg(rearMultField,    OnRearMultChanged);
        Unreg(durationField,    OnDurationChanged);
        Unreg(maxPlayersField,  OnMaxPlayersChanged);
        Unreg(maxTeamPtsField,  OnMaxTeamPtsChanged);
        Unreg(ptsPerKillField,  OnPtsPerKillChanged);
        Unreg(cooldownField,    OnCooldownChanged);
        Unreg(slotFutureField,  OnSlotFutureChanged);
        Unreg(listenDelayField, OnListenDelayChanged);
        Unreg(b1DurField,       OnB1DurChanged);
        Unreg(gap12Field,       OnGap12Changed);
        Unreg(b2DurField,       OnB2DurChanged);
        Unreg(repGapField,      OnRepGapChanged);
        Unreg(repsField,        OnRepsChanged);
        Unreg(resultBufField,   OnResultBufChanged);
    }

    private static void Reg(TMP_InputField f, UnityEngine.Events.UnityAction<string> cb)
    {
        if (f != null) f.onValueChanged.AddListener(cb);
    }

    private static void Unreg(TMP_InputField f, UnityEngine.Events.UnityAction<string> cb)
    {
        if (f != null) f.onValueChanged.RemoveListener(cb);
    }

    // ── Change handlers ───────────────────────────────────────────────────────────

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

    private void OnCooldownChanged(string v)
    {
        if (_settings == null) return;
        if (float.TryParse(v, out float n) && n > 0) { _settings.FireCooldownSeconds = n; Save(); UpdateTotal(); }
    }

    private void OnSlotFutureChanged(string v)
    {
        if (_settings == null) return;
        if (int.TryParse(v, out int n) && n >= 0) { _settings.SlotFutureMs = n; Save(); UpdateTotal(); }
    }

    private void OnListenDelayChanged(string v)
    {
        if (_settings == null) return;
        if (int.TryParse(v, out int n) && n >= 0) { _settings.ListenDelayMs = n; Save(); }
    }

    private void OnB1DurChanged(string v)
    {
        if (_settings == null) return;
        if (int.TryParse(v, out int n) && n > 0) { _settings.B1DurMs = n; Save(); UpdateTotal(); }
    }

    private void OnGap12Changed(string v)
    {
        if (_settings == null) return;
        if (int.TryParse(v, out int n) && n >= 0) { _settings.Gap12Ms = n; Save(); UpdateTotal(); }
    }

    private void OnB2DurChanged(string v)
    {
        if (_settings == null) return;
        if (int.TryParse(v, out int n) && n > 0) { _settings.B2DurMs = n; Save(); UpdateTotal(); }
    }

    private void OnRepGapChanged(string v)
    {
        if (_settings == null) return;
        if (int.TryParse(v, out int n) && n >= 0) { _settings.RepGapMs = n; Save(); UpdateTotal(); }
    }

    private void OnRepsChanged(string v)
    {
        if (_settings == null) return;
        if (int.TryParse(v, out int n) && n > 0) { _settings.Reps = n; Save(); UpdateTotal(); }
    }

    private void OnResultBufChanged(string v)
    {
        if (_settings == null) return;
        if (float.TryParse(v, out float n) && n >= 0) { _settings.ResultBufferSeconds = n; Save(); UpdateTotal(); }
    }

    private void Save()
    {
        _settings.SaveToDisk();
    }
}
