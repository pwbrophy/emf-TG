using System;
using System.Collections.Generic;
using UnityEngine;

public sealed class GameService
{
    public GameState State { get; private set; }

    // robotId → new HP (fired after every damage application)
    public event Action<string, int> OnHpChanged;

    // robotId (fired when HP reaches zero)
    public event Action<string> OnRobotDied;

    // allianceIndex, reason (fired when a win condition is met)
    public event Action<int, string> OnGameWon;

    public bool CanStart() => true;

    public void StartGame()
    {
        var settings = ServiceLocator.GameSettings;
        int maxHp    = settings != null ? settings.MaxHp : 100;

        var dir      = ServiceLocator.RobotDirectory;
        var robotsNow = dir != null ? dir.GetAll() : Array.Empty<RobotInfo>();

        var gs = new GameState
        {
            Alliances = 2,
            Players   = 2,
            Clients   = 2,
            Robots    = new List<RobotInfo>(robotsNow)
        };

        // Seed HP for every robot in the match
        foreach (var r in gs.Robots)
        {
            gs.RobotHp[r.RobotId]         = maxHp;
            gs.TotalDamageDealt[0]         = 0;
            gs.TotalDamageDealt[1]         = 0;
        }

        State = gs;
        ServiceLocator.CapturePoints?.Reset();
    }

    /// <summary>
    /// Force a win by alliance — used by CapturePointService when team points reach max.
    /// </summary>
    public void ForceWin(int allianceIndex, string reason)
    {
        DeclareWinner(allianceIndex, reason);
    }

    /// <summary>
    /// Apply damage from a hit. Direction is the compass string from ir_result ("N","NE",...).
    /// Rear sector (S, SE, SW) applies RearMultiplier.
    /// Returns the actual damage dealt (0 if robot already dead or not in game).
    /// </summary>
    public int ApplyDamage(string shooterId, string targetId, string direction,
                           PlayersService players, IRobotDirectory dir)
    {
        if (State == null) return 0;
        if (State.DeadRobots.Contains(targetId)) return 0;
        if (!State.RobotHp.ContainsKey(targetId)) return 0;

        var settings      = ServiceLocator.GameSettings;
        int baseDamage    = settings != null ? settings.DamagePerHit    : 25;
        float rearMult    = settings != null ? settings.RearMultiplier   : 3f;

        float multiplier  = IsRearHit(direction) ? rearMult : 1f;
        int damage        = Mathf.RoundToInt(baseDamage * multiplier);

        int newHp = Mathf.Max(0, State.RobotHp[targetId] - damage);
        State.RobotHp[targetId] = newHp;

        // Credit damage to the shooter's alliance
        int shooterAlliance = GetAllianceIndex(shooterId, players, dir);
        if (shooterAlliance >= 0)
        {
            if (!State.TotalDamageDealt.ContainsKey(shooterAlliance))
                State.TotalDamageDealt[shooterAlliance] = 0;
            State.TotalDamageDealt[shooterAlliance] += damage;
        }

        OnHpChanged?.Invoke(targetId, newHp);

        if (newHp <= 0)
        {
            State.DeadRobots.Add(targetId);
            OnRobotDied?.Invoke(targetId);
            if (shooterAlliance >= 0)
                ServiceLocator.CapturePoints?.AwardKillPoints(shooterAlliance);
            CheckWinCondition(players, dir);
        }

        return damage;
    }

    /// <summary>
    /// Apply a fixed amount of damage directly to a robot (bypasses shooter/alliance logic).
    /// Used for operator test controls. Returns new HP, or -1 if not in game / already dead.
    /// </summary>
    public int ApplyDirectDamage(string targetId, int amount)
    {
        if (State == null) return -1;
        if (State.DeadRobots.Contains(targetId)) return -1;
        if (!State.RobotHp.ContainsKey(targetId)) return -1;

        int newHp = Mathf.Max(0, State.RobotHp[targetId] - amount);
        State.RobotHp[targetId] = newHp;

        OnHpChanged?.Invoke(targetId, newHp);

        if (newHp <= 0)
        {
            State.DeadRobots.Add(targetId);
            OnRobotDied?.Invoke(targetId);
            CheckWinCondition(ServiceLocator.Players, ServiceLocator.RobotDirectory);
        }

        return newHp;
    }

