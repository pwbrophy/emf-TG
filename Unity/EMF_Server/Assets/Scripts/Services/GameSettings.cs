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
}
