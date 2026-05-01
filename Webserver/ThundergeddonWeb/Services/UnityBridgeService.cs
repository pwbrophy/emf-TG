using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using ThundergeddonWeb.Hubs;

namespace ThundergeddonWeb.Services;

/// <summary>
/// Background service that maintains a WebSocket connection to Unity's
/// PlayerWebSocketServer (ws://127.0.0.1:8081/players).
///
/// Routes phone→Unity: join, leave, drive, turret, fire.
/// Routes Unity→phone: player_list, game_started, state_update, you_are_dead, game_over.
/// </summary>
public class UnityBridgeService : BackgroundService
{
    private const string UnityWsUrl = "ws://127.0.0.1:8081/players";

    private readonly IHubContext<GameHub> _hub;
    private readonly ILogger<UnityBridgeService> _logger;

    private ClientWebSocket? _ws;
    private readonly SemaphoreSlim _sendLock = new SemaphoreSlim(1, 1);

    public UnityBridgeService(IHubContext<GameHub> hub, ILogger<UnityBridgeService> logger)
    {
        _hub    = hub;
        _logger = logger;
    }

    // ── BackgroundService ────────────────────────────────────────────────────────

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await ConnectAndReceive(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning("[Bridge] Disconnected: {msg}. Retrying in 3s.", ex.Message);
            }

