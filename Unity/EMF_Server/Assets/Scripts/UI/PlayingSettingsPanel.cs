using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Live game-settings panel shown on the Playing screen.
/// Fields are wired by PlayingPanelBuilder — do not self-build here.
/// </summary>
public class PlayingSettingsPanel : MonoBehaviour
{
    [SerializeField] private TMP_InputField damageField;
    [SerializeField] private TMP_InputField sideMultField;
    [SerializeField] private TMP_InputField rearMultField;
    [SerializeField] private TMP_InputField killPointsField;
    [SerializeField] private TMP_InputField cooldownField;
    [SerializeField] private TMP_InputField invulnField;
    [SerializeField] private Toggle         buzzerToggle;
    [SerializeField] private TMP_InputField slowTurretField;
    [SerializeField] private Button          videoResButton;
    [SerializeField] private TextMeshProUGUI videoResLabel;
    [SerializeField] private TMP_InputField  videoFpsField;
    [SerializeField] private TMP_InputField  videoQualityField;

    // Resolution index → operator-facing name (matches firmware videoIdxToFrameSize)
    private static readonly string[] ResNames =
        { "QVGA 320x240", "CIF 400x296", "HVGA 480x320", "VGA 640x480" };
    static string ResName(int idx) => (idx >= 0 && idx < ResNames.Length) ? ResNames[idx] : ResNames[2];

    private GameSettings _settings;

    private void OnEnable()
    {
        _settings = ServiceLocator.GameSettings;
        if (_settings == null) return;
        Populate();
        Subscribe();
    }

    private void OnDisable() => Unsubscribe();

    void Populate()
    {
        if (damageField)     damageField.SetTextWithoutNotify(_settings.DamagePerHit.ToString());
        if (sideMultField)   sideMultField.SetTextWithoutNotify(_settings.SideMultiplier.ToString("F1"));
        if (rearMultField)   rearMultField.SetTextWithoutNotify(_settings.RearMultiplier.ToString("F1"));
        if (killPointsField) killPointsField.SetTextWithoutNotify(_settings.TeamPointsPerKill.ToString());
        if (cooldownField)   cooldownField.SetTextWithoutNotify(_settings.FireCooldownSeconds.ToString("F1"));
        if (invulnField)     invulnField.SetTextWithoutNotify(_settings.InvulnerabilitySeconds.ToString("F1"));
        if (buzzerToggle)    buzzerToggle.SetIsOnWithoutNotify(_settings.BuzzerEnabled);
        if (slowTurretField) slowTurretField.SetTextWithoutNotify(_settings.SlowTurretSpeed.ToString("F2"));
        if (videoResLabel)     videoResLabel.text = ResName(_settings.VideoFrameSize);
        if (videoFpsField)     videoFpsField.SetTextWithoutNotify(_settings.VideoFps.ToString());
        if (videoQualityField) videoQualityField.SetTextWithoutNotify(_settings.VideoJpegQuality.ToString());
    }

    void Subscribe()
    {
        if (damageField)     damageField.onValueChanged.AddListener(OnDamage);
        if (sideMultField)   sideMultField.onValueChanged.AddListener(OnSideMult);
        if (rearMultField)   rearMultField.onValueChanged.AddListener(OnRearMult);
        if (killPointsField) killPointsField.onValueChanged.AddListener(OnKillPoints);
        if (cooldownField)   cooldownField.onValueChanged.AddListener(OnCooldown);
        if (invulnField)     invulnField.onValueChanged.AddListener(OnInvuln);
        if (buzzerToggle)    buzzerToggle.onValueChanged.AddListener(OnBuzzer);
        if (slowTurretField) slowTurretField.onValueChanged.AddListener(OnSlowTurret);
        if (videoResButton)    videoResButton.onClick.AddListener(OnVideoResCycle);
        if (videoFpsField)     videoFpsField.onValueChanged.AddListener(OnVideoFps);
        if (videoQualityField) videoQualityField.onValueChanged.AddListener(OnVideoQuality);
    }

