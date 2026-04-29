public static class ServiceLocator
{
    public static IRobotDirectory      RobotDirectory;
    public static GameFlow             GameFlow;
    public static LobbyService         Lobby;
    public static GameService          Game;
    public static RobotWebSocketServer  RobotServer;    // Set by RobotWebSocketServer on Awake
    public static PlayerWebSocketServer PlayerServer;  // Set by PlayerWebSocketServer on Awake
    public static ShootingController    Shooting;      // Set by ShootingController on Awake
    public static IrSlotScheduler      IrSlotScheduler; // Set by IrSlotScheduler on Awake
    public static PlayersService       Players;
    public static GameSettings         GameSettings;   // Set by AppBootstrap
    public static MatchTimer           MatchTimer;     // Set by AppBootstrap
}