            if (!ct.IsCancellationRequested)
                await Task.Delay(3000, ct).ContinueWith(_ => { });
        }
    }

    // ── Connection ───────────────────────────────────────────────────────────────

    private async Task ConnectAndReceive(CancellationToken ct)
    {
        _ws = new ClientWebSocket();
        _logger.LogInformation("[Bridge] Connecting to Unity at {url}…", UnityWsUrl);
        await _ws.ConnectAsync(new Uri(UnityWsUrl), ct);
        _logger.LogInformation("[Bridge] Connected.");

        var buffer = new byte[32768];
        while (_ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            var result = await _ws.ReceiveAsync(buffer, ct);

            if (result.MessageType == WebSocketMessageType.Close)
            {
                await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", ct);
                break;
            }

            if (result.MessageType == WebSocketMessageType.Text)
            {
                string json = Encoding.UTF8.GetString(buffer, 0, result.Count);
                await HandleUnityMessage(json);
            }
        }
    }

    // ── Inbound (Unity → phones) ─────────────────────────────────────────────────

    private async Task HandleUnityMessage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("cmd", out var cmdEl)) return;

            string? cmd = cmdEl.GetString();

            switch (cmd)
            {
                case "player_list":
                    await HandlePlayerList(doc.RootElement);
                    break;

                case "game_started":
                    await HandleGameStarted(doc.RootElement);
                    break;

                case "state_update":
                    await HandleStateUpdate(doc.RootElement);
                    break;

                case "you_are_dead":
                    await HandleYouAreDead(doc.RootElement);
                    break;

                case "you_are_alive":
                    await HandleYouAreAlive(doc.RootElement);
                    break;

                case "game_over":
                    await HandleGameOver(doc.RootElement);
                    break;

                case "rfid_tag":
                    await HandleRfidTag(doc.RootElement);
                    break;

                case "display_update":
                    await _hub.Clients.All.SendAsync("DisplayUpdate", json);
                    break;

                case "display_event":
                    string? evtText = GetString(doc.RootElement, "text");
                    if (!string.IsNullOrEmpty(evtText))
                        await _hub.Clients.All.SendAsync("DisplayEvent", evtText);
                    break;

                case "game_paused":
                    await _hub.Clients.All.SendAsync("GamePaused");
                    break;

                case "game_resumed":
                    await _hub.Clients.All.SendAsync("GameResumed");
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("[Bridge] Failed to parse Unity message: {ex}", ex.Message);
        }
    }

    private async Task HandlePlayerList(JsonElement root)
    {
        var players = root.GetProperty("players")
            .EnumerateArray()
            .Select(e => e.GetString() ?? string.Empty)
            .Where(n => n.Length > 0)
            .ToList();

        await _hub.Clients.All.SendAsync("LobbyUpdate", players);
    }

    private async Task HandleGameStarted(JsonElement root)
    {
        string? connId   = GetString(root, "connectionId");
        if (string.IsNullOrEmpty(connId)) return;

        string callsign = GetString(root, "callsign") ?? "";
        string videoUrl = GetString(root, "videoUrl") ?? "";
        int    hp       = GetInt(root, "hp");
        int    maxHp    = GetInt(root, "maxHp", 100);

        await _hub.Clients.Client(connId).SendAsync("GameStarted",
            new { callsign, videoUrl, hp, maxHp });

        _logger.LogInformation("[Bridge] GameStarted → conn {c} (robot={r})", connId, callsign);
    }

    private async Task HandleStateUpdate(JsonElement root)
    {
        string? connId = GetString(root, "connectionId");
        if (string.IsNullOrEmpty(connId)) return;

        int   hp       = GetInt(root,   "hp");
        int   maxHp    = GetInt(root,   "maxHp", 100);
        float timer    = GetFloat(root, "timer");
        float cooldown = GetFloat(root, "cooldown");

        await _hub.Clients.Client(connId).SendAsync("StateUpdate",
            new { hp, maxHp, timer, cooldown });
    }

    private async Task HandleYouAreDead(JsonElement root)
    {
        string? connId = GetString(root, "connectionId");
        if (string.IsNullOrEmpty(connId)) return;

        await _hub.Clients.Client(connId).SendAsync("YouAreDead");
        _logger.LogInformation("[Bridge] YouAreDead → conn {c}", connId);
    }

    private async Task HandleYouAreAlive(JsonElement root)
    {
        string? connId = GetString(root, "connectionId");
        if (string.IsNullOrEmpty(connId)) return;

        await _hub.Clients.Client(connId).SendAsync("YouAreAlive");
        _logger.LogInformation("[Bridge] YouAreAlive → conn {c}", connId);
    }

    private async Task HandleRfidTag(JsonElement root)
    {
        string? connId = GetString(root, "connectionId");
        if (string.IsNullOrEmpty(connId)) return;

        string uid = GetString(root, "uid") ?? "";
        await _hub.Clients.Client(connId).SendAsync("RfidTag", uid);
        _logger.LogInformation("[Bridge] RfidTag uid={uid} → conn {c}", uid, connId);
    }

    private async Task HandleGameOver(JsonElement root)
    {
        string winnerTeam = GetString(root, "winnerTeam") ?? "Unknown";
        string reason     = GetString(root, "reason")     ?? "";

        await _hub.Clients.All.SendAsync("GameOver", new { winnerTeam, reason });
        _logger.LogInformation("[Bridge] GameOver: {team} ({reason})", winnerTeam, reason);
    }

    // ── Outbound (phones → Unity) ────────────────────────────────────────────────

    public async Task SendToUnity(object payload)
    {
        if (_ws?.State != WebSocketState.Open)
        {
            _logger.LogDebug("[Bridge] SendToUnity skipped — not connected.");
            return;
        }

        string json  = JsonSerializer.Serialize(payload);
        byte[] bytes = Encoding.UTF8.GetBytes(json);

        await _sendLock.WaitAsync();
        try
        {
            await _ws.SendAsync(
                new ArraySegment<byte>(bytes),
                WebSocketMessageType.Text,
                endOfMessage: true,
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("[Bridge] SendToUnity failed: {ex}", ex.Message);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    // ── JSON helpers ─────────────────────────────────────────────────────────────

    private static string? GetString(JsonElement el, string prop)
    {
        return el.TryGetProperty(prop, out var v) ? v.GetString() : null;
    }

    private static int GetInt(JsonElement el, string prop, int def = 0)
    {
        return el.TryGetProperty(prop, out var v) && v.TryGetInt32(out int i) ? i : def;
    }

    private static float GetFloat(JsonElement el, string prop, float def = 0f)
    {
        return el.TryGetProperty(prop, out var v) && v.TryGetSingle(out float f) ? f : def;
    }
}