    void Unsubscribe()
    {
        if (damageField)     damageField.onValueChanged.RemoveListener(OnDamage);
        if (sideMultField)   sideMultField.onValueChanged.RemoveListener(OnSideMult);
        if (rearMultField)   rearMultField.onValueChanged.RemoveListener(OnRearMult);
        if (killPointsField) killPointsField.onValueChanged.RemoveListener(OnKillPoints);
        if (cooldownField)   cooldownField.onValueChanged.RemoveListener(OnCooldown);
        if (invulnField)     invulnField.onValueChanged.RemoveListener(OnInvuln);
        if (buzzerToggle)    buzzerToggle.onValueChanged.RemoveListener(OnBuzzer);
        if (slowTurretField) slowTurretField.onValueChanged.RemoveListener(OnSlowTurret);
        if (videoResButton)    videoResButton.onClick.RemoveListener(OnVideoResCycle);
        if (videoFpsField)     videoFpsField.onValueChanged.RemoveListener(OnVideoFps);
        if (videoQualityField) videoQualityField.onValueChanged.RemoveListener(OnVideoQuality);
    }

    void OnDamage(string v)    { if (_settings != null && int.TryParse(v, out int n) && n > 0) { _settings.DamagePerHit = n; _settings.SaveToDisk(); } }
    void OnSideMult(string v)  { if (_settings != null && float.TryParse(v, out float n) && n >= 0) { _settings.SideMultiplier = n; _settings.SaveToDisk(); } }
    void OnRearMult(string v)  { if (_settings != null && float.TryParse(v, out float n) && n >= 0) { _settings.RearMultiplier = n; _settings.SaveToDisk(); } }
    void OnKillPoints(string v){ if (_settings != null && int.TryParse(v, out int n) && n >= 0) { _settings.TeamPointsPerKill = n; _settings.SaveToDisk(); } }
    void OnCooldown(string v)  { if (_settings != null && float.TryParse(v, out float n) && n >= 0) { _settings.FireCooldownSeconds = n; _settings.SaveToDisk(); } }
    void OnInvuln(string v)    { if (_settings != null && float.TryParse(v, out float n) && n >= 0) { _settings.InvulnerabilitySeconds = n; _settings.SaveToDisk(); } }
    void OnBuzzer(bool on)     { if (_settings != null) { _settings.BuzzerEnabled = on; _settings.SaveToDisk(); ServiceLocator.RobotServer?.BroadcastBuzzerToAll(on); } }
    void OnSlowTurret(string v){ if (_settings != null && float.TryParse(v, out float n) && n > 0f && n <= 1f) { _settings.SlowTurretSpeed = n; _settings.SaveToDisk(); ServiceLocator.PlayerServer?.BroadcastTurretSettings(); } }

    void OnVideoResCycle()
    {
        if (_settings == null) return;
        _settings.VideoFrameSize = (Mathf.Clamp(_settings.VideoFrameSize, 0, ResNames.Length - 1) + 1) % ResNames.Length;
        if (videoResLabel) videoResLabel.text = ResName(_settings.VideoFrameSize);
        _settings.SaveToDisk();
        ServiceLocator.RobotServer?.BroadcastVideoConfigToAll(_settings);
    }
    void OnVideoFps(string v)     { if (_settings != null && int.TryParse(v, out int n) && n >= 1 && n <= 30) { _settings.VideoFps = n; _settings.SaveToDisk(); ServiceLocator.RobotServer?.BroadcastVideoConfigToAll(_settings); } }
    void OnVideoQuality(string v) { if (_settings != null && int.TryParse(v, out int n) && n >= 8 && n <= 40) { _settings.VideoJpegQuality = n; _settings.SaveToDisk(); ServiceLocator.RobotServer?.BroadcastVideoConfigToAll(_settings); } }
}
