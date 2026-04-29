using System;
using UnityEngine;

public sealed class CapturePointService
{
    static readonly string[] PointNames = { "North", "Centre", "South" };

    // (pointIndex, allianceIndex, pointName)
    public event Action<int, int, string> OnPointCaptured;
    public event Action OnTeamPointsChanged;

    public void Reset()
    {
        var gs = ServiceLocator.Game?.State;
        if (gs == null) return;
        gs.CapturePointOwners = new int[] { -1, -1, -1 };
        gs.TeamPoints         = new int[] { 0, 0 };
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
    /// Award 1 team point per owned capture point. Call at 1 Hz from MatchTimer.OnTick.
    /// </summary>
    public void Tick()
    {
        var gs       = ServiceLocator.Game?.State;
        var settings = ServiceLocator.GameSettings;
        if (gs == null || settings == null) return;

        bool changed = false;
        for (int i = 0; i < gs.CapturePointOwners.Length; i++)
        {
            int owner = gs.CapturePointOwners[i];
            if (owner < 0 || owner >= gs.TeamPoints.Length) continue;
            gs.TeamPoints[owner] += 1;
            changed = true;
        }

        if (!changed) return;
        OnTeamPointsChanged?.Invoke();

        // Check tug-of-war win condition
        for (int a = 0; a < gs.TeamPoints.Length; a++)
        {
            if (gs.TeamPoints[a] >= settings.MaxTeamPoints)
            {
                Debug.Log($"[CapturePoints] Alliance {a} wins by team points!");
                ServiceLocator.Game?.ForceWin(a, "points");
                return;
            }
        }
    }

    /// <summary>
    /// Award kill points to the given alliance. Call from GameService.OnRobotDied.
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
