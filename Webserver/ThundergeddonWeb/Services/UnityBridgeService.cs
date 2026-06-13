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
    private bool _connectedToUnity = false;
    private bool _joinAllowed      = false;

    /// <summary>True while the WebSocket connection to Unity's PlayerWebSocketServer is open.</summary>
    public bool IsConnectedToUnity => _connectedToUnity;

    /// <summary>
    /// True when Unity is reachable AND in a phase that accepts new players
    /// (Lobby or Playing).  Phone clients should gate the join button on this.
    /// </summary>
    public bool IsGameReady => _connectedToUnity && _joinAllowed;

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

            if (_connectedToUnity)
            {
                _connectedToUnity = false;
                _joinAllowed      = false;
                await _hub.Clients.All.SendAsync("ServerDisconnected", CancellationToken.None);
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
        _connectedToUnity = true;
        _logger.LogInformation("[Bridge] Connected. Awaiting phase_changed from Unity…");

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

                case "join_rejected":
                {
                    string? connId = GetString(doc.RootElement, "connectionId");
                    string? reason = GetString(doc.RootElement, "reason") ?? "Name already taken.";
                    if (!string.IsNullOrEmpty(connId))
                        await _hub.Clients.Client(connId).SendAsync("JoinRejected", reason);
                    break;
                }

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

                case "spectate_update":
                    await _hub.Clients.All.SendAsync("SpectateUpdate", json);
                    break;

                case "display_event":
                    string? evtText = GetString(doc.RootElement, "text");
                    if (!string.IsNullOrEmpty(evtText))
                        await _hub.Clients.All.SendAsync("DisplayEvent", evtText);
                    break;

                case "turret_settings":
                    if (doc.RootElement.TryGetProperty("slowSpeed", out var ssEl)
                        && ssEl.TryGetSingle(out float ss))
                        await _hub.Clients.All.SendAsync("TurretSettings", ss);
                    break;

                case "fire_result":
                    await HandleFireResult(doc.RootElement);
                    break;

                case "hit_taken":
                    await HandleHitTaken(doc.RootElement);
                    break;

                case "phase_changed":
                {
                    string? phase = GetString(doc.RootElement, "phase");
                    // Lobby and Playing both accept player joins (Playing allows reconnects).
                    bool nowAllowed = phase == "lobby" || phase == "playing";
                    if (nowAllowed != _joinAllowed)
                    {
                        _joinAllowed = nowAllowed;
                        _logger.LogInformation("[Bridge] phase_changed → {phase}, joinAllowed={a}", phase, nowAllowed);
                        await _hub.Clients.All.SendAsync(
                            nowAllowed ? "ServerConnected" : "ServerNotReady",
                            CancellationToken.None);
                    }
                    else
                    {
                        _logger.LogInformation("[Bridge] phase_changed → {phase} (no state change)", phase);
                    }
                    break;
                }

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
            .Select(e =>
            {
                string name    = e.TryGetProperty("name",    out var n) ? n.GetString() ?? "" : e.GetString() ?? "";
                int    alliance = e.TryGetProperty("alliance", out var a) && a.TryGetInt32(out int i) ? i : -1;
                string robot   = e.TryGetProperty("robot",   out var r) ? r.GetString() ?? "" : "";
                return new { name, alliance, robot };
            })
            .Where(p => p.name.Length > 0)
            .ToList();

        await _hub.Clients.All.SendAsync("LobbyUpdate", players);
    }

    private async Task HandleGameStarted(JsonElement root)
    {
        string? connId   = GetString(root, "connectionId");
        if (string.IsNullOrEmpty(connId)) return;

        string callsign        = GetString(root, "callsign") ?? "";
        string videoUrl        = GetString(root, "videoUrl") ?? "";
        int    hp              = GetInt(root, "hp");
        int    maxHp           = GetInt(root, "maxHp", 100);
        float  slowTurretSpeed = GetFloat(root, "slowTurretSpeed", 0.4f);

        await _hub.Clients.Client(connId).SendAsync("GameStarted",
            new { callsign, videoUrl, hp, maxHp, slowTurretSpeed });

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

    private async Task HandleFireResult(JsonElement root)
    {
        string? connId = GetString(root, "connectionId");
        string? text   = GetString(root, "text");
        if (string.IsNullOrEmpty(connId) || string.IsNullOrEmpty(text)) return;
        await _hub.Clients.Client(connId).SendAsync("FireResult", text);
        _logger.LogDebug("[Bridge] FireResult → conn {c}: {t}", connId, text);
    }

    private async Task HandleHitTaken(JsonElement root)
    {
        string? connId  = GetString(root, "connectionId");
        if (string.IsNullOrEmpty(connId)) return;
        string shooter = GetString(root, "shooter") ?? "";
        string dir     = GetString(root, "dir") ?? "";
        await _hub.Clients.Client(connId).SendAsync("HitTaken", new { shooter, dir });
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
