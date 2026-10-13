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
    [SerializeField] private TMP_InputField deadTimeField;
    [SerializeField] private TMP_InputField invulnField;
    [SerializeField] private Toggle         buzzerToggle;
    [SerializeField] private TMP_InputField slowTurretField;
    [SerializeField] private TMP_InputField turretMinField;
    [SerializeField] private TMP_InputField turretPulseField;
    [SerializeField] private TMP_InputField turretKickField;
    [SerializeField] private Toggle         turretBrakeToggle;
    [SerializeField] private TMP_InputField turretBacklashField;
    [SerializeField] private TMP_InputField driveMinField;
    [SerializeField] private TMP_InputField drivePulseField;
    [SerializeField] private TMP_InputField driveKickField;
    [SerializeField] private Toggle         driveBrakeToggle;
    [SerializeField] private TMP_InputField driveBacklashField;
    [SerializeField] private TMP_InputField joyDeadzoneField;
    [SerializeField] private TMP_InputField joyStartField;
    [SerializeField] private TMP_InputField joyFullField;
    [SerializeField] private TMP_InputField joyCurveField;
    [SerializeField] private TMP_InputField joyAxisSnapField;
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
        if (deadTimeField)   deadTimeField.SetTextWithoutNotify(_settings.ExplosionDurationSeconds.ToString("F1"));
        if (invulnField)     invulnField.SetTextWithoutNotify(_settings.InvulnerabilitySeconds.ToString("F1"));
        if (buzzerToggle)    buzzerToggle.SetIsOnWithoutNotify(_settings.BuzzerEnabled);
        if (slowTurretField) slowTurretField.SetTextWithoutNotify(_settings.SlowTurretSpeed.ToString("F2"));
        if (turretMinField)    turretMinField.SetTextWithoutNotify(_settings.TurretMinSpeed.ToString("F2"));
        if (turretPulseField)  turretPulseField.SetTextWithoutNotify(_settings.TurretPulseMs.ToString());
        if (turretKickField)   turretKickField.SetTextWithoutNotify(_settings.TurretKickMs.ToString());
        if (turretBrakeToggle) turretBrakeToggle.SetIsOnWithoutNotify(_settings.TurretBrake);
        if (turretBacklashField) turretBacklashField.SetTextWithoutNotify(_settings.TurretBacklashMs.ToString());
        if (driveMinField)      driveMinField.SetTextWithoutNotify(_settings.DriveMinSpeed.ToString("F2"));
        if (drivePulseField)    drivePulseField.SetTextWithoutNotify(_settings.DrivePulseMs.ToString());
        if (driveKickField)     driveKickField.SetTextWithoutNotify(_settings.DriveKickMs.ToString());
        if (driveBrakeToggle)   driveBrakeToggle.SetIsOnWithoutNotify(_settings.DriveBrake);
        if (driveBacklashField) driveBacklashField.SetTextWithoutNotify(_settings.DriveBacklashMs.ToString());
        if (joyDeadzoneField)   joyDeadzoneField.SetTextWithoutNotify(_settings.JoystickDeadzone.ToString("F2"));
        if (joyStartField)      joyStartField.SetTextWithoutNotify(_settings.JoystickStartSpeed.ToString("F2"));
        if (joyFullField)       joyFullField.SetTextWithoutNotify(_settings.JoystickFullAt.ToString("F2"));
        if (joyCurveField)      joyCurveField.SetTextWithoutNotify(_settings.JoystickCurve.ToString("F1"));
        if (joyAxisSnapField)   joyAxisSnapField.SetTextWithoutNotify(_settings.JoystickAxisSnap.ToString("F2"));
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
        if (deadTimeField)   deadTimeField.onValueChanged.AddListener(OnDeadTime);
        if (invulnField)     invulnField.onValueChanged.AddListener(OnInvuln);
        if (buzzerToggle)    buzzerToggle.onValueChanged.AddListener(OnBuzzer);
        if (slowTurretField) slowTurretField.onValueChanged.AddListener(OnSlowTurret);
        if (turretMinField)    turretMinField.onValueChanged.AddListener(OnTurretMin);
        if (turretPulseField)  turretPulseField.onValueChanged.AddListener(OnTurretPulse);
        if (turretKickField)   turretKickField.onValueChanged.AddListener(OnTurretKick);
        if (turretBrakeToggle) turretBrakeToggle.onValueChanged.AddListener(OnTurretBrake);
        if (turretBacklashField) turretBacklashField.onValueChanged.AddListener(OnTurretBacklash);
        if (driveMinField)      driveMinField.onValueChanged.AddListener(OnDriveMin);
        if (drivePulseField)    drivePulseField.onValueChanged.AddListener(OnDrivePulse);
        if (driveKickField)     driveKickField.onValueChanged.AddListener(OnDriveKick);
        if (driveBrakeToggle)   driveBrakeToggle.onValueChanged.AddListener(OnDriveBrake);
        if (driveBacklashField) driveBacklashField.onValueChanged.AddListener(OnDriveBacklash);
        if (joyDeadzoneField)   joyDeadzoneField.onValueChanged.AddListener(OnJoyDeadzone);
        if (joyStartField)      joyStartField.onValueChanged.AddListener(OnJoyStart);
        if (joyFullField)       joyFullField.onValueChanged.AddListener(OnJoyFull);
        if (joyCurveField)      joyCurveField.onValueChanged.AddListener(OnJoyCurve);
        if (joyAxisSnapField)   joyAxisSnapField.onValueChanged.AddListener(OnJoyAxisSnap);
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
        if (deadTimeField)   deadTimeField.onValueChanged.RemoveListener(OnDeadTime);
        if (invulnField)     invulnField.onValueChanged.RemoveListener(OnInvuln);
        if (buzzerToggle)    buzzerToggle.onValueChanged.RemoveListener(OnBuzzer);
        if (slowTurretField) slowTurretField.onValueChanged.RemoveListener(OnSlowTurret);
        if (turretMinField)    turretMinField.onValueChanged.RemoveListener(OnTurretMin);
        if (turretPulseField)  turretPulseField.onValueChanged.RemoveListener(OnTurretPulse);
        if (turretKickField)   turretKickField.onValueChanged.RemoveListener(OnTurretKick);
        if (turretBrakeToggle) turretBrakeToggle.onValueChanged.RemoveListener(OnTurretBrake);
        if (turretBacklashField) turretBacklashField.onValueChanged.RemoveListener(OnTurretBacklash);
        if (driveMinField)      driveMinField.onValueChanged.RemoveListener(OnDriveMin);
        if (drivePulseField)    drivePulseField.onValueChanged.RemoveListener(OnDrivePulse);
        if (driveKickField)     driveKickField.onValueChanged.RemoveListener(OnDriveKick);
        if (driveBrakeToggle)   driveBrakeToggle.onValueChanged.RemoveListener(OnDriveBrake);
        if (driveBacklashField) driveBacklashField.onValueChanged.RemoveListener(OnDriveBacklash);
        if (joyDeadzoneField)   joyDeadzoneField.onValueChanged.RemoveListener(OnJoyDeadzone);
        if (joyStartField)      joyStartField.onValueChanged.RemoveListener(OnJoyStart);
        if (joyFullField)       joyFullField.onValueChanged.RemoveListener(OnJoyFull);
        if (joyCurveField)      joyCurveField.onValueChanged.RemoveListener(OnJoyCurve);
        if (joyAxisSnapField)   joyAxisSnapField.onValueChanged.RemoveListener(OnJoyAxisSnap);
        if (videoResButton)    videoResButton.onClick.RemoveListener(OnVideoResCycle);
        if (videoFpsField)     videoFpsField.onValueChanged.RemoveListener(OnVideoFps);
        if (videoQualityField) videoQualityField.onValueChanged.RemoveListener(OnVideoQuality);
    }

    void OnDamage(string v)    { if (_settings != null && int.TryParse(v, out int n) && n > 0) { _settings.DamagePerHit = n; _settings.SaveToDisk(); } }
    void OnSideMult(string v)  { if (_settings != null && float.TryParse(v, out float n) && n >= 0) { _settings.SideMultiplier = n; _settings.SaveToDisk(); } }
    void OnRearMult(string v)  { if (_settings != null && float.TryParse(v, out float n) && n >= 0) { _settings.RearMultiplier = n; _settings.SaveToDisk(); } }
    void OnKillPoints(string v){ if (_settings != null && int.TryParse(v, out int n) && n >= 0) { _settings.TeamPointsPerKill = n; _settings.SaveToDisk(); } }
    void OnCooldown(string v)  { if (_settings != null && float.TryParse(v, out float n) && n >= 0) { _settings.FireCooldownSeconds = n; _settings.SaveToDisk(); } }
    void OnDeadTime(string v)  { if (_settings != null && float.TryParse(v, out float n) && n >= 0) { _settings.ExplosionDurationSeconds = n; _settings.SaveToDisk(); } }
    void OnInvuln(string v)    { if (_settings != null && float.TryParse(v, out float n) && n >= 0) { _settings.InvulnerabilitySeconds = n; _settings.SaveToDisk(); } }
    void OnBuzzer(bool on)     { if (_settings != null) { _settings.BuzzerEnabled = on; _settings.SaveToDisk(); ServiceLocator.RobotServer?.BroadcastBuzzerToAll(on); } }
    void OnSlowTurret(string v){ if (_settings != null && float.TryParse(v, out float n) && n > 0f && n <= 1f) { _settings.SlowTurretSpeed = n; _settings.SaveToDisk(); ServiceLocator.PlayerServer?.BroadcastTurretSettings(); } }
    void OnTurretMin(string v)   { if (_settings != null && float.TryParse(v, out float n) && n >= 0f && n <= 1f) { _settings.TurretMinSpeed = n; SavePhysics(); } }
    void OnTurretPulse(string v) { if (_settings != null && int.TryParse(v, out int n) && n >= 10 && n <= 1000) { _settings.TurretPulseMs = n; SavePhysics(); } }
    void OnTurretKick(string v)  { if (_settings != null && int.TryParse(v, out int n) && n >= 0 && n <= 200) { _settings.TurretKickMs = n; SavePhysics(); } }
    void OnTurretBrake(bool on)  { if (_settings != null) { _settings.TurretBrake = on; SavePhysics(); } }
    void OnTurretBacklash(string v) { if (_settings != null && int.TryParse(v, out int n) && n >= 0 && n <= 300) { _settings.TurretBacklashMs = n; SavePhysics(); } }
    void OnDriveMin(string v)      { if (_settings != null && float.TryParse(v, out float n) && n >= 0f && n <= 1f) { _settings.DriveMinSpeed = n; SavePhysics(); } }
    void OnDrivePulse(string v)    { if (_settings != null && int.TryParse(v, out int n) && n >= 10 && n <= 1000) { _settings.DrivePulseMs = n; SavePhysics(); } }
    void OnDriveKick(string v)     { if (_settings != null && int.TryParse(v, out int n) && n >= 0 && n <= 200) { _settings.DriveKickMs = n; SavePhysics(); } }
    void OnDriveBrake(bool on)     { if (_settings != null) { _settings.DriveBrake = on; SavePhysics(); } }
    void OnDriveBacklash(string v) { if (_settings != null && int.TryParse(v, out int n) && n >= 0 && n <= 300) { _settings.DriveBacklashMs = n; SavePhysics(); } }
    void OnJoyDeadzone(string v) { if (_settings != null && float.TryParse(v, out float n) && n >= 0f && n < 0.5f) { _settings.JoystickDeadzone = n; SaveJoystick(); } }
    void OnJoyStart(string v)    { if (_settings != null && float.TryParse(v, out float n) && n >= 0f && n <= 1f) { _settings.JoystickStartSpeed = n; SaveJoystick(); } }
    void OnJoyFull(string v)     { if (_settings != null && float.TryParse(v, out float n) && n > 0.5f && n <= 1f) { _settings.JoystickFullAt = n; SaveJoystick(); } }
    void OnJoyCurve(string v)    { if (_settings != null && float.TryParse(v, out float n) && n >= 0.2f && n <= 5f) { _settings.JoystickCurve = n; SaveJoystick(); } }
    void OnJoyAxisSnap(string v) { if (_settings != null && float.TryParse(v, out float n) && n >= 0f && n <= 0.5f) { _settings.JoystickAxisSnap = n; SaveJoystick(); } }
    void SaveJoystick()          { _settings.SaveToDisk(); ServiceLocator.PlayerServer?.BroadcastJoystickSettings(); }
    void SavePhysics()           { _settings.SaveToDisk(); ServiceLocator.RobotServer?.BroadcastPhysicsToAll(_settings); }

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
