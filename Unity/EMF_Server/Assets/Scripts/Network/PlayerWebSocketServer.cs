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
    private float _lastCaptureTick = 0f;

    // robotId → Time.time when the robot died (used to time the 5-s explosion → dead-walk transition)
    private readonly Dictionary<string, float> _deathTimes = new Dictionary<string, float>();

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
        {
            ServiceLocator.GameFlow.OnPhaseChanged  += OnPhaseChanged;
            ServiceLocator.GameFlow.OnPausedChanged += OnPausedChanged;
        }

        // HP / death / respawn / win events
        if (ServiceLocator.Game != null)
        {
            ServiceLocator.Game.OnHpChanged       += OnHpChanged;
            ServiceLocator.Game.OnRobotDied       += OnRobotDied;
            ServiceLocator.Game.OnRobotRespawned  += OnRobotRespawned;
            ServiceLocator.Game.OnGameWon         += OnGameWon;
        }

        // RFID tag scans
        if (ServiceLocator.RobotServer != null)
            ServiceLocator.RobotServer.OnRfidTag += OnRfidTag;

        // Capture points & match timer tick
        if (ServiceLocator.CapturePoints != null)
        {
            ServiceLocator.CapturePoints.OnPointCaptured    += OnCapturePointCaptured;
            ServiceLocator.CapturePoints.OnTeamPointsChanged += OnTeamPointsChanged;
        }
        if (ServiceLocator.MatchTimer != null)
            ServiceLocator.MatchTimer.OnTick += OnMatchTimerTick;
    }

    private void OnDestroy()
    {
        if (ServiceLocator.Players != null)
            ServiceLocator.Players.OnChanged -= OnPlayersChanged;

        if (ServiceLocator.GameFlow != null)
        {
            ServiceLocator.GameFlow.OnPhaseChanged  -= OnPhaseChanged;
            ServiceLocator.GameFlow.OnPausedChanged -= OnPausedChanged;
        }

        if (ServiceLocator.Game != null)
        {
            ServiceLocator.Game.OnHpChanged      -= OnHpChanged;
            ServiceLocator.Game.OnRobotDied      -= OnRobotDied;
            ServiceLocator.Game.OnRobotRespawned -= OnRobotRespawned;
            ServiceLocator.Game.OnGameWon        -= OnGameWon;
        }

        if (ServiceLocator.RobotServer != null)
            ServiceLocator.RobotServer.OnRfidTag -= OnRfidTag;

        if (ServiceLocator.CapturePoints != null)
        {
            ServiceLocator.CapturePoints.OnPointCaptured    -= OnCapturePointCaptured;
            ServiceLocator.CapturePoints.OnTeamPointsChanged -= OnTeamPointsChanged;
        }
        if (ServiceLocator.MatchTimer != null)
            ServiceLocator.MatchTimer.OnTick -= OnMatchTimerTick;

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
        CheckDeathTransitions();

        // Push state updates (timer) to all players and display at ~1 Hz
        if (_started && Time.time - _lastStateUpdate >= 1.0f)
        {
            _lastStateUpdate = Time.time;
            BroadcastStateUpdates();
            BroadcastDisplayUpdate();
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

        // Block all movement during the 5-s explosion phase
        var state = ServiceLocator.Game?.State;
        if (state != null && state.DeadRobots.Contains(robotId)) return;

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

        // Block during explosion phase
        var state = ServiceLocator.Game?.State;
        if (state != null && state.DeadRobots.Contains(robotId)) return;

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

        // Block fire while dead (exploding) or in dead walk (respawning)
        var state = ServiceLocator.Game?.State;
        if (state != null && (state.DeadRobots.Contains(robotId) || state.RespawningRobots.Contains(robotId))) return;

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
        {
            SendGameStartedToAll();
            ActivateAllRobots();
        }
        else if (phase == GamePhase.Ended)
        {
            SendGameOver();
            DeactivateAllRobots();
            BroadcastDisplayUpdate();
        }
    }

    void ActivateAllRobots()
    {
        var dir = ServiceLocator.RobotDirectory;
        var ws  = ServiceLocator.RobotServer;
        if (dir == null || ws == null) return;
        foreach (var robot in dir.GetAll())
        {
            ws.SendStreamOn(robot.RobotId);
            ws.SendMotorsOn(robot.RobotId);
        }
    }

    void DeactivateAllRobots()
    {
        var dir = ServiceLocator.RobotDirectory;
        var ws  = ServiceLocator.RobotServer;
        if (dir == null || ws == null) return;
        foreach (var robot in dir.GetAll())
        {
            ws.SendStreamOff(robot.RobotId);
            ws.SendMotorsOff(robot.RobotId);
        }
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
        // Record when the robot died so Update() can transition it to dead walk after 5 s
        _deathTimes[robotId] = Time.time;

        // Disable motors and trigger death explosion on the robot
        var robotServer = ServiceLocator.RobotServer;
        robotServer?.SendMotorsOff(robotId);
        robotServer?.SendFlashDeath(robotId);

        // Notify the assigned player's phone
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

    void OnRobotRespawned(string robotId)
    {
        // Notify the assigned player so their phone can exit the dead screen
        foreach (var kvp in _connToPlayer)
        {
            if (PlayerToRobot(kvp.Value) != robotId) continue;

            string json = "{\"cmd\":\"you_are_alive\",\"connectionId\":\"" +
                          EscapeJson(kvp.Key) + "\"}";
            BroadcastRaw(json);
            SendSingleStateUpdate(kvp.Key);
            Debug.Log("[PlayerWS] Sent you_are_alive to " + kvp.Value);
        }
    }

    // Called every Update — moves robots from DeadRobots → RespawningRobots after 5 s,
    // re-enables motors so the player can drive back to base.
    void CheckDeathTransitions()
    {
        if (_deathTimes.Count == 0) return;
        var game = ServiceLocator.Game;
        if (game?.State == null) return;

        List<string> toTransition = null;
        foreach (var kvp in _deathTimes)
        {
            if (Time.time - kvp.Value >= 5f)
            {
                if (toTransition == null) toTransition = new List<string>();
                toTransition.Add(kvp.Key);
            }
        }

        if (toTransition == null) return;
        foreach (var robotId in toTransition)
        {
            _deathTimes.Remove(robotId);
            game.TransitionToRespawning(robotId);
            ServiceLocator.RobotServer?.SendMotorsOn(robotId);
            Debug.Log($"[PlayerWS] {robotId} → dead walk after explosion");
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

    void OnRfidTag(string robotId, string uid)
    {
        var settings = ServiceLocator.GameSettings;
        var game     = ServiceLocator.Game;

        // ---- Respawning robot at its team base → full revival ----
        if (settings != null && game?.State != null && game.State.RespawningRobots.Contains(robotId))
        {
            int alliance = GetRobotAllianceIndex(robotId);
            string baseUid = alliance == 0 ? settings.Alliance0BaseUid :
                             alliance == 1 ? settings.Alliance1BaseUid : null;
            if (!string.IsNullOrEmpty(baseUid) && uid == baseUid)
            {
                game.RespawnRobot(robotId);
                ServiceLocator.RobotServer?.SendFlashHeal(robotId);
                if (game.State.RobotHp.TryGetValue(robotId, out int hp))
                    ServiceLocator.RobotServer?.SendSetHp(robotId, hp, settings.MaxHp);
            }
            return; // respawning robots don't capture points
        }

        // ---- Dead robots (explosion phase) ignore all RFID tags ----
        if (game?.State != null && game.State.DeadRobots.Contains(robotId)) return;

        // ---- Normal play: try capture point, then check own base heal ----
        ServiceLocator.CapturePoints?.TryCapture(robotId, uid);

        if (settings != null && game?.State != null)
        {
            int alliance = GetRobotAllianceIndex(robotId);
            string baseUid = alliance == 0 ? settings.Alliance0BaseUid :
                             alliance == 1 ? settings.Alliance1BaseUid : null;
            if (!string.IsNullOrEmpty(baseUid) && uid == baseUid)
            {
                game.RestoreHp(robotId);
                ServiceLocator.RobotServer?.SendFlashHeal(robotId);
                if (game.State.RobotHp.TryGetValue(robotId, out int hp))
                    ServiceLocator.RobotServer?.SendSetHp(robotId, hp, settings.MaxHp);
            }
        }

        // Forward RFID tag event to the assigned player's phone
        foreach (var kvp in _connToPlayer)
        {
            string connId     = kvp.Key;
            string playerName = kvp.Value;
            if (PlayerToRobot(playerName) != robotId) continue;

            string json = "{\"cmd\":\"rfid_tag\"" +
                          ",\"connectionId\":\"" + EscapeJson(connId) + "\"" +
                          ",\"uid\":\""           + EscapeJson(uid)   + "\"}";
            BroadcastRaw(json);
            Debug.Log($"[PlayerWS] rfid_tag uid={uid} -> {playerName}");
            return;
        }
        Debug.Log($"[PlayerWS] rfid_tag uid={uid} from {robotId} (no assigned player)");
    }

    void OnPausedChanged(bool paused)
    {
        var robotServer = ServiceLocator.RobotServer;
        var dir         = ServiceLocator.RobotDirectory;
        if (robotServer != null && dir != null)
        {
            foreach (var robot in dir.GetAll())
            {
                if (paused)
                {
                    robotServer.SendMotorsOff(robot.RobotId);
                    robotServer.SendStreamOff(robot.RobotId);
                }
                else
                {
                    robotServer.SendMotorsOn(robot.RobotId);
                    robotServer.SendStreamOn(robot.RobotId);
                }
            }
        }

        string cmd = paused ? "game_paused" : "game_resumed";
        BroadcastRaw("{\"cmd\":\"" + cmd + "\"}");
        BroadcastDisplayUpdate();
        Debug.Log("[PlayerWS] " + cmd);
    }

    void OnPlayersChanged() => BroadcastPlayerList();

    void OnMatchTimerTick(float remaining)
    {
        if (Time.time - _lastCaptureTick < 5.0f) return;
        _lastCaptureTick = Time.time;
        ServiceLocator.CapturePoints?.Tick();
    }

    void OnCapturePointCaptured(int pointIndex, int allianceIndex, string pointName)
    {
        string playerName = FindPlayerForLastCapture(allianceIndex);
        string text = string.IsNullOrEmpty(playerName)
            ? $"Alliance {allianceIndex + 1} captured {pointName} Point!"
            : $"{playerName} captured {pointName} Point!";
        BroadcastDisplayEvent(text);
        BroadcastDisplayUpdate();
    }

    string FindPlayerForLastCapture(int allianceIndex)
    {
        var players = ServiceLocator.Players?.GetAll();
        if (players == null) return null;
        foreach (var p in players)
            if (p.AllianceIndex == allianceIndex) return p.Name;
        return null;
    }

    void OnTeamPointsChanged() => BroadcastDisplayUpdate();

    // ── State update helpers ──────────────────────────────────────────────────────

    void SendGameStartedToAll()
    {
        foreach (var kvp in _connToPlayer)
            SendGameStarted(kvp.Key, kvp.Value);
        SendHpToAllRobots();
    }

    void SendHpToAllRobots()
    {
        var server   = ServiceLocator.RobotServer;
        var dir      = ServiceLocator.RobotDirectory;
        var game     = ServiceLocator.Game;
        var settings = ServiceLocator.GameSettings;
        if (server == null || dir == null || game?.State == null) return;

        int maxHp = settings != null ? settings.MaxHp : 100;
        foreach (var robot in dir.GetAll())
        {
            int hp = game.State.RobotHp.GetValueOrDefault(robot.RobotId, maxHp);
            server.SendSetHp(robot.RobotId, hp, maxHp);
        }
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
        var state = ServiceLocator.Game?.State;
        string teamName = (state != null && state.WinnerAllianceIndex >= 0)
            ? "Alliance " + (state.WinnerAllianceIndex + 1)
            : "";
        string reason = state?.EndReason ?? "manual";

        string json = "{\"cmd\":\"game_over\",\"winnerTeam\":\"" +
                      EscapeJson(teamName) + "\",\"reason\":\"" +
                      EscapeJson(reason) + "\"}";
        BroadcastRaw(json);
        Debug.Log("[PlayerWS] Sent game_over reason=" + reason);
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

    // ── Display page broadcast ────────────────────────────────────────────────────

    public void BroadcastDisplayUpdate()
    {
        var sb         = new StringBuilder();
        var flow       = ServiceLocator.GameFlow;
        var gs         = ServiceLocator.Game?.State;
        var settings   = ServiceLocator.GameSettings;
        var players    = ServiceLocator.Players?.GetAll();
        var dir        = ServiceLocator.RobotDirectory;
        float timer    = ServiceLocator.MatchTimer?.Remaining ?? 0f;
        string phase   = flow?.Phase.ToString().ToLower() ?? "mainmenu";

        int maxPlayers   = settings?.MaxPlayers    ?? 6;
        int maxTeamPts   = settings?.MaxTeamPoints  ?? 300;
        int maxHp        = settings?.MaxHp          ?? 100;
        int playerCount  = players?.Count ?? 0;

        int tp0 = gs?.TeamPoints != null && gs.TeamPoints.Length > 0 ? gs.TeamPoints[0] : 0;
        int tp1 = gs?.TeamPoints != null && gs.TeamPoints.Length > 1 ? gs.TeamPoints[1] : 0;

        bool paused = flow?.IsPaused ?? false;

        sb.Append("{\"cmd\":\"display_update\"");
        sb.Append(",\"phase\":\"");   sb.Append(phase); sb.Append("\"");
        sb.Append(",\"paused\":");    sb.Append(paused ? "true" : "false");
        sb.Append(",\"timer\":");     sb.Append(timer.ToString("F1"));
        sb.Append(",\"playerCount\":"); sb.Append(playerCount);
        sb.Append(",\"maxPlayers\":"); sb.Append(maxPlayers);
        sb.Append(",\"teamPoints\":["); sb.Append(tp0); sb.Append(","); sb.Append(tp1); sb.Append("]");
        sb.Append(",\"maxTeamPoints\":"); sb.Append(maxTeamPts);

        // Robots array
        sb.Append(",\"robots\":[");
        if (gs?.Robots != null)
        {
            bool first = true;
            foreach (var r in gs.Robots)
            {
                if (!first) sb.Append(",");
                first = false;

                string callsign  = r.Callsign ?? r.RobotId;
                string playerName = r.AssignedPlayer ?? "";
                int    alliance  = -1;
                if (players != null)
                    foreach (var p in players)
                        if (p.Name == playerName) { alliance = p.AllianceIndex; break; }

                int hp = gs.RobotHp.TryGetValue(r.RobotId, out int v) ? v : maxHp;
                bool dead = gs.DeadRobots.Contains(r.RobotId);

                sb.Append("{\"callsign\":\""); sb.Append(EscapeJson(callsign)); sb.Append("\"");
                sb.Append(",\"player\":\"");   sb.Append(EscapeJson(playerName)); sb.Append("\"");
                sb.Append(",\"alliance\":"); sb.Append(alliance);
                sb.Append(",\"hp\":"); sb.Append(hp);
                sb.Append(",\"maxHp\":"); sb.Append(maxHp);
                sb.Append(",\"dead\":"); sb.Append(dead ? "true" : "false");
                sb.Append("}");
            }
        }
        sb.Append("]");

        // Capture points
        string[] cpNames = { "North", "Centre", "South" };
        sb.Append(",\"capturePoints\":[");
        for (int i = 0; i < 3; i++)
        {
            if (i > 0) sb.Append(",");
            int owner = gs?.CapturePointOwners != null && i < gs.CapturePointOwners.Length
                ? gs.CapturePointOwners[i] : -1;
            sb.Append("{\"name\":\""); sb.Append(cpNames[i]); sb.Append("\"");
            sb.Append(",\"owner\":"); sb.Append(owner);
            sb.Append("}");
        }
        sb.Append("]");

        sb.Append("}");
        BroadcastRaw(sb.ToString());
    }

    public void BroadcastDisplayEvent(string text)
    {
        string json = "{\"cmd\":\"display_event\",\"text\":\"" + EscapeJson(text) + "\"}";
        BroadcastRaw(json);
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

    private int GetRobotAllianceIndex(string robotId)
    {
        var dir     = ServiceLocator.RobotDirectory;
        var players = ServiceLocator.Players;
        if (dir == null || players == null) return -1;
        if (!dir.TryGet(robotId, out var info)) return -1;
        if (string.IsNullOrEmpty(info.AssignedPlayer)) return -1;
        foreach (var p in players.GetAll())
            if (p.Name == info.AssignedPlayer) return p.AllianceIndex;
        return -1;
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
