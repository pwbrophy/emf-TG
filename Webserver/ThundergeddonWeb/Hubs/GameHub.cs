using Microsoft.AspNetCore.SignalR;
using ThundergeddonWeb.Services;

namespace ThundergeddonWeb.Hubs;

public class GameHub : Hub
{
    private readonly UnityBridgeService _bridge;

    public GameHub(UnityBridgeService bridge)
    {
        _bridge = bridge;
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
        if (string.IsNullOrWhiteSpace(name)) return false;

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
        await _bridge.SendToUnity(new
        {
            cmd          = "turret",
            connectionId = Context.ConnectionId,
            speed        = MathF.Round(speed, 3)
        });
    }

    public async Task Fire()
    {
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

        // Replay last display state so the display page can resume mid-game on refresh
        if (_bridge.LastDisplayUpdate != null)
            await Clients.Caller.SendAsync("DisplayUpdate", _bridge.LastDisplayUpdate);
        // Replay last spectate state so the display page can resume FPV on refresh
        if (_bridge.LastSpectateUpdate != null)
            await Clients.Caller.SendAsync("SpectateUpdate", _bridge.LastSpectateUpdate);

        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        await _bridge.SendToUnity(new
        {
            cmd          = "leave",
            connectionId = Context.ConnectionId
        });
        await base.OnDisconnectedAsync(exception);
    }
}
