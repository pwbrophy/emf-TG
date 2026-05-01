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

    [Tooltip("Base damage dealt per successful IR hit.")]
    public int DamagePerHit = 10;

    [Tooltip("Rear-sector damage multiplier (hit from S / SE / SW).")]
    public float RearMultiplier = 3f;

    [Tooltip("Match duration in seconds.")]
    public float MatchDurationSeconds = 600f;

    [Header("Lobby")]
    [Tooltip("Maximum number of player slots shown on display page.")]
    public int MaxPlayers = 6;

    [Header("Team Points")]
    [Tooltip("Team points needed to win immediately via tug-of-war.")]
    public int MaxTeamPoints = 300;

    [Tooltip("Team points awarded to the killing alliance per robot destroyed.")]
    public int TeamPointsPerKill = 20;

    [Header("Base RFID UIDs — one UID per alliance base tag")]
    public string Alliance0BaseUid = "";
    public string Alliance1BaseUid = "";

    [Header("Capture Point RFID UIDs — add one entry per physical tag")]
    public string[] NorthPointUids  = new string[0];
    public string[] CentrePointUids = new string[0];
    public string[] SouthPointUids  = new string[0];

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
                maxHp                = MaxHp,
                damagePerHit         = DamagePerHit,
                rearMultiplier       = RearMultiplier,
                matchDurationSeconds = MatchDurationSeconds,
                maxPlayers           = MaxPlayers,
                maxTeamPoints        = MaxTeamPoints,
                teamPointsPerKill    = TeamPointsPerKill,
                alliance0BaseUid     = Alliance0BaseUid,
                alliance1BaseUid     = Alliance1BaseUid,
                northPointUids       = NorthPointUids,
                centrePointUids      = CentrePointUids,
                southPointUids       = SouthPointUids,
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

            if (data.maxHp                > 0)    MaxHp                = data.maxHp;
            if (data.damagePerHit         > 0)    DamagePerHit         = data.damagePerHit;
            if (data.rearMultiplier       > 0)    RearMultiplier       = data.rearMultiplier;
            if (data.matchDurationSeconds > 0)    MatchDurationSeconds = data.matchDurationSeconds;
            if (data.maxPlayers           > 0)    MaxPlayers           = data.maxPlayers;
            if (data.maxTeamPoints        > 0)    MaxTeamPoints        = data.maxTeamPoints;
            if (data.teamPointsPerKill    > 0)    TeamPointsPerKill    = data.teamPointsPerKill;
            if (data.alliance0BaseUid     != null) Alliance0BaseUid    = data.alliance0BaseUid;
            if (data.alliance1BaseUid     != null) Alliance1BaseUid    = data.alliance1BaseUid;
            if (data.northPointUids       != null) NorthPointUids      = data.northPointUids;
            if (data.centrePointUids      != null) CentrePointUids     = data.centrePointUids;
            if (data.southPointUids       != null) SouthPointUids      = data.southPointUids;

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
        public int      maxHp;
        public int      damagePerHit;
        public float    rearMultiplier;
        public float    matchDurationSeconds;
        public int      maxPlayers;
        public int      maxTeamPoints;
        public int      teamPointsPerKill;
        public string   alliance0BaseUid;
        public string   alliance1BaseUid;
        public string[] northPointUids;
        public string[] centrePointUids;
        public string[] southPointUids;
    }
}
