using System;
using UnityEngine;

public sealed class CapturePointService
{
    static readonly string[] PointNames = { "North", "Centre", "South" };

    // (pointIndex, allianceIndex, pointName)
    public event Action<int, int, string> OnPointCaptured;
    public event Action OnTeamPointsChanged;

    // Per-team accumulator: seconds elapsed toward the next point award.
    // Reset on game start; runs in Update() via Tick(dt).
    private readonly float[] _teamAccum = new float[2];

    private const float PointPeriodSeconds = 3.0f; // 1 CP → 1 pt every 3 s

    public void Reset()
    {
        var gs = ServiceLocator.Game?.State;
        if (gs == null) return;
        gs.CapturePointOwners  = new int[] { -1, -1, -1 };
        gs.TeamPoints          = new int[] { 0, 0 };
        _teamAccum[0] = _teamAccum[1] = 0f;
    }

    /// <summary>
    /// Call every frame (from PlayerWebSocketServer.Update) while the game is Playing.
    /// Awards points at evenly-spaced intervals: 1 CP → 1 pt/3 s, 2 CPs → 1 pt/1.5 s, 3 CPs → 1 pt/1 s.
    /// Fires OnTeamPointsChanged once per individual point so callers can trigger per-point effects.
    /// </summary>
    public void Tick(float dt)
    {
        var gs       = ServiceLocator.Game?.State;
        var settings = ServiceLocator.GameSettings;
        if (gs == null || settings == null) return;

        // Count CPs owned by each team
        int[] cpCount = new int[gs.TeamPoints.Length];
        for (int i = 0; i < gs.CapturePointOwners.Length; i++)
        {
            int owner = gs.CapturePointOwners[i];
            if (owner >= 0 && owner < cpCount.Length) cpCount[owner]++;
        }

        for (int team = 0; team < gs.TeamPoints.Length; team++)
        {
            if (cpCount[team] == 0) continue;

            float interval = PointPeriodSeconds / cpCount[team]; // 3 s / N CPs
            _teamAccum[team] += dt;

            while (_teamAccum[team] >= interval)
            {
                _teamAccum[team] -= interval;
                gs.TeamPoints[team]++;
                OnTeamPointsChanged?.Invoke();

                if (gs.TeamPoints[team] >= settings.MaxTeamPoints)
                {
                    Debug.Log($"[CapturePoints] Alliance {team} wins by team points!");
                    ServiceLocator.Game?.ForceWin(team, "points");
                    return;
                }
            }
        }
    }

    /// <summary>
    /// Call from PlayerWebSocketServer when an RFID tag is scanned by a robot.
    /// Returns true if a point was captured (owner changed).
    /// </summary>
    public bool TryCapture(string robotId, string uid)
    {
        var gs       = ServiceLocator.Game?.State;
        var settings = ServiceLocator.GameSettings;
        var dir      = ServiceLocator.RobotDirectory;
        var players  = ServiceLocator.Players;

        if (gs == null || settings == null || dir == null || players == null) return false;
        if (string.IsNullOrEmpty(uid)) return false;
        if (gs.DeadRobots.Contains(robotId) || gs.RespawningRobots.Contains(robotId)) return false;

        int pointIndex = UidToPointIndex(uid, settings);
        if (pointIndex < 0) return false;

        int alliance = GetAllianceForRobot(robotId, dir, players);
        if (alliance < 0) return false;

        if (gs.CapturePointOwners[pointIndex] == alliance) return false; // already owned

        gs.CapturePointOwners[pointIndex] = alliance;
        Debug.Log($"[CapturePoints] {PointNames[pointIndex]} captured by alliance {alliance} (robot {robotId})");
        OnPointCaptured?.Invoke(pointIndex, alliance, PointNames[pointIndex]);
        return true;
    }

    /// <summary>
    /// Operator override: force a capture point to a given alliance (or -1 for neutral).
    /// Fires OnPointCaptured so UI and VP ticking update immediately.
    /// </summary>
    public void ForceCapture(int pointIndex, int allianceIndex)
    {
        var gs = ServiceLocator.Game?.State;
        if (gs == null) return;
        if (pointIndex < 0 || pointIndex >= gs.CapturePointOwners.Length) return;
        gs.CapturePointOwners[pointIndex] = allianceIndex;
        string name = pointIndex < PointNames.Length ? PointNames[pointIndex] : pointIndex.ToString();
        OnPointCaptured?.Invoke(pointIndex, allianceIndex, name);
    }

    /// <summary>
    /// Award kill points to the given alliance. Call from GameService.ApplyDamage on robot death.
    /// </summary>
    public void AwardKillPoints(int allianceIndex)
    {
        var gs       = ServiceLocator.Game?.State;
        var settings = ServiceLocator.GameSettings;
        if (gs == null || settings == null) return;
        if (allianceIndex < 0 || allianceIndex >= gs.TeamPoints.Length) return;

        gs.TeamPoints[allianceIndex] += settings.TeamPointsPerKill;
        OnTeamPointsChanged?.Invoke();

        if (gs.TeamPoints[allianceIndex] >= settings.MaxTeamPoints)
        {
            Debug.Log($"[CapturePoints] Alliance {allianceIndex} wins by team points (kill bonus)!");
            ServiceLocator.Game?.ForceWin(allianceIndex, "points");
        }
    }

    // ── helpers ────────────────────────────────────────────────────────

    private static int UidToPointIndex(string uid, GameSettings s)
    {
        uid = uid.ToUpperInvariant();
        if (UidInArray(uid, s.NorthPointUids))  return 0;
        if (UidInArray(uid, s.CentrePointUids)) return 1;
        if (UidInArray(uid, s.SouthPointUids))  return 2;
        return -1;
    }

    private static bool UidInArray(string uid, string[] arr)
    {
        if (arr == null) return false;
        foreach (var entry in arr)
            if (!string.IsNullOrEmpty(entry) && uid == entry.ToUpperInvariant()) return true;
        return false;
    }

    private static int GetAllianceForRobot(string robotId, IRobotDirectory dir, PlayersService players)
    {
        if (!dir.TryGet(robotId, out var info)) return -1;
        if (string.IsNullOrEmpty(info.AssignedPlayer)) return -1;
        foreach (var p in players.GetAll())
            if (p.Name == info.AssignedPlayer) return p.AllianceIndex;
        return -1;
    }
}
