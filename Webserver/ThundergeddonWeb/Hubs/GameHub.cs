using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;
using ThundergeddonWeb.Services;

namespace ThundergeddonWeb.Hubs;

public class GameHub : Hub
{
    private readonly UnityBridgeService _bridge;

    // Port 5000 is open to the whole venue LAN, so hub methods must assume hostile
    // callers. Names are length/character capped, and each connection gets a simple
    // per-second message budget. Legit peak is ~25 msg/s (20 Hz drive + turret + fire);
    // 60 leaves headroom while stopping floods from reaching Unity.
    // 12 keeps names readable in the phone lobby rows and on the big display,
    // where longer ones were being truncated. Mirrored by MAX_NAME_LEN in index.html.
    private const int MaxNameLength    = 12;
    private const int MaxMsgsPerSecond = 60;

    // Spectator display pages join this group; display-only traffic (DisplayUpdate,
    // DisplayEvent, HitEvent, KillEvent — ~3 KB/s) goes only to it, not to phones.
    public const string DisplayGroup = "display";

    // Static: hubs are transient per-invocation. Keyed by connectionId, cleaned on disconnect.
    private static readonly ConcurrentDictionary<string, (long Window, int Count)> _rate = new();

    public GameHub(UnityBridgeService bridge)
    {
        _bridge = bridge;
    }

    /// <summary>Sliding 1-second message budget for the calling connection.</summary>
    private bool AllowMessage()
    {
        long window = Environment.TickCount64 / 1000;
        var entry = _rate.AddOrUpdate(Context.ConnectionId,
            _ => (window, 1),
            (_, e) => e.Window == window ? (e.Window, e.Count + 1) : (window, 1));
        return entry.Count <= MaxMsgsPerSecond;
    }

    private static bool IsValidName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > MaxNameLength) return false;
        foreach (char c in name)
            if (char.IsControl(c)) return false;
        return true;
    }

    // ── Lobby ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns true if the join was forwarded to Unity, false if Unity is not yet connected.
    /// The client uses the return value to decide whether to show the lobby screen.
    /// During Playing, the join is forwarded to Unity which accepts reconnects for known
    /// players and rejects unknown names with join_rejected.
    /// </summary>
    public async Task<bool> JoinLobby(string name)
    {
        if (!AllowMessage()) return false;
        name = (name ?? "").Trim();

        // A phone running a cached copy of the page from before the cap will still
        // send up to its old 20-char limit. Tell it why rather than failing silently:
        // truncating here instead would leave the phone's own myName out of sync with
        // the roster, so it would no longer recognise itself as driver or gunner.
        if (name.Length > MaxNameLength)
        {
            await Clients.Caller.SendAsync("JoinRejected",
                $"Names must be {MaxNameLength} characters or fewer.");
            return false;
        }

        if (!IsValidName(name)) return false;

        if (!_bridge.IsConnectedToUnity)
        {
            await Clients.Caller.SendAsync("ServerNotReady");
            return false;
        }

        // Allow joins in Lobby (normal) and Playing (reconnect after page refresh).
        // All other phases (MainMenu, Ended) are not joinable.
        if (!_bridge.IsGameReady && _bridge.CurrentPhase != "playing")
        {
            await Clients.Caller.SendAsync("ServerNotReady");
            return false;
        }

        await _bridge.SendToUnity(new
        {
            cmd          = "join",
            name         = name.Trim(),
            connectionId = Context.ConnectionId
        });
        return true;
    }

    public async Task JoinSquad(int alliance)
    {
        await _bridge.SendToUnity(new
        {
            cmd          = "squad",
            alliance     = alliance,
            connectionId = Context.ConnectionId
        });
    }

    public async Task PickRobot(string robotId)
    {
        await _bridge.SendToUnity(new
        {
            cmd          = "pick_robot",
            robotId      = robotId ?? "",
            connectionId = Context.ConnectionId
        });
    }

    // ── Two-player lobby actions ─────────────────────────────────────────────────

    public async Task SetTwoPlayer(bool enabled)
    {
        await _bridge.SendToUnity(new
        {
            cmd          = "set_two_player",
            connectionId = Context.ConnectionId,
            enabled      = enabled
        });
    }

    public async Task JoinAsGunner(string robotId)
    {
        await _bridge.SendToUnity(new
        {
            cmd          = "join_as_gunner",
            connectionId = Context.ConnectionId,
            robotId      = robotId ?? ""
        });
    }

    public async Task LeaveGunner()
    {
        await _bridge.SendToUnity(new
        {
            cmd          = "leave_gunner",
            connectionId = Context.ConnectionId
        });
    }

    public async Task SwapRoles()
    {
        await _bridge.SendToUnity(new
        {
            cmd          = "swap_roles",
            connectionId = Context.ConnectionId
        });
    }

    // ── Gameplay inputs ──────────────────────────────────────────────────────────

    public async Task SendDrive(float left, float right)
    {
        if (!AllowMessage()) return;
        await _bridge.SendToUnity(new
        {
            cmd          = "drive",
            connectionId = Context.ConnectionId,
            l            = MathF.Round(left,  3),
            r            = MathF.Round(right, 3)
        });
    }

    public async Task SendTurret(float speed)
    {
        if (!AllowMessage()) return;
        await _bridge.SendToUnity(new
        {
            cmd          = "turret",
            connectionId = Context.ConnectionId,
            speed        = MathF.Round(speed, 3)
        });
    }

    public async Task Fire()
    {
        if (!AllowMessage()) return;
        await _bridge.SendToUnity(new
        {
            cmd          = "fire",
            connectionId = Context.ConnectionId
        });
    }

    // ── Connect / Disconnect ─────────────────────────────────────────────────────

    /// <summary>
    /// Tell each new client immediately whether Unity is reachable so the join
    /// button reflects the real server state without needing a join attempt first.
    /// </summary>
    public override async Task OnConnectedAsync()
    {
        string evt = _bridge.IsGameReady        ? "ServerConnected"
                   : _bridge.CurrentPhase == "playing" ? "GameInProgress"
                   : "ServerNotReady";
        await Clients.Caller.SendAsync(evt);

        // Push the current debug-console setting so a phone joining after the
        // operator toggled it still matches everyone else.
        await Clients.Caller.SendAsync("DebugConsole", _bridge.DebugConsoleEnabled);

        await base.OnConnectedAsync();
    }

    /// <summary>
    /// Called by display.html on every (re)connect — group membership does not
    /// survive a reconnect, since that is a new connection id.
    /// </summary>
    public async Task JoinDisplay()
    {
        if (!AllowMessage()) return;
        await Groups.AddToGroupAsync(Context.ConnectionId, DisplayGroup);

        // Replay last display state so the display page can resume mid-game on refresh
        if (_bridge.LastDisplayUpdate != null)
            await Clients.Caller.SendAsync("DisplayUpdate", _bridge.LastDisplayUpdate);
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        _rate.TryRemove(Context.ConnectionId, out _);
        await _bridge.SendToUnity(new
        {
            cmd          = "leave",
            connectionId = Context.ConnectionId
        });
        await base.OnDisconnectedAsync(exception);
    }
}
