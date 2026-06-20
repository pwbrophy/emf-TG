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

    // robotId (fired when a respawning robot returns to base and is fully restored)
    public event Action<string> OnRobotRespawned;

    // allianceIndex, reason (fired when a win condition is met)
    public event Action<int, string> OnGameWon;

    // targetId, shooterId, rawDetMask (8-bit sensor bits), cardinalDir ("N"|"E"|"S"|"W")
    public event Action<string, string, byte, string> OnRobotHitDirection;

    // shooterRobotId, targetRobotId, damageAmount — fired for every hit that lands
    public event Action<string, string, int> OnDamageDealt;

    // shooterId (null if operator/direct damage), targetId — fired when HP reaches zero
    public event Action<string, string> OnRobotKilled;

    // robotId — fired when invulnerability starts and ends
    public event Action<string> OnInvulnerabilityGranted;
    public event Action<string> OnInvulnerabilityEnded;

    public bool CanStart() => true;

    public void StartGame()
    {
        var settings = ServiceLocator.GameSettings;
        int maxHp    = settings != null ? settings.MaxHp : 100;

        var dir      = ServiceLocator.RobotDirectory;
        var allRobots = dir != null ? dir.GetAll() : Array.Empty<RobotInfo>();

        // Only include robots that have a player assigned — unassigned robots stay inactive
        var assignedRobots = new List<RobotInfo>();
        foreach (var r in allRobots)
            if (!string.IsNullOrEmpty(r.AssignedPlayer))
                assignedRobots.Add(r);

        var gs = new GameState
        {
            Alliances = 2,
            Players   = 2,
            Clients   = 2,
            Robots    = assignedRobots
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
    public int ApplyDamage(string shooterId, string targetId,
                           byte rawMask, string cardinalDir,
                           PlayersService players, IRobotDirectory dir)
    {
        if (State == null) return 0;
        // Drop in-flight IR results that resolve while the match is paused
        if (ServiceLocator.GameFlow?.IsPaused == true) return 0;
        if (State.DeadRobots.Contains(targetId) || State.RespawningRobots.Contains(targetId)) return 0;
        if (State.InvulnerableRobots.Contains(targetId)) return 0;
        if (!State.RobotHp.ContainsKey(targetId)) return 0;

        var settings      = ServiceLocator.GameSettings;
        int baseDamage    = settings != null ? settings.DamagePerHit    : 25;
        float rearMult    = settings != null ? settings.RearMultiplier   : 3f;
        float sideMult    = settings != null ? settings.SideMultiplier   : 1.5f;

        float multiplier  = IsRearHit(cardinalDir) ? rearMult
                          : IsSideHit(cardinalDir) ? sideMult
                          : 1f;
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

        OnDamageDealt?.Invoke(shooterId, targetId, damage);
        OnHpChanged?.Invoke(targetId, newHp);
        OnRobotHitDirection?.Invoke(targetId, shooterId, rawMask, cardinalDir);

        if (newHp <= 0)
        {
            State.DeadRobots.Add(targetId);
            OnRobotDied?.Invoke(targetId);
            OnRobotKilled?.Invoke(shooterId, targetId);
            if (shooterAlliance >= 0)
                ServiceLocator.CapturePoints?.AwardKillPoints(shooterAlliance);
            // Elimination no longer ends the game — robots can respawn.
            // Game ends only via manual stop or victory-points threshold.
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
        if (State.DeadRobots.Contains(targetId) || State.RespawningRobots.Contains(targetId)) return -1;
        if (State.InvulnerableRobots.Contains(targetId)) return -1;
        if (!State.RobotHp.ContainsKey(targetId)) return -1;

        int newHp = Mathf.Max(0, State.RobotHp[targetId] - amount);
        State.RobotHp[targetId] = newHp;

        OnHpChanged?.Invoke(targetId, newHp);

        if (newHp <= 0)
        {
            State.DeadRobots.Add(targetId);
            OnRobotDied?.Invoke(targetId);
            OnRobotKilled?.Invoke(null, targetId);
            // Elimination no longer ends the game — robots can respawn.
        }

        return newHp;
    }

    /// <summary>
    /// Apply directional damage from an operator button (no shooter/alliance credit).
    /// Applies the rear multiplier for "S" hits. Returns new HP, or -1 if not in game / already dead.
    /// </summary>
    public int ApplyDirectDamageWithDir(string targetId, string cardinalDir)
    {
        if (State == null) return -1;
        if (State.DeadRobots.Contains(targetId) || State.RespawningRobots.Contains(targetId)) return -1;
        if (State.InvulnerableRobots.Contains(targetId)) return -1;
        if (!State.RobotHp.ContainsKey(targetId)) return -1;

        var settings  = ServiceLocator.GameSettings;
        int baseDamage = settings != null ? settings.DamagePerHit  : 25;
        float rearMult = settings != null ? settings.RearMultiplier : 3f;
        int damage     = Mathf.RoundToInt(baseDamage * (IsRearHit(cardinalDir) ? rearMult : 1f));

        int newHp = Mathf.Max(0, State.RobotHp[targetId] - damage);
        State.RobotHp[targetId] = newHp;

        OnHpChanged?.Invoke(targetId, newHp);
        OnRobotHitDirection?.Invoke(targetId, null, 0, cardinalDir);

        if (newHp <= 0)
        {
            State.DeadRobots.Add(targetId);
            OnRobotDied?.Invoke(targetId);
            OnRobotKilled?.Invoke(null, targetId);
        }

        return newHp;
    }

    /// <summary>
    /// Restore a robot to full HP (base heal for alive robots). No-op if dead/respawning.
    /// </summary>
    public void RestoreHp(string robotId)
    {
        if (State == null) return;
        if (State.DeadRobots.Contains(robotId) || State.RespawningRobots.Contains(robotId)) return;
        if (State.InvulnerableRobots.Contains(robotId)) return; // debounce: already healed recently
        if (!State.RobotHp.ContainsKey(robotId)) return;

        var settings = ServiceLocator.GameSettings;
        int maxHp = settings != null ? settings.MaxHp : 100;

        State.RobotHp[robotId] = maxHp;
        OnHpChanged?.Invoke(robotId, maxHp);
        Debug.Log($"[GameService] Base heal: {robotId} restored to {maxHp} HP");
        GrantInvulnerability(robotId);
    }

    /// <summary>
    /// Move a robot from the explosion phase (DeadRobots) into dead-walk (RespawningRobots).
    /// Called by PlayerWebSocketServer after the 5-second explosion timer.
    /// </summary>
    public void TransitionToRespawning(string robotId)
    {
        if (State == null) return;
        if (!State.DeadRobots.Contains(robotId)) return;

        State.DeadRobots.Remove(robotId);
        State.RespawningRobots.Add(robotId);
        Debug.Log($"[GameService] {robotId} → dead walk (RespawningRobots)");
    }

    /// <summary>
    /// Fully revive a robot that is in dead walk at its team base.
    /// Removes from RespawningRobots, restores HP, fires OnHpChanged + OnRobotRespawned.
    /// </summary>
    public void RespawnRobot(string robotId)
    {
        if (State == null) return;
        if (!State.RespawningRobots.Contains(robotId)) return;
        if (!State.RobotHp.ContainsKey(robotId)) return;

        State.RespawningRobots.Remove(robotId);

        var settings = ServiceLocator.GameSettings;
        int maxHp = settings != null ? settings.MaxHp : 100;

        State.RobotHp[robotId] = maxHp;
        OnHpChanged?.Invoke(robotId, maxHp);
        OnRobotRespawned?.Invoke(robotId);
        Debug.Log($"[GameService] {robotId} respawned at base — HP restored to {maxHp}");
        GrantInvulnerability(robotId);
    }

    /// <summary>
    /// Grant temporary invulnerability after a base heal or respawn.
    /// No-op if the robot is already invulnerable (debounce for repeated RFID scans).
    /// </summary>
    public void GrantInvulnerability(string robotId)
    {
        if (State == null) return;
        if (State.InvulnerableRobots.Contains(robotId)) return;

        var settings = ServiceLocator.GameSettings;
        float duration = settings != null ? settings.InvulnerabilitySeconds : 5f;

        State.InvulnerableRobots.Add(robotId);
        State.InvulnerableExpiry[robotId] = Time.time + duration;

        OnInvulnerabilityGranted?.Invoke(robotId);
        Debug.Log($"[GameService] {robotId} invulnerable for {duration}s");
    }

    /// <summary>
    /// Expire invulnerabilities whose timer has elapsed. Call every frame from PlayerWebSocketServer.Update().
    /// </summary>
    public void TickInvulnerability(float now)
    {
        if (State == null || State.InvulnerableExpiry.Count == 0) return;

        List<string> expired = null;
        foreach (var kvp in State.InvulnerableExpiry)
            if (now >= kvp.Value)
            {
                if (expired == null) expired = new List<string>();
                expired.Add(kvp.Key);
            }

        if (expired == null) return;
        foreach (var robotId in expired)
        {
            State.InvulnerableRobots.Remove(robotId);
            State.InvulnerableExpiry.Remove(robotId);
            OnInvulnerabilityEnded?.Invoke(robotId);
            Debug.Log($"[GameService] {robotId} invulnerability ended");
        }
    }

    /// <summary>
    /// Cancel all active invulnerabilities immediately (e.g. when the game ends).
    /// Fires OnInvulnerabilityEnded for each robot so the robot LED can be restored.
    /// </summary>
    public void ClearAllInvulnerabilities()
    {
        if (State == null) return;
        var robots = new List<string>(State.InvulnerableRobots);
        State.InvulnerableRobots.Clear();
        State.InvulnerableExpiry.Clear();
        foreach (var robotId in robots)
            OnInvulnerabilityEnded?.Invoke(robotId);
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

    private static bool IsRearHit(string dir) => dir == "S";
    private static bool IsSideHit(string dir) => dir == "E" || dir == "W";

    private int DetermineWinnerByScore(PlayersService players, IRobotDirectory dir)
    {
        // Count surviving robots per alliance (respawning robots count as alive)
        var surviving = new Dictionary<int, int>();
        foreach (var r in State.Robots)
        {
            if (State.DeadRobots.Contains(r.RobotId) && !State.RespawningRobots.Contains(r.RobotId)) continue;
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
