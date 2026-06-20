using System;
using System.IO;
using UnityEngine;

/// <summary>
/// Configurable match parameters. Set these in the Lobby before starting.
/// Lives on the Bootstrap GameObject so it persists across scenes.
/// Values are saved to gamesettings.json in persistentDataPath and restored on startup.
/// </summary>
public class GameSettings : MonoBehaviour
{
    [Header("Match Parameters")]
    [Tooltip("Starting hit points for every robot.")]
    public int MaxHp = 100;

    [Tooltip("Base damage dealt per successful IR hit. Multiplied by RearMultiplier for rear-sector hits (S/SE/SW).")]
    public int DamagePerHit = 10;

    [Tooltip("Rear-sector damage multiplier (hit from S). Default 3x rewards flanking.")]
    public float RearMultiplier = 3f;

    [Tooltip("Side-hit damage multiplier (E or W). Default 1.5x.")]
    public float SideMultiplier = 1.5f;

    [Tooltip("Match duration in seconds.")]
    public float MatchDurationSeconds = 600f;

    [Header("Lobby")]
    [Tooltip("Maximum number of player slots shown on the public display page.")]
    public int MaxPlayers = 6;

    [Header("Team Points")]
    [Tooltip("Team points needed to win immediately via tug-of-war. Set very high to effectively disable.")]
    public int MaxTeamPoints = 300;

    [Tooltip("Team points awarded to the killing alliance per robot destroyed.")]
    public int TeamPointsPerKill = 20;

    [Header("Base RFID UIDs — set directly in Inspector, not persisted")]
    public string[] Alliance0BaseUids = new string[0];
    public string[] Alliance1BaseUids = new string[0];

    [Header("Capture Point RFID UIDs — set directly in Inspector, not persisted")]
    public string[] NorthPointUids  = new string[0];
    public string[] CentrePointUids = new string[0];
    public string[] SouthPointUids  = new string[0];

    [Header("Countdown")]
    [Tooltip("Seconds to count down before the match begins. Default 5.")]
    public int CountdownDuration = 5;

    /// <summary>Returns true if uid matches any tag configured for the given alliance's base.</summary>
    public bool IsAllianceBase(int alliance, string uid)
    {
        if (string.IsNullOrEmpty(uid)) return false;
        string[] arr = alliance == 0 ? Alliance0BaseUids :
                       alliance == 1 ? Alliance1BaseUids : null;
        if (arr == null) return false;
        uid = uid.ToUpperInvariant();
        foreach (var entry in arr)
            if (!string.IsNullOrEmpty(entry) && uid == entry.ToUpperInvariant()) return true;
        return false;
    }

    /// <summary>Returns true if uid matches either alliance's base.</summary>
    public bool IsAnyBase(string uid)
        => IsAllianceBase(0, uid) || IsAllianceBase(1, uid);

    /// <summary>Returns the display name ("North", "Centre", "South") if uid is a capture point, or null.</summary>
    public string GetCapturePointName(string uid)
    {
        if (string.IsNullOrEmpty(uid)) return null;
        uid = uid.ToUpperInvariant();
        if (UidInList(uid, NorthPointUids))  return "North";
        if (UidInList(uid, CentrePointUids)) return "Centre";
        if (UidInList(uid, SouthPointUids))  return "South";
        return null;
    }

    private static bool UidInList(string uid, string[] arr)
    {
        if (arr == null) return false;
        foreach (var entry in arr)
            if (!string.IsNullOrEmpty(entry) && uid == entry.ToUpperInvariant()) return true;
        return false;
    }

    [Header("Phone Controls")]
    [Tooltip("Speed (0.1–0.9) sent when a player presses a slow turret button. Fast buttons always use 1.0.")]
    public float SlowTurretSpeed = 0.4f;

    [Tooltip("When true, all phone turret input is capped to SlowTurretSpeed.")]
    public bool SlowTurretEnabled = false;

