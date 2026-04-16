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
}
