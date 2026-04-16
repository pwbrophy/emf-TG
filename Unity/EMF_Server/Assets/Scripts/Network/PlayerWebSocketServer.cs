// PlayerWebSocketServer.cs
// WebSocket server on port 8081 that bridges the ASP.NET web server to Unity.
//
// ── Lobby protocol ────────────────────────────────────────────────────
// ASP.NET → Unity:
//   {"cmd":"join",   "name":"Alice", "connectionId":"abc"}
//   {"cmd":"leave",  "connectionId":"abc"}
//
// Unity → ASP.NET (broadcast):
//   {"cmd":"player_list","players":["Alice","Bob",...]}
//
// ── Gameplay protocol ─────────────────────────────────────────────────
// ASP.NET → Unity:
//   {"cmd":"drive",  "connectionId":"abc", "l":0.5,  "r":-0.3}
//   {"cmd":"turret", "connectionId":"abc", "speed":0.7}
//   {"cmd":"fire",   "connectionId":"abc"}
//
// Unity → ASP.NET (targeted by connectionId):
//   {"cmd":"game_started","connectionId":"abc","callsign":"Thunder1","videoUrl":"http://…","hp":100,"maxHp":100}
//   {"cmd":"state_update", "connectionId":"abc","hp":75,"maxHp":100,"timer":142.0,"cooldown":0.0}
//   {"cmd":"you_are_dead", "connectionId":"abc"}
//
// Unity → ASP.NET (broadcast — no connectionId):
//   {"cmd":"game_over","winnerTeam":"Alliance 1","reason":"elimination"}

using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using WebSocketSharp;
using WebSocketSharp.Server;

public class PlayerWebSocketServer : MonoBehaviour
{
    [Header("WebSocket Host Settings")]
    public int    Port   = 8081;
    public string WsPath = "/players";

    private bool _started;
    private WebSocketServer _wss;

    // Phone connectionId (SignalR) → player name
    private readonly Dictionary<string, string> _connToPlayer =
        new Dictionary<string, string>();

    // WebSocket sessionId (bridge WS conn) → phone connectionIds routed through it
    private readonly Dictionary<string, HashSet<string>> _sessionToConns =
        new Dictionary<string, HashSet<string>>();

    // Main-thread dispatch queue
    private readonly Queue<Action> _main = new Queue<Action>();
    private readonly object _mtx = new object();

    // Throttle state broadcasts to ~1 Hz
    private float _lastStateUpdate = 0f;

    // ── Events (consumed by UI components) ───────────────────────────────────────

    /// Fired when a phone sends drive/turret input.
    /// Args: playerName, leftMotor, rightMotor, turretSpeed
    public event Action<string, float, float, float> OnPlayerInput;

    // ── Lifecycle ────────────────────────────────────────────────────────────────

    private void Awake()
    {
        ServiceLocator.PlayerServer = this;
    }

    private void Start()
    {
        StartServer();

        // Player list changes
        if (ServiceLocator.Players != null)
            ServiceLocator.Players.OnChanged += OnPlayersChanged;

        // Game phase changes
        if (ServiceLocator.GameFlow != null)
            ServiceLocator.GameFlow.OnPhaseChanged += OnPhaseChanged;

        // HP / death / win events
        if (ServiceLocator.Game != null)
        {
            ServiceLocator.Game.OnHpChanged  += OnHpChanged;
            ServiceLocator.Game.OnRobotDied  += OnRobotDied;
            ServiceLocator.Game.OnGameWon    += OnGameWon;
        }
    }

    private void OnDestroy()
    {
        if (ServiceLocator.Players != null)
            ServiceLocator.Players.OnChanged -= OnPlayersChanged;

        if (ServiceLocator.GameFlow != null)
            ServiceLocator.GameFlow.OnPhaseChanged -= OnPhaseChanged;

        if (ServiceLocator.Game != null)
        {
            ServiceLocator.Game.OnHpChanged -= OnHpChanged;
            ServiceLocator.Game.OnRobotDied -= OnRobotDied;
            ServiceLocator.Game.OnGameWon   -= OnGameWon;
        }

        if (ServiceLocator.PlayerServer == this)
            ServiceLocator.PlayerServer = null;

        StopServer();
    }

    // ── Server control ───────────────────────────────────────────────────────────

    public void StartServer()
    {
        if (_started) return;

        _wss = new WebSocketServer("ws://127.0.0.1:" + Port);
        _wss.KeepClean = false;
        var self = this;
        _wss.AddWebSocketService<BridgeService>(WsPath, () => new BridgeService { Parent = self });
        _wss.Start();
        _started = true;
        Debug.Log("[PlayerWS] Listening on ws://127.0.0.1:" + Port + WsPath);
    }

