using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Unified action-button panel for the Playing panel.
/// Handles Damage, Drive-inversion, Camera, and Capture-point groups.
/// Replaces the old TestDamageButton, HealButton, FlipVideoButtons,
/// ToggleCameraButton, and RobotPingButton components.
/// </summary>
public class RobotActionButtons : MonoBehaviour
{
    [Header("Dependencies")]
    [SerializeField] private RobotListPanel robotListPanel;

    [Header("Damage")]
    [SerializeField] private Button hitFrontBtn;
    [SerializeField] private Button hitSideBtn;
    [SerializeField] private Button hitRearBtn;
    [SerializeField] private Button healBtn;

    [Header("Drive Inversion")]
    [SerializeField] private Button revThrottleBtn;
    [SerializeField] private Button revSteerBtn;
    [SerializeField] private Button revTurretBtn;

    [Header("Camera")]
    [SerializeField] private Button flipHBtn;
    [SerializeField] private Button flipVBtn;
    [SerializeField] private Button camToggleBtn;

    [Header("Capture Points")]
    [SerializeField] private Button captureNorthBtn;
    [SerializeField] private Button captureCentreBtn;
    [SerializeField] private Button captureSouthBtn;

    static readonly Color C_NEUTRAL = new Color(0.20f, 0.20f, 0.20f);
    static readonly Color C_A0      = new Color(0.10f, 0.24f, 0.44f);
    static readonly Color C_A1      = new Color(0.55f, 0.10f, 0.10f);
    static readonly Color C_DRV_OFF = new Color(0.10f, 0.18f, 0.28f);
    static readonly Color C_DRV_ON  = new Color(0.60f, 0.20f, 0.10f);

    private IRobotDirectory      _dir;
    private CapturePointService  _cp;

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    private void OnEnable()
    {
        _dir = ServiceLocator.RobotDirectory;
        _cp  = ServiceLocator.CapturePoints;

        if (robotListPanel != null)
            robotListPanel.SelectionChanged += OnSelectionChanged;

        if (_dir != null)
            _dir.OnRobotUpdated += OnRobotUpdated;

        if (_cp != null)
            _cp.OnPointCaptured += OnPointCaptured;

        // Damage
        if (hitFrontBtn  != null) hitFrontBtn.onClick.AddListener(() => ApplyHit("N"));
        if (hitSideBtn   != null) hitSideBtn.onClick.AddListener(()  => ApplyHit("E"));
        if (hitRearBtn   != null) hitRearBtn.onClick.AddListener(()  => ApplyHit("S"));
        if (healBtn      != null) healBtn.onClick.AddListener(OnHeal);

        // Drive inversion
        if (revThrottleBtn != null) revThrottleBtn.onClick.AddListener(() => ToggleDrive(DriveAxis.Throttle));
        if (revSteerBtn    != null) revSteerBtn.onClick.AddListener(()    => ToggleDrive(DriveAxis.Steer));
        if (revTurretBtn   != null) revTurretBtn.onClick.AddListener(()   => ToggleDrive(DriveAxis.Turret));

        // Camera
        if (flipHBtn    != null) flipHBtn.onClick.AddListener(() => robotListPanel?.FlipHForSelected());
        if (flipVBtn    != null) flipVBtn.onClick.AddListener(() => robotListPanel?.FlipVForSelected());
        if (camToggleBtn != null) camToggleBtn.onClick.AddListener(() => robotListPanel?.ToggleCameraForSelected());

        // Capture
        if (captureNorthBtn  != null) captureNorthBtn.onClick.AddListener(() => CycleCapture(0));
        if (captureCentreBtn != null) captureCentreBtn.onClick.AddListener(() => CycleCapture(1));
        if (captureSouthBtn  != null) captureSouthBtn.onClick.AddListener(() => CycleCapture(2));

        RefreshDriveLabels();
        RefreshCaptureColors();
    }

    private void OnDisable()
    {
        if (robotListPanel != null)
            robotListPanel.SelectionChanged -= OnSelectionChanged;

        if (_dir != null)
            _dir.OnRobotUpdated -= OnRobotUpdated;

        if (_cp != null)
            _cp.OnPointCaptured -= OnPointCaptured;

        if (hitFrontBtn    != null) hitFrontBtn.onClick.RemoveAllListeners();
        if (hitSideBtn     != null) hitSideBtn.onClick.RemoveAllListeners();
        if (hitRearBtn     != null) hitRearBtn.onClick.RemoveAllListeners();
        if (healBtn        != null) healBtn.onClick.RemoveAllListeners();
        if (revThrottleBtn != null) revThrottleBtn.onClick.RemoveAllListeners();
        if (revSteerBtn    != null) revSteerBtn.onClick.RemoveAllListeners();
        if (revTurretBtn   != null) revTurretBtn.onClick.RemoveAllListeners();
        if (flipHBtn       != null) flipHBtn.onClick.RemoveAllListeners();
        if (flipVBtn       != null) flipVBtn.onClick.RemoveAllListeners();
        if (camToggleBtn   != null) camToggleBtn.onClick.RemoveAllListeners();
        if (captureNorthBtn  != null) captureNorthBtn.onClick.RemoveAllListeners();
        if (captureCentreBtn != null) captureCentreBtn.onClick.RemoveAllListeners();
        if (captureSouthBtn  != null) captureSouthBtn.onClick.RemoveAllListeners();
    }

    // ── Event callbacks ───────────────────────────────────────────────────────

    private void OnSelectionChanged(string robotId) => RefreshDriveLabels();

    private void OnRobotUpdated(RobotInfo info)
    {
        if (info.RobotId == robotListPanel?.CurrentRobotId)
            RefreshDriveLabels();
    }

