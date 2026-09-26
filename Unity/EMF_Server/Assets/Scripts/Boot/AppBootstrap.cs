using UnityEngine;

[DefaultExecutionOrder(-1000)]
public class AppBootstrap : MonoBehaviour
{
    [SerializeField] private bool startInMainMenu = true;

    private static bool _made;

    // Reset the singleton guard each time play mode starts so the second
    // play session doesn't destroy itself (happens when domain reload is off).
    [UnityEngine.RuntimeInitializeOnLoadMethod(UnityEngine.RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() { _made = false; }

    void Awake()
    {
        Debug.Log("Starting the App Bootstrap");
        if (_made) { Destroy(gameObject); return; }
        _made = true;

        DontDestroyOnLoad(gameObject);

        // Every phone and robot message is handed to the main thread and only acted
        // on in the next Update(), so the frame rate is a floor on control latency.
        //  - runInBackground: without it the player stops updating whenever the
        //    operator clicks away (e.g. onto the display page), freezing all controls.
        //  - 120 fps uncapped by vsync: queued drive/turret messages wait ~4 ms on
        //    average instead of ~8 ms at 60 Hz vsync. The admin UI is cheap to draw.
        Application.runInBackground = true;
        QualitySettings.vSyncCount  = 0;
        Application.targetFrameRate = 120;

        // Services
        Debug.Log("Creating Services");
        var lobby = new LobbyService();
        var game  = new GameService();
        var flow  = new GameFlow(lobby, game);

        ServiceLocator.Lobby    = lobby;
        ServiceLocator.Game     = game;
        ServiceLocator.GameFlow = flow;

        if (startInMainMenu)
            flow.BackToMenu();

        Debug.Log("[AppBootstrap] GameFlow created");

        ServiceLocator.RobotDirectory = new RobotDirectory();
        Debug.Log($"[AppBootstrap] RobotDirectory created: {ServiceLocator.RobotDirectory.GetHashCode()}");

        var players = new PlayersService();
        players.LoadOrEnsureDefaults();
        ServiceLocator.Players = players;
        Debug.Log($"[AppBootstrap] PlayersService created: {players.GetHashCode()} with {players.GetAll().Count} players");

        // MonoBehaviour services — look for them on this same GameObject
        ServiceLocator.GameSettings = GetComponent<GameSettings>()
                                   ?? gameObject.AddComponent<GameSettings>();
        ServiceLocator.MatchTimer   = GetComponent<MatchTimer>()
                                   ?? gameObject.AddComponent<MatchTimer>();

        Debug.Log("[AppBootstrap] GameSettings + MatchTimer ready");

        ServiceLocator.CapturePoints = new CapturePointService();
        Debug.Log("[AppBootstrap] CapturePointService ready");

        ServiceLocator.BeaconDirectory = new CapturePointBeaconDirectory();
        Debug.Log("[AppBootstrap] CapturePointBeaconDirectory ready");

        ServiceLocator.Countdown = GetComponent<CountdownController>()
                                ?? gameObject.AddComponent<CountdownController>();
        Debug.Log("[AppBootstrap] CountdownController ready");
    }
}