    public void StopServer()
    {
        if (!_started) return;
        try { _wss.Stop(); } catch { /* ignore */ }
        _wss = null;
        _started = false;
    }

    // ── Update loop ──────────────────────────────────────────────────────────────

    private void Update()
    {
        PumpMain();

        // Push state updates (timer) to all players at ~1 Hz
        if (_started && Time.time - _lastStateUpdate >= 1.0f)
        {
            _lastStateUpdate = Time.time;
            BroadcastStateUpdates();
        }
    }

    private void PumpMain()
    {
        for (;;)
        {
            Action a = null;
            lock (_mtx) { if (_main.Count == 0) break; a = _main.Dequeue(); }
            try { a?.Invoke(); } catch (Exception ex) { Debug.LogException(ex); }
        }
    }

    public void PostMain(Action a) { if (a == null) return; lock (_mtx) _main.Enqueue(a); }

    // ── WS session callbacks (from WS thread → post to main) ─────────────────────

    internal void OnSessionOpened(string sessionId)  => PostMain(() => HandleSessionOpened(sessionId));
    internal void OnSessionMessage(string sessionId, string json) => PostMain(() => HandleMessage(sessionId, json));
    internal void OnSessionClosed(string sessionId)  => PostMain(() => HandleSessionClosed(sessionId));

    // ── Session lifecycle (main thread) ──────────────────────────────────────────

    void HandleSessionOpened(string sessionId)
    {
        _sessionToConns[sessionId] = new HashSet<string>();
        Debug.Log("[PlayerWS] Bridge connected: " + sessionId);
        BroadcastPlayerList();
    }

    void HandleSessionClosed(string sessionId)
    {
        Debug.Log("[PlayerWS] Bridge disconnected: " + sessionId);
        if (_sessionToConns.TryGetValue(sessionId, out var conns))
        {
            foreach (string connId in new List<string>(conns))
                HandleLeave(connId);
            _sessionToConns.Remove(sessionId);
        }
        BroadcastPlayerList();
    }

    // ── Message dispatch (main thread) ───────────────────────────────────────────

    void HandleMessage(string sessionId, string json)
    {
        BridgeMsg msg;
        try { msg = JsonUtility.FromJson<BridgeMsg>(json); }
        catch { return; }
        if (msg == null || string.IsNullOrWhiteSpace(msg.cmd)) return;

        switch (msg.cmd)
        {
            case "join":
                HandleJoin(sessionId, msg.connectionId, msg.name);
                break;

            case "leave":
                HandleLeave(msg.connectionId);
                if (_sessionToConns.TryGetValue(sessionId, out var s)) s.Remove(msg.connectionId);
                BroadcastPlayerList();
                break;

            case "drive":
                HandleDrive(msg.connectionId, msg.l, msg.r);
                break;

            case "turret":
                HandleTurret(msg.connectionId, msg.speed);
                break;

            case "fire":
                HandleFire(msg.connectionId);
                break;
        }
    }

    // ── Lobby handlers ───────────────────────────────────────────────────────────

    void HandleJoin(string sessionId, string connId, string name)
    {
        if (string.IsNullOrWhiteSpace(connId) || string.IsNullOrWhiteSpace(name)) return;
        if (_connToPlayer.ContainsKey(connId)) return;

        _connToPlayer[connId] = name;
        if (_sessionToConns.TryGetValue(sessionId, out var conns)) conns.Add(connId);

        ServiceLocator.Players?.AddPlayer(name, 0);
        Debug.Log("[PlayerWS] Player joined: " + name + " (conn=" + connId + ")");

        // Auto-assign the first available (unassigned) robot to the new player.
        TryAssignFreeRobotToPlayer(name);

        BroadcastPlayerList();
    }

    // Find the first robot with no assigned player and give it to playerName.
    void TryAssignFreeRobotToPlayer(string playerName)
    {
        var dir = ServiceLocator.RobotDirectory;
        if (dir == null) return;

        foreach (var robot in dir.GetAll())
        {
            if (string.IsNullOrEmpty(robot.AssignedPlayer))
            {
                dir.SetAssignedPlayer(robot.RobotId, playerName);
                Debug.Log("[PlayerWS] Auto-assigned robot " + robot.RobotId + " to player " + playerName);
                return;
            }
        }
    }

    void HandleLeave(string connId)
    {
        if (!_connToPlayer.TryGetValue(connId, out string playerName)) return;
        _connToPlayer.Remove(connId);

        var players = ServiceLocator.Players?.GetAll();
        if (players == null) return;
        for (int i = 0; i < players.Count; i++)
        {
            if (players[i].Name == playerName)
            {
                ServiceLocator.Players.RemovePlayerAt(i);
                Debug.Log("[PlayerWS] Player removed: " + playerName);
                return;
            }
        }
    }

