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
    [Tooltip("Starting hit points for every robot. At default damage (10) a robot takes 10 direct hits or ~3-4 rear hits to destroy.")]
    public int MaxHp = 100;

    [Tooltip("Base damage dealt per successful IR hit. Multiplied by RearMultiplier for rear-sector hits (S/SE/SW).")]
    public int DamagePerHit = 10;

    [Tooltip("Rear-sector damage multiplier (hit from S / SE / SW). Default 3x rewards flanking manoeuvres.")]
    public float RearMultiplier = 3f;

    [Tooltip("Match duration in seconds. At expiry the team with the most surviving robots wins; damage breaks ties.")]
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

    [Header("Shot Timing")]
    [Tooltip("Minimum seconds between shots for the same robot. Set longer than the total shot time.")]
    public float FireCooldownSeconds = 3f;

    [Tooltip("How many ms after receiving the ir_fire_slot command the shooter waits before emitting IR. Gives listeners time to get ready. Reduce to speed up the sequence; increase if enemies consistently miss the window.")]
    public int SlotFutureMs = 50;

    [Tooltip("Extra delay (ms) added to the listener start time relative to the fire command. Currently 0. Could offset the listen window if the shooter fires early.")]
    public int ListenDelayMs = 0;

    [Tooltip("Duration of the first IR burst within each repetition. Longer = more chance of detection; too long wastes time with diminishing returns.")]
    public int B1DurMs = 25;

    [Tooltip("Silent gap between Burst 1 and Burst 2 within each rep. The two-burst design lets the receiver disambiguate which reps triggered.")]
    public int Gap12Ms = 20;

    [Tooltip("Duration of the second IR burst within each repetition. Should match B1DurMs unless intentionally asymmetric.")]
    public int B2DurMs = 25;

    [Tooltip("Silent gap between consecutive repetitions. Shortening packs more reps into the same window; lengthening helps slow receivers reset.")]
    public int RepGapMs = 20;

    [Tooltip("Number of IR burst-pairs emitted per shot. More reps = higher hit reliability at cost of longer slot. Listen window must cover all reps: Reps x (B1+Gap+B2+RepGap) - RepGap ms.")]
    public int Reps = 7;

    [Tooltip("Seconds Unity waits after the slot ends before collecting results. Accounts for Wi-Fi/processing latency on the robot. Reduce if robots respond quickly; increase if results arrive late.")]
    public float ResultBufferSeconds = 0.5f;

    [Tooltip("Pause all robot camera streams for the duration of the IR detection window. Reduces Wi-Fi congestion so slot commands arrive on time.")]
    public bool DisableCameraWhileDetecting = true;

    [Tooltip("Send motors_off to all robots involved in a shot before the detection window, then motors_on after. Stops motor electrical noise from triggering IR receivers.")]
    public bool DisableMotorsWhileDetecting = true;

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
                maxHp                       = MaxHp,
                damagePerHit                = DamagePerHit,
                rearMultiplier              = RearMultiplier,
                matchDurationSeconds        = MatchDurationSeconds,
                maxPlayers                  = MaxPlayers,
                maxTeamPoints               = MaxTeamPoints,
                teamPointsPerKill           = TeamPointsPerKill,
                fireCooldownSeconds         = FireCooldownSeconds,
                slotFutureMs                = SlotFutureMs,
                listenDelayMs               = ListenDelayMs,
                b1DurMs                     = B1DurMs,
                gap12Ms                     = Gap12Ms,
                b2DurMs                     = B2DurMs,
                repGapMs                    = RepGapMs,
                reps                        = Reps,
                resultBufferSeconds         = ResultBufferSeconds,
                disableCameraWhileDetecting = DisableCameraWhileDetecting,
                disableMotorsWhileDetecting = DisableMotorsWhileDetecting,
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

            if (data.maxHp                > 0) MaxHp                = data.maxHp;
            if (data.damagePerHit         > 0) DamagePerHit         = data.damagePerHit;
            if (data.rearMultiplier       > 0) RearMultiplier       = data.rearMultiplier;
            if (data.matchDurationSeconds > 0) MatchDurationSeconds = data.matchDurationSeconds;
            if (data.maxPlayers           > 0) MaxPlayers           = data.maxPlayers;
            if (data.maxTeamPoints        > 0) MaxTeamPoints        = data.maxTeamPoints;
            if (data.teamPointsPerKill    > 0) TeamPointsPerKill    = data.teamPointsPerKill;
            if (data.fireCooldownSeconds  > 0) FireCooldownSeconds  = data.fireCooldownSeconds;
            if (data.slotFutureMs         > 0) SlotFutureMs         = data.slotFutureMs;
            // listenDelayMs may legitimately be 0, preserve default if saved value is also 0
            ListenDelayMs = data.listenDelayMs;
            if (data.b1DurMs              > 0) B1DurMs              = data.b1DurMs;
            if (data.gap12Ms              > 0) Gap12Ms              = data.gap12Ms;
            if (data.b2DurMs              > 0) B2DurMs              = data.b2DurMs;
            if (data.repGapMs             > 0) RepGapMs             = data.repGapMs;
            if (data.reps                 > 0) Reps                 = data.reps;
            if (data.resultBufferSeconds  > 0) ResultBufferSeconds  = data.resultBufferSeconds;
            DisableCameraWhileDetecting = data.disableCameraWhileDetecting;
            DisableMotorsWhileDetecting = data.disableMotorsWhileDetecting;

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
        public float fireCooldownSeconds;
        public int   slotFutureMs;
        public int   listenDelayMs;
        public int   b1DurMs;
        public int   gap12Ms;
        public int   b2DurMs;
        public int   repGapMs;
        public int   reps;
        public float resultBufferSeconds;
        public bool  disableCameraWhileDetecting;
        public bool  disableMotorsWhileDetecting;
    }
}
