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

    [Tooltip("Rear-sector damage multiplier (hit from S / SE / SW). Default 3x rewards flanking.")]
    public float RearMultiplier = 3f;

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
    public string Alliance0BaseUid = "";
    public string Alliance1BaseUid = "";

    [Header("Capture Point RFID UIDs — set directly in Inspector, not persisted")]
    public string[] NorthPointUids  = new string[0];
    public string[] CentrePointUids = new string[0];
    public string[] SouthPointUids  = new string[0];

    [Header("Phone Controls")]
    [Tooltip("Speed (0.1–0.9) sent when a player presses a slow turret button. Fast buttons always use 1.0.")]
    public float SlowTurretSpeed = 0.4f;

    [Header("Tank Physics")]
    [Tooltip("Seconds to ramp from stopped to full drive speed. 0 = instant response.")]
    public float DriveAcceleration = 0f;
    [Tooltip("Seconds to coast to a stop after drive input is released. 0 = instant stop.")]
    public float DriveDeceleration = 0f;
    [Tooltip("Seconds to ramp from stopped to full turret speed. 0 = instant response.")]
    public float TurretAcceleration = 0f;
    [Tooltip("Seconds to coast to a stop after turret input is released. 0 = instant stop.")]
    public float TurretDeceleration = 0f;

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
                matchDurationSeconds     = MatchDurationSeconds,
                maxPlayers               = MaxPlayers,
                maxTeamPoints            = MaxTeamPoints,
                teamPointsPerKill        = TeamPointsPerKill,
                slowTurretSpeed          = SlowTurretSpeed,
                driveAcceleration        = DriveAcceleration,
                driveDeceleration        = DriveDeceleration,
                turretAcceleration       = TurretAcceleration,
                turretDeceleration       = TurretDeceleration,
                fireCooldownSeconds      = FireCooldownSeconds,
                handshakeWindowMs        = HandshakeWindowMs,
                handshakeAckTimeoutMs    = HandshakeAckTimeoutMs,
                handshakeWindowTimeoutMs = HandshakeWindowTimeoutMs,
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
            if (data.matchDurationSeconds     > 0) MatchDurationSeconds     = data.matchDurationSeconds;
            if (data.maxPlayers               > 0) MaxPlayers               = data.maxPlayers;
            if (data.maxTeamPoints            > 0) MaxTeamPoints            = data.maxTeamPoints;
            if (data.teamPointsPerKill        > 0) TeamPointsPerKill        = data.teamPointsPerKill;
            if (data.slowTurretSpeed          > 0) SlowTurretSpeed          = data.slowTurretSpeed;
            // Physics params are valid at zero, so always restore them.
            DriveAcceleration  = data.driveAcceleration;
            DriveDeceleration  = data.driveDeceleration;
            TurretAcceleration = data.turretAcceleration;
            TurretDeceleration = data.turretDeceleration;
            if (data.fireCooldownSeconds      > 0) FireCooldownSeconds      = data.fireCooldownSeconds;
            if (data.handshakeWindowMs        > 0) HandshakeWindowMs        = data.handshakeWindowMs;
            if (data.handshakeAckTimeoutMs    > 0) HandshakeAckTimeoutMs    = data.handshakeAckTimeoutMs;
            if (data.handshakeWindowTimeoutMs > 0) HandshakeWindowTimeoutMs = data.handshakeWindowTimeoutMs;

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
        public float slowTurretSpeed;
        public float driveAcceleration;
        public float driveDeceleration;
        public float turretAcceleration;
        public float turretDeceleration;
        public float fireCooldownSeconds;
        public int   handshakeWindowMs;
        public int   handshakeAckTimeoutMs;
        public int   handshakeWindowTimeoutMs;
    }
}