    // ── Gameplay input handlers ───────────────────────────────────────────────────

    void HandleDrive(string connId, float l, float r)
    {
        string robotId = ConnToRobot(connId);
        if (robotId == null) return;

        ServiceLocator.RobotServer?.SendDrive(robotId, l, r);

        string playerName = _connToPlayer.TryGetValue(connId, out var n) ? n : null;
        if (playerName != null)
            OnPlayerInput?.Invoke(playerName, l, r, GetLastTurret(playerName));

        _lastTurret[connId] = GetLastTurret(_connToPlayer.TryGetValue(connId, out var nm) ? nm : "");
    }

    void HandleTurret(string connId, float speed)
    {
        string robotId = ConnToRobot(connId);
        if (robotId == null) return;

        ServiceLocator.RobotServer?.SendTurret(robotId, speed);
        _lastTurretByConn[connId] = speed;

        string playerName = _connToPlayer.TryGetValue(connId, out var n) ? n : null;
        if (playerName != null)
            OnPlayerInput?.Invoke(playerName, 0f, 0f, speed);
    }

    void HandleFire(string connId)
    {
        string robotId = ConnToRobot(connId);
        if (robotId == null) return;

        var shooting = ServiceLocator.Shooting;
        if (shooting == null)
        {
            Debug.LogWarning("[PlayerWS] ShootingController not available.");
            return;
        }

        shooting.RequestFire(robotId);
    }

    // Track last turret value per connection for the OnPlayerInput event
    private readonly Dictionary<string, float> _lastTurretByConn = new Dictionary<string, float>();
    private readonly Dictionary<string, float> _lastTurret        = new Dictionary<string, float>();

    float GetLastTurret(string playerName)
    {
        foreach (var kvp in _connToPlayer)
            if (kvp.Value == playerName)
            {
                _lastTurretByConn.TryGetValue(kvp.Key, out float t);
                return t;
            }
        return 0f;
    }

    // ── Game event handlers (main thread) ────────────────────────────────────────

    void OnPhaseChanged(GamePhase phase)
    {
        if (phase == GamePhase.Playing)
            SendGameStartedToAll();
        else if (phase == GamePhase.Ended)
            SendGameOver();
    }

    void OnHpChanged(string robotId, int newHp)
    {
        // Find all connections whose player drives this robot
        foreach (var kvp in _connToPlayer)
        {
            string connId    = kvp.Key;
            string playerName = kvp.Value;
            string rId       = PlayerToRobot(playerName);
            if (rId != robotId) continue;

            SendSingleStateUpdate(connId);
        }
    }

    void OnRobotDied(string robotId)
    {
        foreach (var kvp in _connToPlayer)
        {
            string rId = PlayerToRobot(kvp.Value);
            if (rId != robotId) continue;

            string json = "{\"cmd\":\"you_are_dead\",\"connectionId\":\"" +
                          EscapeJson(kvp.Key) + "\"}";
            BroadcastRaw(json);
            Debug.Log("[PlayerWS] Sent you_are_dead to " + kvp.Value);
        }
    }

    void OnGameWon(int allianceIndex, string reason)
    {
        string teamName = "Alliance " + (allianceIndex + 1);
        string json = "{\"cmd\":\"game_over\",\"winnerTeam\":\"" +
                      EscapeJson(teamName) + "\",\"reason\":\"" +
                      EscapeJson(reason) + "\"}";
        BroadcastRaw(json);
        Debug.Log("[PlayerWS] Sent game_over: " + teamName + " (" + reason + ")");
    }

    void OnPlayersChanged() => BroadcastPlayerList();

    // ── State update helpers ──────────────────────────────────────────────────────

    void SendGameStartedToAll()
    {
        foreach (var kvp in _connToPlayer)
            SendGameStarted(kvp.Key, kvp.Value);
    }

    void SendGameStarted(string connId, string playerName)
    {
        string robotId = PlayerToRobot(playerName);
        var dir = ServiceLocator.RobotDirectory;
        string callsign = robotId;
        string ip       = "";

        if (robotId != null && dir != null && dir.TryGet(robotId, out var rInfo))
        {
            callsign = string.IsNullOrEmpty(rInfo.Callsign) ? robotId : rInfo.Callsign;
            ip       = rInfo.Ip ?? "";
        }

        int maxHp = ServiceLocator.GameSettings?.MaxHp ?? 100;
        int hp    = GetCurrentHp(robotId, maxHp);

        string videoUrl = string.IsNullOrEmpty(ip) ? "" : "http://" + ip + ":81/stream";

        string json =
            "{\"cmd\":\"game_started\"" +
            ",\"connectionId\":\""  + EscapeJson(connId)    + "\"" +
            ",\"callsign\":\""      + EscapeJson(callsign)  + "\"" +
            ",\"videoUrl\":\""      + EscapeJson(videoUrl)  + "\"" +
            ",\"hp\":"              + hp                           +
            ",\"maxHp\":"           + maxHp                        +
            "}";

        BroadcastRaw(json);
        Debug.Log("[PlayerWS] Sent game_started to " + playerName + " (robot=" + callsign + ")");
    }