    /// <summary>
    /// Called by MatchTimer when time expires. Determines winner by surviving tanks,
    /// then tiebreaks by total damage dealt.
    /// </summary>
    public void TimeExpired(PlayersService players, IRobotDirectory dir)
    {
        if (State == null) return;

        // Team points are the primary win condition; fall back to survivor/damage tiebreaker
        int winner = DetermineWinnerByTeamPoints();
        if (winner < 0) winner = DetermineWinnerByScore(players, dir);
        DeclareWinner(winner, "time");
    }

    private int DetermineWinnerByTeamPoints()
    {
        if (State?.TeamPoints == null || State.TeamPoints.Length < 2) return -1;
        if (State.TeamPoints[0] > State.TeamPoints[1]) return 0;
        if (State.TeamPoints[1] > State.TeamPoints[0]) return 1;
        return -1; // tied — caller falls back to survivor count
    }

    // ── private helpers ────────────────────────────────────────────────

    private static bool IsRearHit(string dir)
    {
        return dir == "S" || dir == "SE" || dir == "SW";
    }

    private void CheckWinCondition(PlayersService players, IRobotDirectory dir)
    {
        if (State == null) return;

        // Count living robots per alliance
        var surviving = new Dictionary<int, int>();
        foreach (var r in State.Robots)
        {
            if (State.DeadRobots.Contains(r.RobotId)) continue;
            int alliance = GetAllianceIndex(r.RobotId, players, dir);
            if (alliance < 0) continue;
            surviving[alliance] = surviving.GetValueOrDefault(alliance) + 1;
        }

        // If any alliance has zero survivors, the other alliance wins
        foreach (var kv in surviving)
        {
            if (kv.Value == 0)
            {
                // Find the alliance with survivors
                int winner = -1;
                foreach (var other in surviving)
                    if (other.Value > 0) { winner = other.Key; break; }

                DeclareWinner(winner, "elimination");
                return;
            }
        }

        // Also check if all alliances but one are wiped (handles >2 team edge case)
        int aliveAlliances = 0;
        int lastAlive = -1;
        foreach (var kv in surviving)
        {
            if (kv.Value > 0) { aliveAlliances++; lastAlive = kv.Key; }
        }
        if (aliveAlliances == 1)
            DeclareWinner(lastAlive, "elimination");
    }

    private int DetermineWinnerByScore(PlayersService players, IRobotDirectory dir)
    {
        // Count surviving robots per alliance
        var surviving = new Dictionary<int, int>();
        foreach (var r in State.Robots)
        {
            if (State.DeadRobots.Contains(r.RobotId)) continue;
            int alliance = GetAllianceIndex(r.RobotId, players, dir);
            if (alliance < 0) continue;
            surviving[alliance] = surviving.GetValueOrDefault(alliance) + 1;
        }

        // Highest survivors wins; tiebreak by damage dealt
        int bestAlliance = -1;
        int bestSurvivors = -1;
        int bestDamage = -1;

        foreach (var kv in surviving)
        {
            int dmg = State.TotalDamageDealt.GetValueOrDefault(kv.Key);
            if (kv.Value > bestSurvivors ||
               (kv.Value == bestSurvivors && dmg > bestDamage))
            {
                bestAlliance  = kv.Key;
                bestSurvivors = kv.Value;
                bestDamage    = dmg;
            }
        }

        return bestAlliance;
    }

    private void DeclareWinner(int allianceIndex, string reason)
    {
        if (State == null) return;
        if (State.WinnerAllianceIndex >= 0) return;  // already declared

        State.WinnerAllianceIndex = allianceIndex;
        State.EndReason           = reason;

        OnGameWon?.Invoke(allianceIndex, reason);
        ServiceLocator.GameFlow?.EndGame();
    }

    private static int GetAllianceIndex(string robotId, PlayersService players, IRobotDirectory dir)
    {
        if (dir == null || players == null) return -1;
        if (!dir.TryGet(robotId, out var info)) return -1;
        if (string.IsNullOrEmpty(info.AssignedPlayer)) return -1;

        var playerList = players.GetAll();
        foreach (var p in playerList)
            if (p.Name == info.AssignedPlayer) return p.AllianceIndex;

        return -1;
    }
}