    [Header("Tank Physics")]
    [Tooltip("Seconds to ramp from stopped to full drive speed. 0 = instant response.")]
    public float DriveAcceleration = 0f;
    [Tooltip("Seconds to coast to a stop after drive input is released. 0 = instant stop.")]
    public float DriveDeceleration = 0f;
    [Tooltip("Seconds to ramp from stopped to full turret speed. 0 = instant response.")]
    public float TurretAcceleration = 0f;
    [Tooltip("Seconds to coast to a stop after turret input is released. 0 = instant stop.")]
    public float TurretDeceleration = 0f;

    [Header("Two-Player Mode")]
    [Tooltip("Allow a second player to join a tank as gunner in the lobby. Driver controls drive; gunner controls turret and fire.")]
    public bool TwoPlayerModeEnabled = false;

    [Header("Audio")]
    [Tooltip("When disabled, no buzzer sounds play on any robot.")]
    public bool BuzzerEnabled = true;

    [Header("Video")]
    [Tooltip("Robot camera frame-rate cap in fps (1-30). Lower = less Wi-Fi bandwidth. Default 20.")]
    public int VideoFps = 20;
    [Tooltip("Robot camera resolution index: 0=QVGA 320x240, 1=CIF 400x296, 2=HVGA 480x320, 3=VGA 640x480.")]
    public int VideoFrameSize = 2;
    [Tooltip("Robot camera JPEG quality (8=best/largest .. 40=worst/smallest). Default 10.")]
    public int VideoJpegQuality = 10;

    [Header("Respawn")]
    [Tooltip("Seconds of invulnerability granted after respawn or base heal. Also debounces the RFID tag.")]
    public float InvulnerabilitySeconds = 5f;

    [Header("Shot Timing")]
    [Tooltip("Minimum seconds between shots for the same robot.")]
    public float FireCooldownSeconds = 3f;

    [Tooltip("Duration (ms) of each listen window per phase. Enemy listens for this long after receiving ir_listen_window.")]
    public int HandshakeWindowMs = 10;

    [Tooltip("Timeout (ms) waiting for ir_emit_ack from the shooter. If exceeded the shot is aborted.")]
    public int HandshakeAckTimeoutMs = 300;

    [Tooltip("Timeout (ms) waiting for ir_window_result from each enemy. Non-responding enemies are treated as misses.")]
    public int HandshakeWindowTimeoutMs = 300;

    // ── Lifecycle ────────────────────────────────────────────────────────────────

    private void Awake()
    {
        LoadFromDisk();
    }

    // ── Persistence ──────────────────────────────────────────────────────────────

    private string SavePath =>
        Path.Combine(Application.persistentDataPath, "gamesettings.json");

    public void SaveToDisk()
    {
        try
        {
            var data = new SaveData
            {
                maxHp                    = MaxHp,
                damagePerHit             = DamagePerHit,
                rearMultiplier           = RearMultiplier,
                sideMultiplier           = SideMultiplier,
                matchDurationSeconds     = MatchDurationSeconds,
                maxPlayers               = MaxPlayers,
                maxTeamPoints            = MaxTeamPoints,
                teamPointsPerKill        = TeamPointsPerKill,
                countdownDuration        = CountdownDuration,
                slowTurretSpeed          = SlowTurretSpeed,
                driveAcceleration        = DriveAcceleration,
                driveDeceleration        = DriveDeceleration,
                turretAcceleration       = TurretAcceleration,
                turretDeceleration       = TurretDeceleration,
                invulnerabilitySeconds   = InvulnerabilitySeconds,
                fireCooldownSeconds      = FireCooldownSeconds,
                handshakeWindowMs        = HandshakeWindowMs,
                handshakeAckTimeoutMs    = HandshakeAckTimeoutMs,
                handshakeWindowTimeoutMs = HandshakeWindowTimeoutMs,
                buzzerEnabled            = BuzzerEnabled ? 1 : 0,
                twoPlayerModeEnabled     = TwoPlayerModeEnabled ? 1 : 0,
                slowTurretEnabled        = SlowTurretEnabled ? 1 : 0,
                videoFps                 = VideoFps,
                videoFrameSize           = VideoFrameSize,
                videoJpegQuality         = VideoJpegQuality,
            };
            File.WriteAllText(SavePath, JsonUtility.ToJson(data, true));
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[GameSettings] Save failed: " + ex.Message);
        }
    }

