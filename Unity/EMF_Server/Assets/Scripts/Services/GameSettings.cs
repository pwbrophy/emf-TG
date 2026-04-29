using UnityEngine;

/// <summary>
/// Configurable match parameters. Set these in the Lobby before starting.
/// Lives on the Bootstrap GameObject so it persists across scenes.
/// </summary>
public class GameSettings : MonoBehaviour
{
    [Header("Match Parameters")]
    [Tooltip("Starting hit points for every robot.")]
    public int MaxHp = 100;

    [Tooltip("Base damage dealt per successful IR hit.")]
    public int DamagePerHit = 25;

    [Tooltip("Rear-sector damage multiplier (hit from S / SE / SW).")]
    public float RearMultiplier = 3f;

    [Tooltip("Match duration in seconds.")]
    public float MatchDurationSeconds = 180f;

    [Header("Lobby")]
    [Tooltip("Maximum number of player slots shown on display page.")]
    public int MaxPlayers = 6;

    [Header("Team Points")]
    [Tooltip("Team points needed to win immediately via tug-of-war.")]
    public int MaxTeamPoints = 300;

    [Tooltip("Team points awarded to the killing alliance per robot destroyed.")]
    public int TeamPointsPerKill = 25;

    [Header("Capture Point RFID UIDs — add one entry per physical tag")]
    public string[] NorthPointUids  = new string[0];
    public string[] CentrePointUids = new string[0];
    public string[] SouthPointUids  = new string[0];
}
