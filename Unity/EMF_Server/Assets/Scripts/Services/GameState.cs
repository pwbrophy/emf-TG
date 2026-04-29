using System.Collections.Generic;

public sealed class GameState
{
    public int Alliances = 2;
    public int Players   = 2;
    public int Clients   = 2;

    public List<RobotInfo> Robots;

    // HP tracking: robotId → current HP
    public Dictionary<string, int> RobotHp = new Dictionary<string, int>();

    // Damage dealt per alliance index (for tiebreaker)
    public Dictionary<int, int> TotalDamageDealt = new Dictionary<int, int>();

    // Dead robots (HP reached 0)
    public HashSet<string> DeadRobots = new HashSet<string>();

    // Match result — set when EndGame is called
    public int  WinnerAllianceIndex = -1;   // -1 = not yet decided
    public string EndReason         = "";   // "elimination" | "time" | "manual"

    // Capture points: index 0=North, 1=Centre, 2=South. Owner = alliance index, -1 = uncaptured.
    public int[] CapturePointOwners = { -1, -1, -1 };

    // Team points tug-of-war
    public int[] TeamPoints = { 0, 0 };
}
