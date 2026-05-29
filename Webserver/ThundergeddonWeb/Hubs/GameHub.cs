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

    public async Task JoinLobby(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;

        // If Unity isn't up yet, tell the caller to wait rather than silently
        // dropping the join.  The phone will show a status message and can retry
        // once it receives the ServerConnected broadcast.
        if (!_bridge.IsConnectedToUnity)
        {
            await Clients.Caller.SendAsync("ServerNotReady");
            return;
        }

        await _bridge.SendToUnity(new
        {
            cmd          = "join",
            name         = name.Trim(),
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

    // ── Disconnect ───────────────────────────────────────────────────────────────

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