    private void LoadFromDisk()
    {
        if (!File.Exists(SavePath)) return;
        try
        {
            var data = JsonUtility.FromJson<SaveData>(File.ReadAllText(SavePath));
            if (data == null) return;

            if (data.maxHp                    > 0) MaxHp                    = data.maxHp;
            if (data.damagePerHit             > 0) DamagePerHit             = data.damagePerHit;
            if (data.rearMultiplier           > 0) RearMultiplier           = data.rearMultiplier;
            if (data.sideMultiplier           > 0) SideMultiplier           = data.sideMultiplier;
            if (data.matchDurationSeconds     > 0) MatchDurationSeconds     = data.matchDurationSeconds;
            if (data.maxPlayers               > 0) MaxPlayers               = data.maxPlayers;
            if (data.maxTeamPoints            > 0) MaxTeamPoints            = data.maxTeamPoints;
            if (data.teamPointsPerKill        > 0) TeamPointsPerKill        = data.teamPointsPerKill;
            if (data.countdownDuration        > 0) CountdownDuration        = data.countdownDuration;
            if (data.slowTurretSpeed          > 0) SlowTurretSpeed          = data.slowTurretSpeed;
            // Physics params are valid at zero, so always restore them.
            DriveAcceleration  = data.driveAcceleration;
            DriveDeceleration  = data.driveDeceleration;
            TurretAcceleration = data.turretAcceleration;
            TurretDeceleration = data.turretDeceleration;
            if (data.invulnerabilitySeconds   > 0) InvulnerabilitySeconds   = data.invulnerabilitySeconds;
            if (data.fireCooldownSeconds      > 0) FireCooldownSeconds      = data.fireCooldownSeconds;
            if (data.handshakeWindowMs        > 0) HandshakeWindowMs        = data.handshakeWindowMs;
            if (data.handshakeAckTimeoutMs    > 0) HandshakeAckTimeoutMs    = data.handshakeAckTimeoutMs;
            if (data.handshakeWindowTimeoutMs > 0) HandshakeWindowTimeoutMs = data.handshakeWindowTimeoutMs;
            // buzzerEnabled: -1 = not yet written (old save) → keep default true
            if (data.buzzerEnabled >= 0) BuzzerEnabled = data.buzzerEnabled != 0;
            // twoPlayerModeEnabled: -1 = not yet written → keep default false
            if (data.twoPlayerModeEnabled >= 0) TwoPlayerModeEnabled = data.twoPlayerModeEnabled != 0;
            if (data.slowTurretEnabled    >= 0) SlowTurretEnabled    = data.slowTurretEnabled    != 0;
            if (data.videoFps             > 0)  VideoFps             = data.videoFps;
            if (data.videoFrameSize       >= 0) VideoFrameSize       = data.videoFrameSize;
            if (data.videoJpegQuality     > 0)  VideoJpegQuality     = data.videoJpegQuality;

            Debug.Log("[GameSettings] Loaded from " + SavePath);
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[GameSettings] Load failed: " + ex.Message);
        }
    }

    [Serializable]
    private class SaveData
    {
        public int   maxHp;
        public int   damagePerHit;
        public float rearMultiplier;
        public float matchDurationSeconds;
        public int   maxPlayers;
        public int   maxTeamPoints;
        public int   teamPointsPerKill;
        public int   countdownDuration;
        public float slowTurretSpeed;
        public float driveAcceleration;
        public float driveDeceleration;
        public float turretAcceleration;
        public float turretDeceleration;
        public float invulnerabilitySeconds;
        public float fireCooldownSeconds;
        public int   handshakeWindowMs;
        public int   handshakeAckTimeoutMs;
        public int   handshakeWindowTimeoutMs;
        public float sideMultiplier;
        public int   buzzerEnabled = -1;         // -1 = not set (old save); 0 = off; 1 = on
        public int   twoPlayerModeEnabled = -1;
        public int   slowTurretEnabled = -1;
        public int   videoFps;                   // 0 = not set → keep default 20
        public int   videoFrameSize = -1;        // -1 = not set → keep default (index 2, HVGA)
        public int   videoJpegQuality;           // 0 = not set → keep default 10
    }
}