    void BroadcastStateUpdates()
    {
        if (ServiceLocator.GameFlow?.Phase != GamePhase.Playing) return;
        foreach (var kvp in _connToPlayer)
            SendSingleStateUpdate(kvp.Key);
    }

    void SendSingleStateUpdate(string connId)
    {
        if (!_connToPlayer.TryGetValue(connId, out string playerName)) return;

        string robotId = PlayerToRobot(playerName);
        int maxHp      = ServiceLocator.GameSettings?.MaxHp ?? 100;
        int hp         = GetCurrentHp(robotId, maxHp);
        float timer    = ServiceLocator.MatchTimer?.Remaining ?? 0f;

        string json =
            "{\"cmd\":\"state_update\"" +
            ",\"connectionId\":\""  + EscapeJson(connId) + "\"" +
            ",\"hp\":"              + hp                        +
            ",\"maxHp\":"           + maxHp                     +
            ",\"timer\":"           + timer.ToString("F1")      +
            ",\"cooldown\":0.0"     +
            "}";

        BroadcastRaw(json);
    }

    void SendGameOver()
    {
        // OnGameWon fires before EndGame in most cases; this is a fallback.
        // Only send if we haven't already (OnGameWon handles it).
    }

    // ── Player list broadcast ─────────────────────────────────────────────────────

    void BroadcastPlayerList()
    {
        var players = ServiceLocator.Players?.GetAll();
        string json = BuildPlayerListJson(players);
        BroadcastRaw(json);
    }

    string BuildPlayerListJson(IReadOnlyList<PlayerInfo> players)
    {
        var sb = new StringBuilder("{\"cmd\":\"player_list\",\"players\":[");
        if (players != null)
        {
            for (int i = 0; i < players.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append('"');
                sb.Append(EscapeJson(players[i].Name));
                sb.Append('"');
            }
        }
        sb.Append("]}");
        return sb.ToString();
    }

    void BroadcastRaw(string json)
    {
        if (!_started || _wss == null) return;
        try { _wss.WebSocketServices[WsPath].Sessions.Broadcast(json); }
        catch (Exception ex) { Debug.LogWarning("[PlayerWS] Broadcast error: " + ex.Message); }
    }

    // ── Lookup helpers ───────────────────────────────────────────────────────────

    string ConnToRobot(string connId)
    {
        if (!_connToPlayer.TryGetValue(connId, out string playerName)) return null;
        return PlayerToRobot(playerName);
    }

    string PlayerToRobot(string playerName)
    {
        if (string.IsNullOrEmpty(playerName)) return null;
        var dir = ServiceLocator.RobotDirectory;
        if (dir == null) return null;
        foreach (var r in dir.GetAll())
            if (r.AssignedPlayer == playerName) return r.RobotId;
        return null;
    }

    int GetCurrentHp(string robotId, int defaultMax)
    {
        if (string.IsNullOrEmpty(robotId)) return defaultMax;
        var state = ServiceLocator.Game?.State;
        if (state == null) return defaultMax;
        return state.RobotHp.TryGetValue(robotId, out int hp) ? hp : defaultMax;
    }

    static string EscapeJson(string s)
    {
        if (s == null) return "";
        return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }

    // ── Message DTO ─────────────────────────────────────────────────────────────

    [Serializable]
    class BridgeMsg
    {
        public string cmd;
        public string name;
        public string connectionId;
        public float  l;
        public float  r;
        public float  speed;
    }

    // ── WebSocket behaviour ──────────────────────────────────────────────────────

    class BridgeService : WebSocketBehavior
    {
        public PlayerWebSocketServer Parent;

        protected override void OnOpen()    { Parent?.OnSessionOpened(ID); }
        protected override void OnMessage(MessageEventArgs e) { if (e.IsText) Parent?.OnSessionMessage(ID, e.Data); }
        protected override void OnClose(CloseEventArgs e)     { Parent?.OnSessionClosed(ID); }
        protected override void OnError(ErrorEventArgs e)     { Debug.LogWarning("[PlayerWS] Error: " + e.Message); }
    }
}
