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
//   {"cmd":"game_over","winnerTeam":"Desert Squad","reason":"elimination"}

using System;
using System.Collections.Generic;
using System.Linq;
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

    // robotId → Time.time when the robot died (used to time the 5-s explosion → dead-walk transition)
    private readonly Dictionary<string, float> _deathTimes = new Dictionary<string, float>();

    // ── Announcer tracking ────────────────────────────────────────────────────────
    private bool _firstBloodDone;
    private float _gameStartTime;
    private readonly Dictionary<string, int> _playerKillStreak   = new Dictionary<string, int>();
    private readonly Dictionary<string, int> _playerKillTotal    = new Dictionary<string, int>();
    private readonly Dictionary<string, int> _playerCaptureScore = new Dictionary<string, int>();
    private readonly HashSet<string> _lowHpWarned = new HashSet<string>();

    // ── Per-player end-of-game stats ──────────────────────────────────────────────
    private readonly Dictionary<string, int>   _statKills          = new Dictionary<string, int>();
    private readonly Dictionary<string, int>   _statDeaths         = new Dictionary<string, int>();
    private readonly Dictionary<string, int>   _statDamage         = new Dictionary<string, int>();
    // target player name → (source player name → cumulative damage dealt to that target)
    private readonly Dictionary<string, Dictionary<string, int>> _statDamageFrom =
        new Dictionary<string, Dictionary<string, int>>();
    // VP earned by this player: float accumulator for capture ticks (split per CP held)
    private readonly Dictionary<string, float> _statVpFromCaptures = new Dictionary<string, float>();
    // VP earned by this player from kill bonuses
    private readonly Dictionary<string, int>   _statVpFromKills    = new Dictionary<string, int>();
    // Accumulated drive effort: sum of (Abs(l)+Abs(r)) per drive call during Playing
    private readonly Dictionary<string, float> _statGround         = new Dictionary<string, float>();
    private int[]  _prevTeamPoints   = { 0, 0 };
    private bool[] _teamPoints50Fired = { false, false };
    private bool[] _teamPoints90Fired = { false, false };

    // ── Player identification colours ─────────────────────────────────────────────
    private static readonly (string Name, byte R, byte G, byte B)[] PlayerColors =
    {
        ("RED",    255,   0,   0),
        ("GREEN",    0, 200,   0),
        ("BLUE",     0,   0, 255),
        ("YELLOW", 255, 200,   0),
        ("CYAN",     0, 220, 255),
        ("PINK",   255,   0, 180),
        ("ORANGE", 255,  70,   0),
        ("PURPLE",  50,   0, 255),
        ("LIME",    80, 255,   0),
        ("WHITE",  255, 255, 255),
    };
    private readonly Dictionary<string, int>    _robotColorIndex = new Dictionary<string, int>();
    private readonly Dictionary<string, string> _robotPrevPlayer = new Dictionary<string, string>();
    private int _nextColorIndex = 0;

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

        // Robot assignment changes (operator reassigning robots in the lobby)
        if (ServiceLocator.RobotDirectory != null)
        {
            ServiceLocator.RobotDirectory.OnRobotUpdated += OnRobotUpdated;
            ServiceLocator.RobotDirectory.OnRobotAdded   += OnRobotUpdated;
            ServiceLocator.RobotDirectory.OnRobotRemoved += OnRobotRemoved;
        }

        // Game phase changes
        if (ServiceLocator.GameFlow != null)
        {
            ServiceLocator.GameFlow.OnPhaseChanged  += OnPhaseChanged;
            ServiceLocator.GameFlow.OnPausedChanged += OnPausedChanged;
        }

        // HP / death / respawn / win events
        if (ServiceLocator.Game != null)
        {
            ServiceLocator.Game.OnHpChanged          += OnHpChanged;
            ServiceLocator.Game.OnRobotDied          += OnRobotDied;
            ServiceLocator.Game.OnRobotRespawned         += OnRobotRespawned;
            ServiceLocator.Game.OnGameWon                += OnGameWon;
            ServiceLocator.Game.OnInvulnerabilityGranted += OnInvulnerabilityGranted;
            ServiceLocator.Game.OnInvulnerabilityEnded   += OnInvulnerabilityEnded;
            ServiceLocator.Game.OnRobotHitDirection  += OnHitDirection;
            ServiceLocator.Game.OnRobotKilled        += OnRobotKilled;
            ServiceLocator.Game.OnDamageDealt        += OnDamageDealt;
        }

        // RFID tag scans
        if (ServiceLocator.RobotServer != null)
            ServiceLocator.RobotServer.OnRfidTag += OnRfidTag;

        // Capture points & match timer tick
        if (ServiceLocator.CapturePoints != null)
        {
            ServiceLocator.CapturePoints.OnPointCaptured     += OnCapturePointCaptured;
            ServiceLocator.CapturePoints.OnTeamPointsChanged += OnTeamPointsChanged;
            ServiceLocator.CapturePoints.OnCaptureVpAwarded  += OnCaptureVpAwardedStat;
        }
    }

    private void OnDestroy()
    {
        if (ServiceLocator.Players != null)
            ServiceLocator.Players.OnChanged -= OnPlayersChanged;

        if (ServiceLocator.RobotDirectory != null)
        {
            ServiceLocator.RobotDirectory.OnRobotUpdated -= OnRobotUpdated;
            ServiceLocator.RobotDirectory.OnRobotAdded   -= OnRobotUpdated;
            ServiceLocator.RobotDirectory.OnRobotRemoved -= OnRobotRemoved;
        }

        if (ServiceLocator.GameFlow != null)
        {
            ServiceLocator.GameFlow.OnPhaseChanged  -= OnPhaseChanged;
            ServiceLocator.GameFlow.OnPausedChanged -= OnPausedChanged;
        }

        if (ServiceLocator.Game != null)
        {
            ServiceLocator.Game.OnHpChanged         -= OnHpChanged;
            ServiceLocator.Game.OnRobotDied         -= OnRobotDied;
            ServiceLocator.Game.OnRobotRespawned         -= OnRobotRespawned;
            ServiceLocator.Game.OnGameWon                -= OnGameWon;
            ServiceLocator.Game.OnInvulnerabilityGranted -= OnInvulnerabilityGranted;
            ServiceLocator.Game.OnInvulnerabilityEnded   -= OnInvulnerabilityEnded;
            ServiceLocator.Game.OnRobotHitDirection -= OnHitDirection;
            ServiceLocator.Game.OnRobotKilled       -= OnRobotKilled;
            ServiceLocator.Game.OnDamageDealt       -= OnDamageDealt;
        }

        if (ServiceLocator.RobotServer != null)
            ServiceLocator.RobotServer.OnRfidTag -= OnRfidTag;

        if (ServiceLocator.CapturePoints != null)
        {
            ServiceLocator.CapturePoints.OnPointCaptured    -= OnCapturePointCaptured;
            ServiceLocator.CapturePoints.OnTeamPointsChanged -= OnTeamPointsChanged;
            ServiceLocator.CapturePoints.OnCaptureVpAwarded -= OnCaptureVpAwardedStat;
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
        CheckDeathTransitions();

        // Award capture-point victory points and expire invulnerabilities every frame
        if (ServiceLocator.GameFlow?.Phase == GamePhase.Playing)
        {
            ServiceLocator.CapturePoints?.Tick(Time.deltaTime);
            ServiceLocator.Game?.TickInvulnerability(Time.time);
        }

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
        BroadcastRobotList();
        // Tell the newly-connected bridge what phase we're in so it can gate player joins.
        BroadcastPhase(ServiceLocator.GameFlow?.Phase ?? GamePhase.MainMenu);
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
        BroadcastRobotList();
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

            case "squad":
                HandleSquad(msg.connectionId, msg.alliance);
                break;

            case "pick_robot":
                HandlePickRobot(msg.connectionId, msg.robotId ?? "");
                break;

            case "leave":
                HandleLeave(msg.connectionId);
                if (_sessionToConns.TryGetValue(sessionId, out var s)) s.Remove(msg.connectionId);
                BroadcastPlayerList();
                BroadcastRobotList();
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

            case "set_two_player":
                HandleSetTwoPlayer(msg.connectionId, msg.enabled);
                break;

            case "join_as_gunner":
                HandleJoinAsGunner(msg.connectionId, msg.robotId ?? "");
                break;

            case "leave_gunner":
                HandleLeaveGunner(msg.connectionId);
                break;

            case "swap_roles":
                HandleSwapRoles(msg.connectionId);
                break;
        }
    }

    // ── Lobby handlers ───────────────────────────────────────────────────────────

    void HandleJoin(string sessionId, string connId, string name)
    {
        if (string.IsNullOrWhiteSpace(connId) || string.IsNullOrWhiteSpace(name)) return;
        if (_connToPlayer.ContainsKey(connId)) return;

        // Check if name already exists
        bool alreadyListed = false;
        var existingList = ServiceLocator.Players?.GetAll();
        if (existingList != null)
            foreach (var p in existingList)
                if (p.Name == name) { alreadyListed = true; break; }

        // Reject duplicate names in Lobby. During Playing, same name = reconnect — allow it.
        if (alreadyListed && ServiceLocator.GameFlow?.Phase == GamePhase.Lobby)
        {
            string reason = EscapeJson("\"" + name + "\" is already taken — please choose a different name.");
            BroadcastRaw("{\"cmd\":\"join_rejected\",\"connectionId\":\"" + EscapeJson(connId) + "\",\"reason\":\"" + reason + "\"}");
            Debug.Log("[PlayerWS] Rejected duplicate name: " + name + " (conn=" + connId + ")");
            return;
        }

        // During Playing, only allow reconnects for players already in the game.
        // New joins (including players who were kicked for not having a tank) are rejected.
        if (ServiceLocator.GameFlow?.Phase == GamePhase.Playing && !alreadyListed)
        {
            string reason = EscapeJson("Game is in progress. Please wait for the next game.");
            BroadcastRaw("{\"cmd\":\"join_rejected\",\"connectionId\":\"" + EscapeJson(connId) + "\",\"reason\":\"" + reason + "\"}");
            Debug.Log("[PlayerWS] Rejected new join during Playing: " + name);
            return;
        }

        _connToPlayer[connId] = name;
        if (_sessionToConns.TryGetValue(sessionId, out var conns)) conns.Add(connId);

        if (!alreadyListed)
            ServiceLocator.Players?.AddPlayer(name, -1); // -1 = unassigned until squad chosen

        Debug.Log("[PlayerWS] Player joined: " + name + " (conn=" + connId + ")");

        // If the game is already running, send game_started immediately so the
        // phone transitions to the playing screen without waiting for the next phase change.
        if (ServiceLocator.GameFlow?.Phase == GamePhase.Playing)
            SendGameStarted(connId, name);

        BroadcastPlayerList();
        BroadcastRobotList();
    }

    /// <summary>
    /// Sends every player who has no robot assignment back to the join screen,
    /// then removes them from active state. Called just before StartGame().
    /// </summary>
    public void KickUnassignedPlayers()
    {
        var dir     = ServiceLocator.RobotDirectory;
        var players = ServiceLocator.Players;
        if (dir == null || players == null) return;

        // Build the set of player names that DO have a robot (as driver or gunner)
        var assignedPlayers = new HashSet<string>();
        foreach (var robot in dir.GetAll())
        {
            if (!string.IsNullOrEmpty(robot.AssignedPlayer)) assignedPlayers.Add(robot.AssignedPlayer);
            if (!string.IsNullOrEmpty(robot.GunnerPlayer))   assignedPlayers.Add(robot.GunnerPlayer);
        }

        // Collect names of players without a robot
        var toKick = new List<string>();
        foreach (var p in players.GetAll())
            if (!assignedPlayers.Contains(p.Name))
                toKick.Add(p.Name);

        if (toKick.Count == 0) return;

        string kickReason = EscapeJson("Game is starting — you are not assigned to a tank. Please wait for the next game.");

        foreach (string playerName in toKick)
        {
            // Kick all active connections for this player
            var connsToRemove = new List<string>();
            foreach (var kvp in _connToPlayer)
                if (kvp.Value == playerName)
                    connsToRemove.Add(kvp.Key);

            foreach (string connId in connsToRemove)
            {
                BroadcastRaw("{\"cmd\":\"join_rejected\"" +
                             ",\"connectionId\":\"" + EscapeJson(connId) + "\"" +
                             ",\"reason\":\""        + kickReason         + "\"}");
                _connToPlayer.Remove(connId);
            }

            // Remove from PlayersService (find index fresh each iteration as list shifts)
            var pl = players.GetAll();
            for (int i = 0; i < pl.Count; i++)
            {
                if (pl[i].Name == playerName)
                {
                    players.RemovePlayerAt(i);
                    break;
                }
            }

            Debug.Log("[PlayerWS] Kicked unassigned player: " + playerName);
        }

        BroadcastPlayerList();
        BroadcastRobotList();
    }

    /// <summary>
    /// Sends unassigned players back to the join screen without removing them from state.
    /// Called after StartGame() to clean up any remaining players with no robot (non-kick path).
    /// </summary>
    public void RedirectUnassignedPlayers()
    {
        var dir     = ServiceLocator.RobotDirectory;
        var players = ServiceLocator.Players;
        if (dir == null || players == null) return;

        var assigned = new HashSet<string>();
        foreach (var r in dir.GetAll())
        {
            if (!string.IsNullOrEmpty(r.AssignedPlayer)) assigned.Add(r.AssignedPlayer);
            if (!string.IsNullOrEmpty(r.GunnerPlayer))   assigned.Add(r.GunnerPlayer);
        }

        string reason = EscapeJson("Game is starting — you are not assigned to a tank. Please wait for the next game.");

        foreach (var kvp in _connToPlayer.ToList())
        {
            string connId     = kvp.Key;
            string playerName = kvp.Value;
            if (assigned.Contains(playerName)) continue;

            BroadcastRaw("{\"cmd\":\"join_rejected\"" +
                         ",\"connectionId\":\"" + EscapeJson(connId) + "\"" +
                         ",\"reason\":\""        + reason             + "\"}");
            Debug.Log("[PlayerWS] Redirected unassigned player: " + playerName);
        }
    }

    /// <summary>
    /// Resets player and robot assignment state for a fresh lobby session.
    /// Sends all currently-connected phones back to the join screen, then clears
    /// the player list and all robot assignments.
    /// </summary>
    void ResetForNewGame()
    {
        string reason = EscapeJson("A new lobby has opened. Please rejoin to play.");
        foreach (var connId in _connToPlayer.Keys.ToList())
        {
            BroadcastRaw("{\"cmd\":\"join_rejected\"" +
                         ",\"connectionId\":\"" + EscapeJson(connId) + "\"" +
                         ",\"reason\":\""        + reason             + "\"}");
        }
        _connToPlayer.Clear();

        ServiceLocator.Players?.ClearAll();
        ServiceLocator.RobotDirectory?.ClearAllAssignedPlayers();

        Debug.Log("[PlayerWS] Reset for new game: players and robot assignments cleared.");
    }

    // Find the first unassigned robot in the player's chosen squad and give it to playerName.
    // Falls back to any unassigned robot if no squad match exists.
    void TryAssignFreeRobotToPlayer(string playerName, int alliance = -1)
    {
        var dir = ServiceLocator.RobotDirectory;
        if (dir == null) return;

        var all = dir.GetAll();

        // First pass: prefer a robot whose PreferredAlliance matches the player's squad.
        if (alliance == 0 || alliance == 1)
        {
            foreach (var robot in all)
            {
                if (string.IsNullOrEmpty(robot.AssignedPlayer) && robot.PreferredAlliance == alliance)
                {
                    dir.SetAssignedPlayer(robot.RobotId, playerName);
                    Debug.Log("[PlayerWS] Auto-assigned squad robot " + robot.RobotId + " (alliance=" + alliance + ") to " + playerName);
                    return;
                }
            }
        }

        // Second pass: fall back to any free robot.
        foreach (var robot in all)
        {
            if (string.IsNullOrEmpty(robot.AssignedPlayer))
            {
                dir.SetAssignedPlayer(robot.RobotId, playerName);
                Debug.Log("[PlayerWS] Auto-assigned robot " + robot.RobotId + " (fallback) to " + playerName);
                return;
            }
        }
    }

    void HandleSquad(string connId, int alliance)
    {
        if (!_connToPlayer.TryGetValue(connId, out string playerName)) return;
        if (alliance != -1 && alliance != 0 && alliance != 1) return;

        // Update the player's alliance (-1 = unassigned)
        ServiceLocator.Players?.SetAllianceByName(playerName, alliance);

        // Always clear the current robot assignment first
        var dir = ServiceLocator.RobotDirectory;
        if (dir != null)
        {
            foreach (var robot in dir.GetAll())
                if (robot.AssignedPlayer == playerName)
                    dir.ClearAssignedPlayer(robot.RobotId);

            // Only re-assign if they picked a squad
            if (alliance == 0 || alliance == 1)
                TryAssignFreeRobotToPlayer(playerName, alliance);
        }

        BroadcastPlayerList();
        string action = alliance >= 0 ? "joined squad " + AllianceName(alliance) : "left squad";
        Debug.Log("[PlayerWS] Player " + playerName + " " + action);
    }

    void HandlePickRobot(string connId, string robotId)
    {
        if (!_connToPlayer.TryGetValue(connId, out string playerName)) return;

        var dir = ServiceLocator.RobotDirectory;
        if (dir == null) return;

        // Clear current robot assignment for this player
        foreach (var robot in dir.GetAll())
            if (robot.AssignedPlayer == playerName)
                dir.ClearAssignedPlayer(robot.RobotId);

        if (!string.IsNullOrEmpty(robotId))
        {
            // Assign specific robot; derive alliance from its PreferredAlliance
            if (dir.TryGet(robotId, out var robotInfo))
            {
                dir.SetAssignedPlayer(robotId, playerName);
                int alliance = robotInfo.PreferredAlliance;
                ServiceLocator.Players?.SetAllianceByName(playerName, alliance >= 0 ? alliance : -1);
                Debug.Log("[PlayerWS] " + playerName + " picked robot " + robotId + " (alliance=" + alliance + ")");
            }
        }
        else
        {
            // Deselected — go unassigned
            ServiceLocator.Players?.SetAllianceByName(playerName, -1);
            Debug.Log("[PlayerWS] " + playerName + " deselected robot");
        }

        BroadcastPlayerList();
        BroadcastRobotList();
    }

    void HandleSetTwoPlayer(string connId, bool enabled)
    {
        if (!_connToPlayer.TryGetValue(connId, out string playerName)) return;
        var dir = ServiceLocator.RobotDirectory;
        if (dir == null) return;

        // Only the assigned driver can toggle 2-player mode
        string robotId = PlayerToRobot(playerName);
        if (robotId == null) return;

        dir.SetTwoPlayerEnabled(robotId, enabled);
        Debug.Log($"[PlayerWS] {playerName} set TwoPlayerEnabled={enabled} on {robotId}");
        BroadcastRobotList();
    }

    void HandleJoinAsGunner(string connId, string robotId)
    {
        if (!_connToPlayer.TryGetValue(connId, out string playerName)) return;
        if (string.IsNullOrEmpty(robotId)) return;

        var settings = ServiceLocator.GameSettings;
        if (settings == null || !settings.TwoPlayerModeEnabled)
        {
            Debug.Log("[PlayerWS] join_as_gunner rejected — TwoPlayerModeEnabled is off");
            return;
        }

        var dir = ServiceLocator.RobotDirectory;
        if (dir == null || !dir.TryGet(robotId, out var robotInfo)) return;

        if (!robotInfo.TwoPlayerEnabled)
        {
            Debug.Log($"[PlayerWS] join_as_gunner rejected — robot {robotId} has not enabled 2-player");
            return;
        }
        if (!string.IsNullOrEmpty(robotInfo.GunnerPlayer))
        {
            Debug.Log($"[PlayerWS] join_as_gunner rejected — gunner slot already taken by {robotInfo.GunnerPlayer}");
            return;
        }
        if (robotInfo.AssignedPlayer == playerName)
        {
            Debug.Log($"[PlayerWS] join_as_gunner rejected — {playerName} is already the driver");
            return;
        }
        // Player cannot be gunner for a different robot if they're already a driver or gunner somewhere
        if (PlayerToRobot(playerName) != null || GunnerToRobot(playerName) != null)
        {
            Debug.Log($"[PlayerWS] join_as_gunner rejected — {playerName} is already assigned to a robot");
            return;
        }

        dir.SetGunnerPlayer(robotId, playerName);

        // Send tank colour to gunner if robot already has one (driver got it on assignment)
        if (_robotColorIndex.TryGetValue(robotId, out int colorIdx))
        {
            var (colorName, cr, cg, cb) = PlayerColors[colorIdx];
            string gunnerConn = FindConnForPlayer(playerName);
            if (!string.IsNullOrEmpty(gunnerConn))
                BroadcastRaw("{\"cmd\":\"tank_color\",\"connectionId\":\"" +
                             EscapeJson(gunnerConn) + "\",\"colorName\":\"" +
                             EscapeJson(colorName) + "\"}");
        }

        // Add to PlayersService with the robot's alliance so they appear in the player list
        int alliance = robotInfo.PreferredAlliance;
        if (!ServiceLocator.Players?.GetAll().Any(p => p.Name == playerName) ?? false)
            ServiceLocator.Players?.AddPlayer(playerName, alliance >= 0 ? alliance : -1);
        else
            ServiceLocator.Players?.SetAllianceByName(playerName, alliance >= 0 ? alliance : -1);

        Debug.Log($"[PlayerWS] {playerName} joined as gunner on {robotId}");
        BroadcastPlayerList();
        BroadcastRobotList();
    }

    void HandleLeaveGunner(string connId)
    {
        if (!_connToPlayer.TryGetValue(connId, out string playerName)) return;

        var dir = ServiceLocator.RobotDirectory;
        if (dir == null) return;

        string robotId = GunnerToRobot(playerName);
        if (robotId == null) return;

        dir.ClearGunnerPlayer(robotId);

        // Remove from PlayersService during lobby (gunner has no robot now)
        if (ServiceLocator.GameFlow?.Phase != GamePhase.Playing)
        {
            var players = ServiceLocator.Players?.GetAll();
            if (players != null)
                for (int i = 0; i < players.Count; i++)
                    if (players[i].Name == playerName)
                    { ServiceLocator.Players.RemovePlayerAt(i); break; }
        }

        Debug.Log($"[PlayerWS] {playerName} left gunner slot on {robotId}");
        BroadcastPlayerList();
        BroadcastRobotList();
    }

    void HandleSwapRoles(string connId)
    {
        if (!_connToPlayer.TryGetValue(connId, out string playerName)) return;

        var dir = ServiceLocator.RobotDirectory;
        if (dir == null) return;

        string robotId = PlayerToRobot(playerName) ?? GunnerToRobot(playerName);
        if (robotId == null || !dir.TryGet(robotId, out var info)) return;

        if (string.IsNullOrEmpty(info.GunnerPlayer) || string.IsNullOrEmpty(info.AssignedPlayer))
        {
            Debug.Log("[PlayerWS] swap_roles rejected — need both driver and gunner assigned");
            return;
        }

        string oldDriver = info.AssignedPlayer;
        string oldGunner = info.GunnerPlayer;

        // Clear both slots so PickAssignedPlayer sees neither as taken
        dir.ClearAssignedPlayer(robotId);
        dir.ClearGunnerPlayer(robotId);
        // Re-assign swapped: old gunner becomes driver, old driver becomes gunner
        dir.SetAssignedPlayer(robotId, oldGunner);
        dir.SetGunnerPlayer(robotId, oldDriver);

        Debug.Log($"[PlayerWS] Roles swapped on {robotId}: driver={oldGunner}, gunner={oldDriver}");
        BroadcastPlayerList();
        BroadcastRobotList();
    }

    void HandleLeave(string connId)
    {
        if (!_connToPlayer.TryGetValue(connId, out string playerName)) return;
        _connToPlayer.Remove(connId);

        // Player already rejoined with a new connection — don't evict them.
        foreach (var v in _connToPlayer.Values)
            if (v == playerName) return;

        // During active gameplay keep the player in PlayersService so alliance
        // lookups in IrSlotScheduler stay valid for their (still-connected) robot.
        // SignalR's onreconnected handler will call JoinLobby again automatically.
        if (ServiceLocator.GameFlow?.Phase == GamePhase.Playing) return;

        // Release any robot this player had claimed (as driver or gunner) so it becomes available again.
        var dir = ServiceLocator.RobotDirectory;
        if (dir != null)
        {
            foreach (var robot in dir.GetAll())
            {
                if (robot.AssignedPlayer == playerName)
                    dir.ClearAssignedPlayer(robot.RobotId);
                if (robot.GunnerPlayer == playerName)
                    dir.ClearGunnerPlayer(robot.RobotId);
            }
        }

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
        if (ServiceLocator.GameFlow?.Phase != GamePhase.Playing) return;

        string robotId = ConnToRobot(connId);
        if (robotId == null) return;

        // Block all movement during the 5-s explosion phase
        var state = ServiceLocator.Game?.State;
        if (state != null && state.DeadRobots.Contains(robotId)) return;

        ServiceLocator.RobotServer?.SendDrive(robotId, l, r);

        string playerName = _connToPlayer.TryGetValue(connId, out var n) ? n : null;
        if (playerName != null)
        {
            OnPlayerInput?.Invoke(playerName, l, r, GetLastTurret(playerName));
            _statGround[playerName] = _statGround.GetValueOrDefault(playerName) + Mathf.Abs(l) + Mathf.Abs(r);
        }

        _lastTurret[connId] = GetLastTurret(_connToPlayer.TryGetValue(connId, out var nm) ? nm : "");
    }

    // Returns the robot a player may use for turret/fire: their gunner robot, or their
    // driver robot only if no gunner is assigned (solo mode). Returns null if denied.
    string ConnToGunRobot(string connId)
    {
        if (!_connToPlayer.TryGetValue(connId, out string playerName)) return null;

        // Gunner always gets gun controls
        string robotId = GunnerToRobot(playerName);
        if (robotId != null) return robotId;

        // Solo driver: AssignedPlayer with no gunner assigned
        string driverRobot = PlayerToRobot(playerName);
        if (driverRobot == null) return null;
        var dir = ServiceLocator.RobotDirectory;
        if (dir != null && dir.TryGet(driverRobot, out var info) && !string.IsNullOrEmpty(info.GunnerPlayer))
            return null; // driver of a two-player tank — no gun access
        return driverRobot;
    }

    void HandleTurret(string connId, float speed)
    {
        if (ServiceLocator.GameFlow?.Phase != GamePhase.Playing) return;

        string robotId = ConnToGunRobot(connId);
        if (robotId == null) return;

        // Block during explosion phase
        var state = ServiceLocator.Game?.State;
        if (state != null && state.DeadRobots.Contains(robotId)) return;

        var gs = ServiceLocator.GameSettings;
        if (gs != null && gs.SlowTurretEnabled)
            speed = Mathf.Clamp(speed, -gs.SlowTurretSpeed, gs.SlowTurretSpeed);
        ServiceLocator.RobotServer?.SendTurret(robotId, speed);
        _lastTurretByConn[connId] = speed;

        string playerName = _connToPlayer.TryGetValue(connId, out var n) ? n : null;
        if (playerName != null)
            OnPlayerInput?.Invoke(playerName, 0f, 0f, speed);
    }

    void HandleFire(string connId)
    {
        if (ServiceLocator.GameFlow?.Phase != GamePhase.Playing) return;

        string robotId = ConnToGunRobot(connId);
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
        BroadcastPhase(phase);
        if (phase == GamePhase.Playing)
        {
            // Clear player colour pulses on all robots — game_start_fanfare also does this
            // on the firmware side, but belt-and-suspenders for any missed fanfare.
            var dir = ServiceLocator.RobotDirectory;
            if (dir != null)
                foreach (var r in dir.GetAll())
                    ServiceLocator.RobotServer?.SendClearPlayerColor(r.RobotId);
            _robotPrevPlayer.Clear(); // allow re-assignment of colours next lobby
            _robotColorIndex.Clear(); // deterministic colour assignment each match
            _nextColorIndex = 0;

            SendGameStartedToAll();
            ActivateAllRobots();
            _gameStartTime = Time.time;
            _firstBloodDone = false;
            _playerKillStreak.Clear();
            _playerKillTotal.Clear();
            _playerCaptureScore.Clear();
            _lowHpWarned.Clear();
            _statKills.Clear();
            _statDeaths.Clear();
            _statDamage.Clear();
            _statDamageFrom.Clear();
            _statVpFromCaptures.Clear();
            _statVpFromKills.Clear();
            _statGround.Clear();
            System.Array.Clear(_prevTeamPoints,    0, _prevTeamPoints.Length);
            System.Array.Clear(_teamPoints50Fired, 0, _teamPoints50Fired.Length);
            System.Array.Clear(_teamPoints90Fired, 0, _teamPoints90Fired.Length);
        }
        else if (phase == GamePhase.Ended)
        {
            ServiceLocator.Game?.ClearAllInvulnerabilities();
            SendGameOver();
            DeactivateAllRobots();
            ServiceLocator.RobotServer?.BroadcastResetIdleToAll();
            BroadcastDisplayUpdate();
        }
        else if (phase == GamePhase.Lobby)
        {
            // Clear stale death timers from the previous game so they don't fire
            // transitions on the new GameState when a fresh game starts.
            _deathTimes.Clear();
            // Return all robots to the white-bounce idle animation.
            ServiceLocator.RobotServer?.BroadcastResetIdleToAll();
            // Reset player and assignment state for a fresh game session.
            ResetForNewGame();
        }
    }

    void BroadcastPhase(GamePhase phase)
    {
        string phaseStr = phase.ToString().ToLower();
        BroadcastRaw("{\"cmd\":\"phase_changed\",\"phase\":\"" + phaseStr + "\"}");
    }

    void ActivateAllRobots()
    {
        var dir = ServiceLocator.RobotDirectory;
        var ws  = ServiceLocator.RobotServer;
        if (dir == null || ws == null) return;
        foreach (var robot in dir.GetAll())
        {
            if (string.IsNullOrEmpty(robot.AssignedPlayer)) continue;
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
        // Find all connections whose player is on this robot (driver or gunner)
        foreach (var kvp in _connToPlayer)
        {
            string connId    = kvp.Key;
            string playerName = kvp.Value;
            string rId       = PlayerToRobotAny(playerName);
            if (rId != robotId) continue;

            SendSingleStateUpdate(connId);
        }

        // Critical HP warning — once per robot per life, at ≤25%
        int maxHp = ServiceLocator.GameSettings?.MaxHp ?? 100;
        if (newHp > 0 && newHp <= maxHp / 4 && !_lowHpWarned.Contains(robotId))
        {
            _lowHpWarned.Add(robotId);
            string warnName = GetCrewNamesForRobot(robotId);
            if (!string.IsNullOrEmpty(warnName))
                BroadcastDisplayEvent($"Warning — {warnName}'s tank is critical!");
        }
    }

    void OnHitDirection(string targetId, string shooterId, byte rawMask, string cardinalDir)
    {
        // Resolve shooter's crew name (both players if two-player tank)
        string shooterName = GetCrewNamesForRobot(shooterId);

        // Send hit_taken to all connections on the target robot (driver and gunner)
        foreach (var kvp in _connToPlayer)
        {
            if (PlayerToRobotAny(kvp.Value) != targetId) continue;

            string json = "{\"cmd\":\"hit_taken\"" +
                          ",\"connectionId\":\"" + EscapeJson(kvp.Key) + "\"" +
                          ",\"shooter\":\"" + EscapeJson(shooterName) + "\"" +
                          ",\"dir\":\"" + EscapeJson(cardinalDir ?? "") + "\"}";
            BroadcastRaw(json);
        }

        // Announce rear hits
        if (cardinalDir == "S" && !string.IsNullOrEmpty(shooterName))
        {
            string targetCrew = GetCrewNamesForRobot(targetId);
            string rearMsg = string.IsNullOrEmpty(targetCrew)
                ? $"Rear hit! {shooterName} attacks from behind!"
                : $"Rear hit! {shooterName} hits {targetCrew} from behind!";
            BroadcastDisplayEvent(rearMsg);
        }
    }

    void OnRobotDied(string robotId)
    {
        // Track deaths for every crew member on this robot
        var dDir = ServiceLocator.RobotDirectory;
        if (dDir != null && dDir.TryGet(robotId, out var dInfo))
        {
            if (!string.IsNullOrEmpty(dInfo.AssignedPlayer))
                _statDeaths[dInfo.AssignedPlayer] = _statDeaths.GetValueOrDefault(dInfo.AssignedPlayer) + 1;
            if (!string.IsNullOrEmpty(dInfo.GunnerPlayer))
                _statDeaths[dInfo.GunnerPlayer] = _statDeaths.GetValueOrDefault(dInfo.GunnerPlayer) + 1;
        }

        // Record when the robot died so Update() can transition it to dead walk after 5 s
        _deathTimes[robotId] = Time.time;

        // Disable motors and trigger death explosion on the robot
        var robotServer = ServiceLocator.RobotServer;
        robotServer?.SendMotorsOff(robotId);
        robotServer?.SendFlashDeath(robotId);

        // Notify all players on this robot's phone (driver and gunner)
        foreach (var kvp in _connToPlayer)
        {
            string rId = PlayerToRobotAny(kvp.Value);
            if (rId != robotId) continue;

            string json = "{\"cmd\":\"you_are_dead\",\"connectionId\":\"" +
                          EscapeJson(kvp.Key) + "\"}";
            BroadcastRaw(json);
            Debug.Log("[PlayerWS] Sent you_are_dead to " + kvp.Value);
        }
    }

    void OnRobotKilled(string shooterId, string targetId)
    {
        string targetName  = GetCrewNamesForRobot(targetId);
        string shooterName = string.IsNullOrEmpty(shooterId) ? "" : GetCrewNamesForRobot(shooterId);

        // Reset target's kill streak on death
        if (!string.IsNullOrEmpty(targetName))
            _playerKillStreak[targetName] = 0;

        string text;
        if (string.IsNullOrEmpty(shooterName))
        {
            text = string.IsNullOrEmpty(targetName) ? "A tank was destroyed!" : $"{targetName} was destroyed!";
        }
        else
        {
            if (!_playerKillTotal.ContainsKey(shooterName))    _playerKillTotal[shooterName]    = 0;
            if (!_playerKillStreak.ContainsKey(shooterName)) _playerKillStreak[shooterName] = 0;
            _playerKillTotal[shooterName]++;
            _playerKillStreak[shooterName]++;
            int streak = _playerKillStreak[shooterName];

            string victim = string.IsNullOrEmpty(targetName) ? "an enemy" : targetName;

            if (!_firstBloodDone)
            {
                _firstBloodDone = true;
                text = $"First blood — {shooterName} eliminated {victim}!";
            }
            else if (streak == 2)
            {
                text = $"Double kill — {shooterName} eliminated {victim}!";
            }
            else if (streak == 3)
            {
                text = $"Triple kill — {shooterName} is on fire!";
            }
            else if (streak >= 4)
            {
                text = $"{shooterName} is unstoppable! Eliminated {victim}!";
            }
            else if (Time.time - _gameStartTime < 60f)
            {
                text = $"An early casualty — {shooterName} eliminated {victim}!";
            }
            else
            {
                text = $"{shooterName} eliminated {victim}!";
            }
        }

        BroadcastDisplayEvent(text);

        // Track individual kill stat — credit goes to gunner (they fire), or driver if solo
        if (!string.IsNullOrEmpty(shooterId))
        {
            var kDir = ServiceLocator.RobotDirectory;
            if (kDir != null && kDir.TryGet(shooterId, out var kInfo))
            {
                string credit = !string.IsNullOrEmpty(kInfo.GunnerPlayer)
                    ? kInfo.GunnerPlayer : kInfo.AssignedPlayer ?? "";
                if (!string.IsNullOrEmpty(credit))
                {
                    _statKills[credit] = _statKills.GetValueOrDefault(credit) + 1;
                    int killVp = ServiceLocator.GameSettings?.TeamPointsPerKill ?? 0;
                    if (killVp > 0)
                        _statVpFromKills[credit] = _statVpFromKills.GetValueOrDefault(credit) + killVp;
                }
            }
        }

        // Signal display page to play kill burn animation on bar + HP row
        int killerAlliance = string.IsNullOrEmpty(shooterId) ? -1 : GetRobotAllianceIndex(shooterId);
        string tCallsign   = "";
        var rdir = ServiceLocator.RobotDirectory;
        if (rdir != null && rdir.TryGet(targetId, out var tInfo))
            tCallsign = string.IsNullOrEmpty(tInfo.Callsign) ? targetId : tInfo.Callsign;
        int killPts = ServiceLocator.GameSettings?.TeamPointsPerKill ?? 0;
        BroadcastRaw("{\"cmd\":\"kill_event\"" +
            ",\"teamIndex\":" + killerAlliance +
            ",\"targetCallsign\":\"" + EscapeJson(tCallsign) + "\"" +
            ",\"points\":" + killPts + "}");
    }

    void OnDamageDealt(string shooterRobotId, string targetRobotId, int damage)
    {
        var dir = ServiceLocator.RobotDirectory;
        if (dir == null) return;

        // Shooter credit: gunner fires shots; fall back to driver in solo mode
        string shooterName = "";
        if (!string.IsNullOrEmpty(shooterRobotId) && dir.TryGet(shooterRobotId, out var sInfo))
            shooterName = !string.IsNullOrEmpty(sInfo.GunnerPlayer)
                ? sInfo.GunnerPlayer : sInfo.AssignedPlayer ?? "";

        // Target: the assigned driver (for nemesis lookup — someone has to own the hull)
        string targetName = "";
        if (!string.IsNullOrEmpty(targetRobotId) && dir.TryGet(targetRobotId, out var tInfo))
            targetName = tInfo.AssignedPlayer ?? "";

        if (string.IsNullOrEmpty(shooterName)) return;

        _statDamage[shooterName] = _statDamage.GetValueOrDefault(shooterName) + damage;

        if (!string.IsNullOrEmpty(targetName))
        {
            if (!_statDamageFrom.ContainsKey(targetName))
                _statDamageFrom[targetName] = new Dictionary<string, int>();
            var from = _statDamageFrom[targetName];
            from[shooterName] = from.GetValueOrDefault(shooterName) + damage;
        }
    }

    void OnCaptureVpAwardedStat(string playerName, float points)
    {
        _statVpFromCaptures[playerName] = _statVpFromCaptures.GetValueOrDefault(playerName) + points;
    }

    string GetNemesisFor(string playerName)
    {
        if (!_statDamageFrom.TryGetValue(playerName, out var from) || from.Count == 0) return "";
        string nemesis = null;
        int maxDmg = 0;
        foreach (var kv in from)
            if (kv.Value > maxDmg) { maxDmg = kv.Value; nemesis = kv.Key; }
        return nemesis ?? "";
    }

    /// <summary>
    /// Called by RobotWebSocketServer when a robot sends 'hello' during Playing.
    /// Sends game_started to the assigned phone player so their video stream
    /// reconnects to the robot's (possibly updated) IP address.
    /// Skipped for robots currently in the explosion/dead-walk phase to avoid
    /// clearing the dead overlay prematurely.
    /// </summary>
    public void NotifyRobotRejoined(string robotId)
    {
        if (ServiceLocator.GameFlow?.Phase != GamePhase.Playing) return;

        var state = ServiceLocator.Game?.State;
        if (state != null && (state.DeadRobots.Contains(robotId) || state.RespawningRobots.Contains(robotId)))
            return;

        var dir = ServiceLocator.RobotDirectory;
        if (dir == null || !dir.TryGet(robotId, out var robotInfo)) return;
        if (string.IsNullOrEmpty(robotInfo.AssignedPlayer)) return;

        foreach (var kvp in _connToPlayer)
        {
            if (kvp.Value == robotInfo.AssignedPlayer || kvp.Value == robotInfo.GunnerPlayer)
            {
                SendGameStarted(kvp.Key, kvp.Value);
                Debug.Log($"[PlayerWS] Robot {robotId} rejoined → re-sent game_started to {kvp.Value}");
            }
        }
    }

    public void SendFireResult(string shooterRobotId, string resultText)
    {
        foreach (var kvp in _connToPlayer)
        {
            if (PlayerToRobotAny(kvp.Value) != shooterRobotId) continue;
            string json = "{\"cmd\":\"fire_result\"" +
                          ",\"connectionId\":\"" + EscapeJson(kvp.Key) + "\"" +
                          ",\"text\":\"" + EscapeJson(resultText) + "\"}";
            BroadcastRaw(json);
            Debug.Log($"[PlayerWS] fire_result → {kvp.Value}: {resultText}");
        }
    }

    void OnInvulnerabilityGranted(string robotId)
    {
        ServiceLocator.RobotServer?.SendInvulnStart(robotId);
    }

    void OnInvulnerabilityEnded(string robotId)
    {
        ServiceLocator.RobotServer?.SendInvulnEnd(robotId);
    }

    void OnRobotRespawned(string robotId)
    {
        // Notify all players on this robot so their phone can exit the dead screen
        foreach (var kvp in _connToPlayer)
        {
            if (PlayerToRobotAny(kvp.Value) != robotId) continue;

            string json = "{\"cmd\":\"you_are_alive\",\"connectionId\":\"" +
                          EscapeJson(kvp.Key) + "\"}";
            BroadcastRaw(json);
            SendSingleStateUpdate(kvp.Key);
            Debug.Log("[PlayerWS] Sent you_are_alive to " + kvp.Value);
        }

        _lowHpWarned.Remove(robotId);
        string respawnName = GetCrewNamesForRobot(robotId);
        if (!string.IsNullOrEmpty(respawnName))
            BroadcastDisplayEvent($"{respawnName} have returned to battle!");
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

    static string AllianceName(int index)
        => index == 0 ? "Desert Squad" : index == 1 ? "Jungle Squad" : "Unknown";

    string GetPlayerNameForRobot(string robotId)
    {
        var dir = ServiceLocator.RobotDirectory;
        if (string.IsNullOrEmpty(robotId) || dir == null || !dir.TryGet(robotId, out var info)) return "";
        if (!string.IsNullOrEmpty(info.AssignedPlayer)) return info.AssignedPlayer;
        if (!string.IsNullOrEmpty(info.Callsign))       return info.Callsign;
        return "";
    }

    // Returns "Driver" for single-player, or "Driver and Gunner" for two-player tanks.
    string GetCrewNamesForRobot(string robotId)
    {
        var dir = ServiceLocator.RobotDirectory;
        if (string.IsNullOrEmpty(robotId) || dir == null || !dir.TryGet(robotId, out var info)) return "";
        string driver = info.AssignedPlayer ?? "";
        string gunner = info.GunnerPlayer   ?? "";
        if (string.IsNullOrEmpty(driver) && string.IsNullOrEmpty(gunner))
            return string.IsNullOrEmpty(info.Callsign) ? "" : info.Callsign;
        if (string.IsNullOrEmpty(gunner)) return driver;
        if (string.IsNullOrEmpty(driver)) return gunner;
        return driver + " and " + gunner;
    }

    string CalculateBestPlayer()
    {
        var scores = new Dictionary<string, int>();
        foreach (var kvp in _playerKillTotal)
            scores[kvp.Key] = scores.GetValueOrDefault(kvp.Key) + kvp.Value * 10;
        foreach (var kvp in _playerCaptureScore)
            scores[kvp.Key] = scores.GetValueOrDefault(kvp.Key) + kvp.Value * 5;

        string best = null;
        int bestScore = 0;
        foreach (var kvp in scores)
            if (kvp.Value > bestScore) { bestScore = kvp.Value; best = kvp.Key; }
        return best;
    }

    void OnGameWon(int allianceIndex, string reason)
    {
        string teamName = AllianceName(allianceIndex);

        // Full spoken announcement via display event (arrives before game_over, so speak() isn't cancelled)
        string bestPlayer = CalculateBestPlayer();
        string announcement = $"Game over! {teamName} are victorious!";
        if (!string.IsNullOrEmpty(bestPlayer))
            announcement += $" {bestPlayer} was the best player!";
        BroadcastDisplayEvent(announcement);
        // game_over with stats is sent by SendGameOver() when phase transitions to Ended
    }

    void OnRfidTag(string robotId, string uid)
    {
        var settings = ServiceLocator.GameSettings;
        var game     = ServiceLocator.Game;

        // ---- Respawning robot → only interacts with its own team base ----
        if (settings != null && game?.State != null && game.State.RespawningRobots.Contains(robotId))
        {
            int alliance = GetRobotAllianceIndex(robotId);
            if (settings.IsAllianceBase(alliance, uid))
            {
                game.RespawnRobot(robotId);
                ServiceLocator.RobotServer?.SendFlashHeal(robotId);
                if (game.State.RobotHp.TryGetValue(robotId, out int hp))
                    ServiceLocator.RobotServer?.SendSetHp(robotId, hp, settings.MaxHp);
                SendRfidNotification(robotId, "You have respawned!");
            }
            return; // respawning robots don't capture points
        }

        // ---- Dead robots (explosion phase) ignore all RFID tags ----
        if (game?.State != null && game.State.DeadRobots.Contains(robotId)) return;

        // ---- Normal play: try capture point, then check own base heal ----
        bool captured = ServiceLocator.CapturePoints?.TryCapture(robotId, uid) ?? false;
        if (captured)
        {
            ServiceLocator.RobotServer?.SendFlashCapture(robotId);
            string pointName = settings?.GetCapturePointName(uid) ?? "the point";
            SendRfidNotification(robotId, "You have captured " + pointName + "!");
        }
        else
        {
            // If this IS a capture-point UID but capture failed, the team already owns it
            string pointName = settings?.GetCapturePointName(uid);
            if (pointName != null)
            {
                int allianceForCp = GetRobotAllianceIndex(robotId);
                var stateForCp    = game?.State;
                if (allianceForCp >= 0 && stateForCp != null)
                {
                    int pointIndex = pointName == "North" ? 0 : pointName == "Centre" ? 1 : 2;
                    if (pointIndex < stateForCp.CapturePointOwners.Length &&
                        stateForCp.CapturePointOwners[pointIndex] == allianceForCp)
                        SendRfidNotification(robotId, "You already captured this point!");
                }
            }
        }

        if (settings != null && game?.State != null)
        {
            int alliance = GetRobotAllianceIndex(robotId);
            if (settings.IsAllianceBase(alliance, uid))
            {
                game.RestoreHp(robotId);
                ServiceLocator.RobotServer?.SendFlashHeal(robotId);
                if (game.State.RobotHp.TryGetValue(robotId, out int hp))
                    ServiceLocator.RobotServer?.SendSetHp(robotId, hp, settings.MaxHp);
                SendRfidNotification(robotId, "Hit points restored!");
            }
            else if (settings.IsAnyBase(uid))
            {
                SendRfidNotification(robotId, "This is not your base");
            }
        }
    }

    void SendRfidNotification(string robotId, string text)
    {
        foreach (var kvp in _connToPlayer)
        {
            if (PlayerToRobot(kvp.Value) != robotId) continue;
            string json = "{\"cmd\":\"rfid_notification\"" +
                          ",\"connectionId\":\"" + EscapeJson(kvp.Key) + "\"" +
                          ",\"text\":\"" + EscapeJson(text) + "\"}";
            BroadcastRaw(json);
            Debug.Log($"[PlayerWS] rfid_notification → {kvp.Value}: {text}");
            return;
        }
    }

    void OnPausedChanged(bool paused)
    {
        var robotServer = ServiceLocator.RobotServer;
        var dir         = ServiceLocator.RobotDirectory;
        if (robotServer != null && dir != null)
        {
            foreach (var robot in dir.GetAll())
            {
                if (string.IsNullOrEmpty(robot.AssignedPlayer)) continue;
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
    void OnRobotUpdated(RobotInfo robot)
    {
        // When a robot is auto-assigned a player (e.g. tank connects after players join),
        // update that player's alliance to match the robot's team.
        if (!string.IsNullOrEmpty(robot.AssignedPlayer) && robot.PreferredAlliance >= 0)
            ServiceLocator.Players?.SetAllianceByName(robot.AssignedPlayer, robot.PreferredAlliance);

        // Player colour identification: detect assignment changes and send colour to robot + phone.
        string robotId   = robot.RobotId;
        string newPlayer = robot.AssignedPlayer ?? "";
        _robotPrevPlayer.TryGetValue(robotId, out string prevPlayer);

        if (newPlayer != (prevPlayer ?? ""))
        {
            _robotPrevPlayer[robotId] = newPlayer;
            if (!string.IsNullOrEmpty(newPlayer))
            {
                if (!_robotColorIndex.ContainsKey(robotId))
                    _robotColorIndex[robotId] = _nextColorIndex++ % PlayerColors.Length;
                int idx = _robotColorIndex[robotId];
                var (colorName, r, g, b) = PlayerColors[idx];
                ServiceLocator.RobotServer?.SendPlayerColor(robotId, r, g, b);
                string connId = FindConnForPlayer(newPlayer);
                if (!string.IsNullOrEmpty(connId))
                    BroadcastRaw("{\"cmd\":\"tank_color\",\"connectionId\":\"" +
                                 EscapeJson(connId) + "\",\"colorName\":\"" +
                                 EscapeJson(colorName) + "\"}");
            }
            else
            {
                ServiceLocator.RobotServer?.SendClearPlayerColor(robotId);
            }
        }

        BroadcastPlayerList();
        BroadcastRobotList();
    }

    string FindConnForPlayer(string playerName)
    {
        foreach (var kvp in _connToPlayer)
            if (kvp.Value == playerName) return kvp.Key;
        return null;
    }
    void OnRobotRemoved(string _)    { BroadcastPlayerList(); BroadcastRobotList(); }

    void OnCapturePointCaptured(int pointIndex, int allianceIndex, string pointName)
    {
        string playerName = FindPlayerForLastCapture(allianceIndex);
        string text = string.IsNullOrEmpty(playerName)
            ? $"{AllianceName(allianceIndex)} captured {pointName} Point!"
            : $"{playerName} captured {pointName} Point!";
        BroadcastDisplayEvent(text);

        // Credit capture for best-player scoring
        if (!string.IsNullOrEmpty(playerName))
        {
            if (!_playerCaptureScore.ContainsKey(playerName)) _playerCaptureScore[playerName] = 0;
            _playerCaptureScore[playerName]++;
        }

        // All three points captured by same alliance?
        var gs = ServiceLocator.Game?.State;
        if (gs?.CapturePointOwners != null && gs.CapturePointOwners.Length >= 3
            && gs.CapturePointOwners[0] == allianceIndex
            && gs.CapturePointOwners[1] == allianceIndex
            && gs.CapturePointOwners[2] == allianceIndex)
        {
            BroadcastDisplayEvent($"{AllianceName(allianceIndex)} has captured all the points!");
        }

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

    void OnTeamPointsChanged()
    {
        BroadcastDisplayUpdate();

        var gs       = ServiceLocator.Game?.State;
        var settings = ServiceLocator.GameSettings;
        if (gs == null || settings == null) return;

        int max = settings.MaxTeamPoints;
        int tp0 = gs.TeamPoints.Length > 0 ? gs.TeamPoints[0] : 0;
        int tp1 = gs.TeamPoints.Length > 1 ? gs.TeamPoints[1] : 0;
        int prev0 = _prevTeamPoints[0];
        int prev1 = _prevTeamPoints[1];

        // Milestone checks (each fires at most once per game)
        for (int a = 0; a < 2; a++)
        {
            int pts  = a == 0 ? tp0 : tp1;
            string n = AllianceName(a);

            if (!_teamPoints50Fired[a] && pts >= max / 2)
            {
                _teamPoints50Fired[a] = true;
                BroadcastDisplayEvent($"{n} is halfway to a points victory!");
            }
            if (!_teamPoints90Fired[a] && pts >= (max * 9) / 10)
            {
                _teamPoints90Fired[a] = true;
                BroadcastDisplayEvent($"{n} is nearly victorious!");
            }
        }

        // Overtake: announce when a team that was NOT leading becomes the leader.
        // Require both teams to have scored to avoid "first kill = takes the lead" noise.
        if (tp0 > 0 && tp1 > 0)
        {
            bool prev0Leading = prev0 > prev1;
            bool prev1Leading = prev1 > prev0;
            if (!prev0Leading && tp0 > tp1)
                BroadcastDisplayEvent($"{AllianceName(0)} has taken the points lead!");
            else if (!prev1Leading && tp1 > tp0)
                BroadcastDisplayEvent($"{AllianceName(1)} has taken the points lead!");
        }

        _prevTeamPoints[0] = tp0;
        _prevTeamPoints[1] = tp1;
    }

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
        string robotId = PlayerToRobotAny(playerName);
        var dir = ServiceLocator.RobotDirectory;
        string callsign = robotId;
        string ip       = "";

        // Determine role: solo (no gunner), driver, or gunner
        string role        = "solo";
        string partnerName = "";
        if (robotId != null && dir != null && dir.TryGet(robotId, out var rInfo))
        {
            callsign = string.IsNullOrEmpty(rInfo.Callsign) ? robotId : rInfo.Callsign;
            ip       = rInfo.Ip ?? "";
            if (!string.IsNullOrEmpty(rInfo.GunnerPlayer))
            {
                if (rInfo.AssignedPlayer == playerName)
                { role = "driver"; partnerName = rInfo.GunnerPlayer; }
                else
                { role = "gunner"; partnerName = rInfo.AssignedPlayer ?? ""; }
            }
        }

        int maxHp   = ServiceLocator.GameSettings?.MaxHp ?? 100;
        int hp      = GetCurrentHp(robotId, maxHp);
        float slowT = ServiceLocator.GameSettings?.SlowTurretSpeed ?? 0.4f;
        float cooldownDuration = ServiceLocator.GameSettings?.FireCooldownSeconds ?? 3f;

        string videoUrl = string.IsNullOrEmpty(ip) ? "" : "http://" + ip + ":81/stream";

        string json =
            "{\"cmd\":\"game_started\"" +
            ",\"connectionId\":\""  + EscapeJson(connId)           + "\"" +
            ",\"callsign\":\""      + EscapeJson(callsign)         + "\"" +
            ",\"videoUrl\":\""      + EscapeJson(videoUrl)         + "\"" +
            ",\"hp\":"              + hp                                   +
            ",\"maxHp\":"           + maxHp                                +
            ",\"slowTurretSpeed\":" + slowT.ToString("F3")                 +
            ",\"cooldownDuration\":" + cooldownDuration.ToString("F2")    +
            ",\"role\":\""          + EscapeJson(role)              + "\"" +
            ",\"partnerName\":\""   + EscapeJson(partnerName)       + "\"" +
            "}";

        BroadcastRaw(json);
        Debug.Log($"[PlayerWS] Sent game_started to {playerName} (robot={callsign}, role={role})");
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

        string robotId           = PlayerToRobotAny(playerName);
        int maxHp                = ServiceLocator.GameSettings?.MaxHp ?? 100;
        int hp                   = GetCurrentHp(robotId, maxHp);
        float timer              = ServiceLocator.MatchTimer?.Remaining ?? 0f;
        float cooldown           = ServiceLocator.Shooting?.CooldownRemaining(robotId) ?? 0f;
        float cooldownDuration   = ServiceLocator.GameSettings?.FireCooldownSeconds ?? 3f;

        string json =
            "{\"cmd\":\"state_update\"" +
            ",\"connectionId\":\""    + EscapeJson(connId)             + "\"" +
            ",\"hp\":"                + hp                                    +
            ",\"maxHp\":"             + maxHp                                 +
            ",\"timer\":"             + timer.ToString("F1")                  +
            ",\"cooldown\":"          + cooldown.ToString("F2")               +
            ",\"cooldownDuration\":"  + cooldownDuration.ToString("F2")       +
            "}";

        BroadcastRaw(json);
    }

    void SendGameOver()
    {
        var state    = ServiceLocator.Game?.State;
        var players  = ServiceLocator.Players;
        string teamName = (state != null && state.WinnerAllianceIndex >= 0)
            ? AllianceName(state.WinnerAllianceIndex) : "";
        string reason   = state?.EndReason;
        if (string.IsNullOrEmpty(reason)) reason = "manual";

        var sb = new StringBuilder();
        sb.Append("{\"cmd\":\"game_over\"");
        sb.Append(",\"winnerTeam\":\""); sb.Append(EscapeJson(teamName)); sb.Append("\"");
        sb.Append(",\"reason\":\"");     sb.Append(EscapeJson(reason));   sb.Append("\"");
        // Most ground covered: player with highest accumulated drive effort
        string mostGroundPlayer = "";
        float  mostGroundVal    = -1f;
        foreach (var kvp in _statGround)
            if (kvp.Value > mostGroundVal) { mostGroundVal = kvp.Value; mostGroundPlayer = kvp.Key; }
        if (!string.IsNullOrEmpty(mostGroundPlayer))
        {
            sb.Append(",\"mostGround\":\""); sb.Append(EscapeJson(mostGroundPlayer)); sb.Append("\"");
        }

        sb.Append(",\"playerStats\":[");
        bool first = true;
        foreach (var kvp in _connToPlayer)
        {
            string connId     = kvp.Key;
            string playerName = kvp.Value;
            if (PlayerToRobotAny(playerName) == null) continue; // skip lobby-only connections

            int kills         = _statKills.GetValueOrDefault(playerName, 0);
            int deaths        = _statDeaths.GetValueOrDefault(playerName, 0);
            int damage        = _statDamage.GetValueOrDefault(playerName, 0);
            string nemesis    = GetNemesisFor(playerName);

            // Individual VP contribution: captures (float → rounded int) + kill bonuses
            int vpFromCaptures = (int)System.Math.Round(_statVpFromCaptures.GetValueOrDefault(playerName, 0f));
            int vpFromKills    = _statVpFromKills.GetValueOrDefault(playerName, 0);
            int victoryPoints  = vpFromCaptures + vpFromKills;

            if (!first) sb.Append(",");
            first = false;
            sb.Append("{\"connectionId\":\""); sb.Append(EscapeJson(connId));      sb.Append("\"");
            sb.Append(",\"playerName\":\"");   sb.Append(EscapeJson(playerName)); sb.Append("\"");
            sb.Append(",\"kills\":");           sb.Append(kills);
            sb.Append(",\"deaths\":");          sb.Append(deaths);
            sb.Append(",\"damage\":");          sb.Append(damage);
            sb.Append(",\"victoryPoints\":");   sb.Append(victoryPoints);
            sb.Append(",\"vpFromCaptures\":");  sb.Append(vpFromCaptures);
            sb.Append(",\"vpFromKills\":");     sb.Append(vpFromKills);
            sb.Append(",\"nemesis\":\"");       sb.Append(EscapeJson(nemesis));   sb.Append("\"");
            sb.Append("}");
        }
        sb.Append("]}");
        BroadcastRaw(sb.ToString());
        Debug.Log("[PlayerWS] Sent game_over: " + teamName + " (" + reason + ")");
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
        var dir = ServiceLocator.RobotDirectory;
        var sb = new StringBuilder("{\"cmd\":\"player_list\",\"players\":[");
        if (players != null)
        {
            for (int i = 0; i < players.Count; i++)
            {
                if (i > 0) sb.Append(',');
                string robotCallsign = "";
                if (dir != null)
                    foreach (var robot in dir.GetAll())
                        if (robot.AssignedPlayer == players[i].Name || robot.GunnerPlayer == players[i].Name)
                        {
                            robotCallsign = string.IsNullOrEmpty(robot.Callsign) ? robot.RobotId : robot.Callsign;
                            break;
                        }
                sb.Append("{\"name\":\"");
                sb.Append(EscapeJson(players[i].Name));
                sb.Append("\",\"alliance\":");
                sb.Append(players[i].AllianceIndex);
                sb.Append(",\"robot\":\"");
                sb.Append(EscapeJson(robotCallsign));
                sb.Append("\"}");
            }
        }
        sb.Append("]}");
        return sb.ToString();
    }

    // ── Robot list broadcast ──────────────────────────────────────────────────────

    void BroadcastRobotList()
    {
        var dir = ServiceLocator.RobotDirectory;
        bool twoPlayerMode = ServiceLocator.GameSettings?.TwoPlayerModeEnabled ?? false;
        var sb = new StringBuilder("{\"cmd\":\"robot_list\"");
        sb.Append(",\"twoPlayerModeEnabled\":"); sb.Append(twoPlayerMode ? "true" : "false");
        sb.Append(",\"robots\":[");
        bool first = true;
        if (dir != null)
        {
            foreach (var robot in dir.GetAll())
            {
                if (!first) sb.Append(',');
                first = false;
                string callsign = string.IsNullOrEmpty(robot.Callsign) ? robot.RobotId : robot.Callsign;
                sb.Append("{\"id\":\"");              sb.Append(EscapeJson(robot.RobotId));              sb.Append("\"");
                sb.Append(",\"name\":\"");            sb.Append(EscapeJson(callsign));                   sb.Append("\"");
                sb.Append(",\"alliance\":");          sb.Append(robot.PreferredAlliance);
                sb.Append(",\"assignedPlayer\":\"");  sb.Append(EscapeJson(robot.AssignedPlayer ?? "")); sb.Append("\"");
                sb.Append(",\"gunnerPlayer\":\"");    sb.Append(EscapeJson(robot.GunnerPlayer   ?? "")); sb.Append("\"");
                sb.Append(",\"twoPlayerEnabled\":"); sb.Append(robot.TwoPlayerEnabled ? "true" : "false");
                sb.Append("}");
            }
        }
        sb.Append("]}");
        BroadcastRaw(sb.ToString());
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

        // Only a live or just-finished match has meaningful robot/point state. In
        // MainMenu/Lobby the previous GameState still lingers; suppress it so the
        // display doesn't show stale robots/HP/points from the prior game.
        bool inMatch = phase == "playing" || phase == "ended";

        int maxPlayers   = settings?.MaxPlayers    ?? 6;
        int maxTeamPts   = settings?.MaxTeamPoints  ?? 300;
        int maxHp        = settings?.MaxHp          ?? 100;
        int playerCount  = players?.Count ?? 0;

        int tp0 = inMatch && gs?.TeamPoints != null && gs.TeamPoints.Length > 0 ? gs.TeamPoints[0] : 0;
        int tp1 = inMatch && gs?.TeamPoints != null && gs.TeamPoints.Length > 1 ? gs.TeamPoints[1] : 0;

        bool paused = flow?.IsPaused ?? false;

        float matchDuration = settings?.MatchDurationSeconds ?? 600f;

        sb.Append("{\"cmd\":\"display_update\"");
        sb.Append(",\"phase\":\"");        sb.Append(phase); sb.Append("\"");
        sb.Append(",\"paused\":");         sb.Append(paused ? "true" : "false");
        sb.Append(",\"timer\":");          sb.Append(timer.ToString("F1"));
        sb.Append(",\"matchDuration\":"); sb.Append(matchDuration.ToString("F0"));
        sb.Append(",\"playerCount\":"); sb.Append(playerCount);
        sb.Append(",\"maxPlayers\":"); sb.Append(maxPlayers);
        sb.Append(",\"teamPoints\":["); sb.Append(tp0); sb.Append(","); sb.Append(tp1); sb.Append("]");
        sb.Append(",\"maxTeamPoints\":"); sb.Append(maxTeamPts);
        sb.Append(",\"allianceNames\":[\""); sb.Append(EscapeJson(AllianceName(0)));
        sb.Append("\",\"");                  sb.Append(EscapeJson(AllianceName(1))); sb.Append("\"]");

        // Robots array
        sb.Append(",\"robots\":[");
        if (inMatch && gs?.Robots != null)
        {
            bool first = true;
            foreach (var r in gs.Robots)
            {
                if (!first) sb.Append(",");
                first = false;

                string callsign   = r.Callsign ?? r.RobotId;
                string playerName = r.AssignedPlayer ?? "";
                string gunnerName = r.GunnerPlayer   ?? "";
                int    alliance   = -1;
                if (players != null)
                    foreach (var p in players)
                        if (p.Name == playerName) { alliance = p.AllianceIndex; break; }

                int hp = gs.RobotHp.TryGetValue(r.RobotId, out int v) ? v : maxHp;
                bool dead = gs.DeadRobots.Contains(r.RobotId);

                // Get live IP from directory (snapshot in gs may be stale after reconnect)
                string liveIp = "";
                if (dir != null && dir.TryGet(r.RobotId, out var liveInfo))
                    liveIp = liveInfo.Ip ?? "";
                string videoUrl = string.IsNullOrEmpty(liveIp) ? "" : "http://" + liveIp + ":81/stream";

                sb.Append("{\"callsign\":\"");     sb.Append(EscapeJson(callsign));   sb.Append("\"");
                sb.Append(",\"player\":\"");        sb.Append(EscapeJson(playerName)); sb.Append("\"");
                sb.Append(",\"gunnerPlayer\":\"");  sb.Append(EscapeJson(gunnerName)); sb.Append("\"");
                sb.Append(",\"alliance\":"); sb.Append(alliance);
                sb.Append(",\"hp\":"); sb.Append(hp);
                sb.Append(",\"maxHp\":"); sb.Append(maxHp);
                sb.Append(",\"dead\":"); sb.Append(dead ? "true" : "false");
                sb.Append(",\"videoUrl\":\""); sb.Append(EscapeJson(videoUrl)); sb.Append("\"");
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
            int owner = inMatch && gs?.CapturePointOwners != null && i < gs.CapturePointOwners.Length
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

    public void SendSpectateUpdate(string robotId, bool enabled)
    {
        if (!enabled)
        {
            BroadcastRaw("{\"cmd\":\"spectate_update\",\"enabled\":false}");
            return;
        }

        var dir = ServiceLocator.RobotDirectory;
        if (dir == null || string.IsNullOrEmpty(robotId) || !dir.TryGet(robotId, out var rInfo)) return;

        string ip         = rInfo.Ip ?? "";
        string playerName = rInfo.AssignedPlayer ?? "";
        string videoUrl   = string.IsNullOrEmpty(ip) ? "" : "http://" + ip + ":81/stream";
        int    maxHp      = ServiceLocator.GameSettings?.MaxHp ?? 100;
        var    state      = ServiceLocator.Game?.State;
        int    hp         = (state != null && state.RobotHp.TryGetValue(robotId, out int v)) ? v : maxHp;

        string json =
            "{\"cmd\":\"spectate_update\"" +
            ",\"enabled\":true" +
            ",\"mode\":\"single\"" +
            ",\"videoUrl\":\""    + EscapeJson(videoUrl)    + "\"" +
            ",\"playerName\":\"" + EscapeJson(playerName)  + "\"" +
            ",\"hp\":"           + hp                             +
            ",\"maxHp\":"        + maxHp                          +
            "}";
        BroadcastRaw(json);
        Debug.Log("[PlayerWS] spectate_update single → player=" + playerName + " url=" + videoUrl);
    }

    public void SendSpectateUpdateGrid()
    {
        BroadcastRaw("{\"cmd\":\"spectate_update\",\"enabled\":true,\"mode\":\"grid\"}");
        Debug.Log("[PlayerWS] spectate_update grid");
    }

    public void BroadcastTwoPlayerModeChanged(bool enabled)
    {
        BroadcastRaw("{\"cmd\":\"two_player_mode\",\"enabled\":" + (enabled ? "true" : "false") + "}");
        BroadcastRobotList(); // refresh lobby so phones pick up the new setting
        Debug.Log("[PlayerWS] BroadcastTwoPlayerModeChanged: " + enabled);
    }

    public void BroadcastTurretSettings()
    {
        float slow = ServiceLocator.GameSettings?.SlowTurretSpeed ?? 0.4f;
        string json = "{\"cmd\":\"turret_settings\",\"slowSpeed\":" + slow.ToString("F3") + "}";
        BroadcastRaw(json);
    }

    public void BroadcastCountdownStart(int total)
    {
        BroadcastRaw("{\"cmd\":\"countdown_start\",\"total\":" + total + "}");
        Debug.Log("[PlayerWS] countdown_start total=" + total);
    }

    public void BroadcastCountdownTick(int count, int total)
    {
        BroadcastRaw("{\"cmd\":\"countdown_tick\",\"count\":" + count + ",\"total\":" + total + "}");
        Debug.Log("[PlayerWS] countdown_tick count=" + count + "/" + total);
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

    string GunnerToRobot(string playerName)
    {
        if (string.IsNullOrEmpty(playerName)) return null;
        var dir = ServiceLocator.RobotDirectory;
        if (dir == null) return null;
        foreach (var r in dir.GetAll())
            if (r.GunnerPlayer == playerName) return r.RobotId;
        return null;
    }

    // Returns the robot for a player regardless of whether they are driver or gunner.
    string PlayerToRobotAny(string playerName)
        => PlayerToRobot(playerName) ?? GunnerToRobot(playerName);

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
        public int    alliance = -1;
        public string robotId;
        public bool   enabled;
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
