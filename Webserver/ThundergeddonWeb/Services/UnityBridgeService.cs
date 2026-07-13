using System.IO;
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
    private bool   _connectedToUnity = false;
    private bool   _joinAllowed      = false;
    private string _currentPhase     = "mainmenu";
    private string? _lastDisplayUpdate  = null;

    /// <summary>True while the WebSocket connection to Unity's PlayerWebSocketServer is open.</summary>
    public bool IsConnectedToUnity => _connectedToUnity;

    /// <summary>Last game phase received from Unity (e.g. "lobby", "playing", "ended").</summary>
    public string CurrentPhase => _currentPhase;

    /// <summary>Last display_update JSON received from Unity, or null if none yet received.</summary>
    public string? LastDisplayUpdate => _lastDisplayUpdate;

    /// <summary>
    /// True when Unity is reachable AND in Lobby (accepting new players).
    /// Phone clients gate the join button on this.
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
        using var ms = new MemoryStream();
        while (_ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            var result = await _ws.ReceiveAsync(buffer, ct);

            if (result.MessageType == WebSocketMessageType.Close)
            {
                await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", ct);
                break;
            }

            if (result.MessageType != WebSocketMessageType.Text) continue;

            // A single WebSocket message can arrive across multiple frames when it
            // exceeds the buffer (e.g. a large display_update / game_over). Accumulate
            // until EndOfMessage, then parse the whole message in one piece.
            ms.Write(buffer, 0, result.Count);
            if (!result.EndOfMessage) continue;

            string json = Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
            ms.SetLength(0);
            await HandleUnityMessage(json);
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

                case "robot_list":
                    await HandleRobotList(doc.RootElement);
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

                case "return_to_join":
                    await _hub.Clients.All.SendAsync("ReturnToJoin");
                    _logger.LogInformation("[Bridge] ReturnToJoin broadcast");
                    break;

                case "rfid_notification":
                    await HandleRfidNotification(doc.RootElement);
                    break;

                case "display_update":
                    _lastDisplayUpdate = json;
                    await _hub.Clients.All.SendAsync("DisplayUpdate", json);
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

                case "fire_event":
                {
                    string? robotId  = GetString(doc.RootElement, "robotId");
                    string? callsign = GetString(doc.RootElement, "callsign");
                    await _hub.Clients.All.SendAsync("FireEvent",
                        new { robotId = robotId ?? "", callsign = callsign ?? "" });
                    break;
                }

                case "hit_taken":
                    await HandleHitTaken(doc.RootElement);
                    break;

                case "hit_event":
                {
                    string? robotId  = GetString(doc.RootElement, "robotId");
                    string? callsign = GetString(doc.RootElement, "callsign");
                    await _hub.Clients.All.SendAsync("HitEvent",
                        new { robotId = robotId ?? "", callsign = callsign ?? "" });
                    break;
                }

                case "phase_changed":
                {
                    string? phase = GetString(doc.RootElement, "phase");
                    _currentPhase = phase ?? "mainmenu";
                    // Only Lobby allows new players to join; Playing and other phases lock the button.
                    bool nowAllowed = phase == "lobby";
                    if (nowAllowed != _joinAllowed)
                    {
                        _joinAllowed = nowAllowed;
                        // Use distinct "GameInProgress" event for playing so the client can
                        // grey the button without forcibly redirecting mid-game players.
                        string evt = nowAllowed ? "ServerConnected"
                                   : phase == "playing" ? "GameInProgress"
                                   : "ServerNotReady";
                        _logger.LogInformation("[Bridge] phase_changed → {phase}, event={evt}", phase, evt);
                        await _hub.Clients.All.SendAsync(evt, CancellationToken.None);
                    }
                    else
                    {
                        _logger.LogInformation("[Bridge] phase_changed → {phase} (no state change)", phase);
                    }
                    break;
                }

                case "countdown_start":
                {
                    int total = GetInt(doc.RootElement, "total", 5);
                    await _hub.Clients.All.SendAsync("CountdownStarted", total);
                    _logger.LogInformation("[Bridge] countdown_start total={total}", total);
                    break;
                }

                case "countdown_tick":
                {
                    int count = GetInt(doc.RootElement, "count", 1);
                    int total = GetInt(doc.RootElement, "total", 5);
                    await _hub.Clients.All.SendAsync("CountdownTick", count, total);
                    break;
                }

                case "kill_event":
                {
                    int    teamIndex       = GetInt(doc.RootElement, "teamIndex", -1);
                    string? targetCallsign = GetString(doc.RootElement, "targetCallsign");
                    int    points          = GetInt(doc.RootElement, "points");
                    await _hub.Clients.All.SendAsync("KillEvent",
                        new { teamIndex, targetCallsign = targetCallsign ?? "", points });
                    break;
                }

                case "tank_color":
                {
                    string? connId    = GetString(doc.RootElement, "connectionId");
                    string? colorName = GetString(doc.RootElement, "colorName");
                    if (!string.IsNullOrEmpty(connId) && !string.IsNullOrEmpty(colorName))
                        await _hub.Clients.Client(connId).SendAsync("TankColor", colorName);
                    break;
                }

                case "two_player_mode":
                {
                    bool enabled = doc.RootElement.TryGetProperty("enabled", out var tpEl)
                        && tpEl.ValueKind == JsonValueKind.True;
                    await _hub.Clients.All.SendAsync("TwoPlayerMode", enabled);
                    _logger.LogInformation("[Bridge] TwoPlayerMode → {en}", enabled);
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

    private async Task HandleRobotList(JsonElement root)
    {
        if (!root.TryGetProperty("robots", out var robotsEl)) return;

        bool twoPlayerModeEnabled = root.TryGetProperty("twoPlayerModeEnabled", out var tpme)
            && tpme.ValueKind == JsonValueKind.True;

        var robots = robotsEl
            .EnumerateArray()
            .Select(e =>
            {
                string id               = e.TryGetProperty("id",             out var i)  ? i.GetString()  ?? "" : "";
                string name             = e.TryGetProperty("name",           out var n)  ? n.GetString()  ?? "" : "";
                int    alliance         = e.TryGetProperty("alliance",       out var a) && a.TryGetInt32(out int ai) ? ai : -1;
                string assignedPlayer   = e.TryGetProperty("assignedPlayer", out var ap) ? ap.GetString() ?? "" : "";
                string gunnerPlayer     = e.TryGetProperty("gunnerPlayer",   out var gp) ? gp.GetString() ?? "" : "";
                bool   twoPlayerEnabled = e.TryGetProperty("twoPlayerEnabled", out var tp)
                    && tp.ValueKind == JsonValueKind.True;
                return new { id, name, alliance, assignedPlayer, gunnerPlayer, twoPlayerEnabled };
            })
            .ToList();

        await _hub.Clients.All.SendAsync("RobotListUpdate", new { robots, twoPlayerModeEnabled });
    }

    private async Task HandleGameStarted(JsonElement root)
    {
        string? connId   = GetString(root, "connectionId");
        if (string.IsNullOrEmpty(connId)) return;

        string callsign          = GetString(root, "callsign")         ?? "";
        string videoUrl          = GetString(root, "videoUrl")         ?? "";
        int    hp                = GetInt(root, "hp");
        int    maxHp             = GetInt(root, "maxHp", 100);
        float  slowTurretSpeed   = GetFloat(root, "slowTurretSpeed",   0.4f);
        float  cooldownDuration  = GetFloat(root, "cooldownDuration",  3f);
        string role              = GetString(root, "role")             ?? "solo";
        string partnerName       = GetString(root, "partnerName")      ?? "";

        await _hub.Clients.Client(connId).SendAsync("GameStarted",
            new { callsign, videoUrl, hp, maxHp, slowTurretSpeed, cooldownDuration, role, partnerName });

        _logger.LogInformation("[Bridge] GameStarted → conn {c} (robot={r}, role={role})",
            connId, callsign, role);
    }

    private async Task HandleStateUpdate(JsonElement root)
    {
        string? connId = GetString(root, "connectionId");
        if (string.IsNullOrEmpty(connId)) return;

        int   hp               = GetInt(root,   "hp");
        int   maxHp            = GetInt(root,   "maxHp", 100);
        float timer            = GetFloat(root, "timer");
        float cooldown         = GetFloat(root, "cooldown");
        float cooldownDuration = GetFloat(root, "cooldownDuration", 3f);

        await _hub.Clients.Client(connId).SendAsync("StateUpdate",
            new { hp, maxHp, timer, cooldown, cooldownDuration });
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

    private async Task HandleRfidNotification(JsonElement root)
    {
        string? connId = GetString(root, "connectionId");
        if (string.IsNullOrEmpty(connId)) return;

        string text = GetString(root, "text") ?? "";
        // Reuse the existing RfidTag SignalR event so cached browser clients also receive it.
        await _hub.Clients.Client(connId).SendAsync("RfidTag", text);
        _logger.LogInformation("[Bridge] RfidTag (notification) text={text} → conn {c}", text, connId);
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
        bool   hasStats      = root.TryGetProperty("playerStats", out var statsArray);
        string mostGroundP   = GetString(root, "mostGround") ?? "";
        string bestPlayerP   = GetString(root, "bestPlayer") ?? "";

        // ── Aggregate stats for the display page ──────────────────────────────────
        object? displayStats = null;
        if (hasStats)
        {
            string killsP = "", deathsP = "", damGvnP = "", ptsP = "", capP = "";
            int    killsV = -1, deathsV = -1, damGvnV = -1, ptsV = -1, capV = -1;

            foreach (var s in statsArray.EnumerateArray())
            {
                string? n = GetString(s, "playerName");
                if (string.IsNullOrEmpty(n)) continue;
                int k  = GetInt(s, "kills"),  d = GetInt(s, "deaths"),
                    dm = GetInt(s, "damage"), vp = GetInt(s, "victoryPoints"),
                    cp = GetInt(s, "vpFromCaptures");
                if (k  > killsV)  { killsV  = k;  killsP  = n; }
                if (d  > deathsV) { deathsV = d;  deathsP = n; }
                if (dm > damGvnV) { damGvnV = dm; damGvnP = n; }
                if (vp > ptsV)    { ptsV    = vp; ptsP    = n; }
                if (cp > capV)    { capV    = cp; capP    = n; }
            }

            // Best player: authoritative value from Unity (most victory points, i.e.
            // captures + kills — matches the spoken announcement exactly).
            string bestP = bestPlayerP;

            displayStats = new
            {
                bestPlayer      = bestP.Length > 0 ? (object)new { player = bestP }                              : null,
                mostKills       = killsV  >= 0     ? (object)new { player = killsP,  value = (object)killsV  }  : null,
                mostDeaths      = deathsV >= 0     ? (object)new { player = deathsP, value = (object)deathsV }  : null,
                mostDamageGiven = damGvnV >= 0     ? (object)new { player = damGvnP, value = (object)damGvnV }  : null,
                mostPoints      = ptsV    >= 0     ? (object)new { player = ptsP,    value = (object)ptsV    }  : null,
                mostCaptures    = capV    >= 0     ? (object)new { player = capP,    value = (object)capV    }  : null,
                mostGround      = mostGroundP.Length > 0 ? (object)new { player = mostGroundP }                : null,
            };
        }

        await _hub.Clients.All.SendAsync("GameOver", new { winnerTeam, reason, stats = displayStats });

        // ── Route per-player stats to individual phone connections ────────────────
        if (hasStats)
        {
            foreach (var stat in statsArray.EnumerateArray())
            {
                string? connId = GetString(stat, "connectionId");
                if (string.IsNullOrEmpty(connId)) continue;
                int    kills          = GetInt(stat,    "kills");
                int    deaths         = GetInt(stat,    "deaths");
                int    damage         = GetInt(stat,    "damage");
                int    victoryPoints  = GetInt(stat,    "victoryPoints");
                int    vpFromCaptures = GetInt(stat,    "vpFromCaptures");
                int    vpFromKills    = GetInt(stat,    "vpFromKills");
                string nemesis        = GetString(stat, "nemesis") ?? "";
                await _hub.Clients.Client(connId).SendAsync("PlayerStats",
                    new { kills, deaths, damage, victoryPoints, vpFromCaptures, vpFromKills, nemesis });
            }
        }

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