    private void OnPointCaptured(int pointIndex, int alliance, string _)
        => RefreshCaptureColors();

    // ── Damage / Heal ─────────────────────────────────────────────────────────

    private void ApplyHit(string dir)
    {
        string robotId = robotListPanel?.CurrentRobotId;
        if (string.IsNullOrEmpty(robotId)) return;

        var game     = ServiceLocator.Game;
        var server   = ServiceLocator.RobotServer;
        var settings = ServiceLocator.GameSettings;

        int newHp = game?.ApplyDirectDamageWithDir(robotId, dir) ?? -1;
        if (newHp < 0) return;

        int maxHp = settings != null ? settings.MaxHp : 100;
        server?.SendFlashHit(robotId);
        server?.SendSetHp(robotId, newHp, maxHp);
    }

    private void OnHeal()
    {
        string robotId = robotListPanel?.CurrentRobotId;
        if (string.IsNullOrEmpty(robotId)) return;

        var game     = ServiceLocator.Game;
        var server   = ServiceLocator.RobotServer;
        var settings = ServiceLocator.GameSettings;

        if (game?.State == null) return;
        int maxHp = settings != null ? settings.MaxHp : 100;

        if (game.State.DeadRobots.Contains(robotId))
        {
            game.TransitionToRespawning(robotId);
            game.RespawnRobot(robotId);
        }
        else if (game.State.RespawningRobots.Contains(robotId))
        {
            game.RespawnRobot(robotId);
        }
        else
        {
            game.RestoreHp(robotId);
        }

        server?.SendFlashHeal(robotId);
        server?.SendSetHp(robotId, maxHp, maxHp);
    }

    // ── Drive inversion ───────────────────────────────────────────────────────

    private enum DriveAxis { Throttle, Steer, Turret }

    private void ToggleDrive(DriveAxis axis)
    {
        string robotId = robotListPanel?.CurrentRobotId;
        if (string.IsNullOrEmpty(robotId)) return;

        var dir    = _dir ?? ServiceLocator.RobotDirectory;
        var server = ServiceLocator.RobotServer;
        if (dir == null || !dir.TryGet(robotId, out var info)) return;

        bool th = info.InvThrottle;
        bool st = info.InvSteer;
        bool tu = info.InvTurret;

        switch (axis)
        {
            case DriveAxis.Throttle: th = !th; break;
            case DriveAxis.Steer:    st = !st; break;
            case DriveAxis.Turret:   tu = !tu; break;
        }

        dir.SetDriveConfig(robotId, th, st, tu);
        server?.SendDriveConfig(robotId, th, st, tu);
        RefreshDriveLabels();
    }

    // ── Capture cycling ───────────────────────────────────────────────────────

    private void CycleCapture(int pointIndex)
    {
        var cp    = _cp ?? ServiceLocator.CapturePoints;
        var state = ServiceLocator.Game?.State;
        if (cp == null || state == null) return;
        if (pointIndex < 0 || pointIndex >= state.CapturePointOwners.Length) return;

        int current = state.CapturePointOwners[pointIndex];
        // Cycle: -1 (neutral) → 0 (A0) → 1 (A1) → -1
        int next = current < 0 ? 0 : current == 0 ? 1 : -1;
        cp.ForceCapture(pointIndex, next);
        // OnPointCaptured fires → RefreshCaptureColors
    }

    // ── Refresh helpers ───────────────────────────────────────────────────────

    private void RefreshDriveLabels()
    {
        string robotId = robotListPanel?.CurrentRobotId;
        bool hasRobot  = !string.IsNullOrEmpty(robotId);

        RobotInfo info = null;
        if (hasRobot)
            (_dir ?? ServiceLocator.RobotDirectory)?.TryGet(robotId, out info);

        SetDriveBtn(revThrottleBtn, "REV THROTTLE", info?.InvThrottle ?? false);
        SetDriveBtn(revSteerBtn,    "REV STEER",    info?.InvSteer    ?? false);
        SetDriveBtn(revTurretBtn,   "REV TURRET",   info?.InvTurret   ?? false);
    }

    private static void SetDriveBtn(Button btn, string baseName, bool active)
    {
        if (btn == null) return;
        var lbl = btn.GetComponentInChildren<TextMeshProUGUI>();
        if (lbl != null) lbl.text = $"{baseName}: {(active ? "ON" : "OFF")}";
        var img = btn.GetComponent<Image>();
        if (img != null) img.color = active ? C_DRV_ON : C_DRV_OFF;
    }

    private void RefreshCaptureColors()
    {
        var state = ServiceLocator.Game?.State;
        var owners = state?.CapturePointOwners ?? new int[] { -1, -1, -1 };
        SetCaptureBtn(captureNorthBtn,  owners.Length > 0 ? owners[0] : -1, "NORTH");
        SetCaptureBtn(captureCentreBtn, owners.Length > 1 ? owners[1] : -1, "CENTRE");
        SetCaptureBtn(captureSouthBtn,  owners.Length > 2 ? owners[2] : -1, "SOUTH");
    }

    private static void SetCaptureBtn(Button btn, int alliance, string label)
    {
        if (btn == null) return;
        var img = btn.GetComponent<Image>();
        if (img != null) img.color = alliance == 0 ? C_A0 : alliance == 1 ? C_A1 : C_NEUTRAL;
        var lbl = btn.GetComponentInChildren<TextMeshProUGUI>();
        if (lbl != null)
        {
            string owner = alliance == 0 ? "TEAM 1" : alliance == 1 ? "TEAM 2" : "—";
            lbl.text = $"{label}\n{owner}";
        }
    }
}
