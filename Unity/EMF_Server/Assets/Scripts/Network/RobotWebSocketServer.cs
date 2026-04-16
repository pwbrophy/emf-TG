// RobotWebSocketServer.cs - WebSocket host for ESP32 robots (text control + binary JPEG frames)
using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using WebSocketSharp;
using WebSocketSharp.Server;

public class RobotWebSocketServer : MonoBehaviour
{
    [Header("WebSocket Host Settings")]
    public int Port = 8080;
    public string Path = "/esp32";

    [Header("Timeouts")]
    public float TimeoutSeconds = 8f;
    public float SweepIntervalSeconds = 2f;

    [Header("Debug")]
    public bool VerboseJoins = true;
    public bool VerboseLeaves = true;
    public bool VerboseHeartbeats = false;

    // --- State / services ---
    private bool serverStarted = false;
    private WebSocketServer _wss;
    private IRobotDirectory _dir;
    private GameFlow _flow;

    // Per-session data
    private class SessionInfo
    {
        public string RobotId;
        public float LastSeenTime;
        public int NumFrames;
    }

    private readonly Dictionary<string, SessionInfo> _bySession = new Dictionary<string, SessionInfo>();
    private readonly Dictionary<string, string> _sessionByRobot = new Dictionary<string, string>();

    // Main-thread queue for Unity safety
    private readonly Queue<Action> _main = new Queue<Action>();
    private readonly object _mtx = new object();

    private float _nextSweepTime = 0f;

    // Fired when a robot replies to a ping.  Arg: robotId.
    public event Action<string> OnPong;

    // Fired when a robot finishes preparing to emit IR.
    public event Action<string> OnIrEmitReady;

    // Fired when a robot completes an IR listen-and-report window.
    // Args: robotId, hit, direction (compass string e.g. "SE", or "" if no hit)
    public event Action<string, bool, string> OnIrResult;

    private static RobotWebSocketServer _self;

    private void Awake()
    {
        _self = this;
        ServiceLocator.RobotServer = this;
    }

    private void Start()
    {
        StartWebSocketServer();
    }

    private void OnDestroy()
    {
        if (_self == this) _self = null;
        if (ServiceLocator.RobotServer == this) ServiceLocator.RobotServer = null;
        StopServer();
    }

    // ===== Public control =====

    public void StartWebSocketServer()
    {
        if (serverStarted) return;

        _dir = ServiceLocator.RobotDirectory;
        _flow = ServiceLocator.GameFlow;

        if (_dir == null || _flow == null)
        {
            Debug.LogError("[WS] RobotDirectory or GameFlow is null.");
            return;
        }

        string ip = PetersUtils.GetLocalIPAddress().ToString();

        // Bind to all interfaces (0.0.0.0) so robots on any subnet can connect.
        // Binding to a specific IP in WebSocketSharp can silently reject connections
        // that arrive on a different adapter or after a DHCP renewal.
        Debug.Log("[WS] Starting server on 0.0.0.0:" + Port + Path + "  (LAN IP: " + ip + ")");

        _wss = new WebSocketServer(Port);
        _wss.KeepClean = false;

        var parent = this;
        _wss.AddWebSocketService<ESP32Service>(Path, () => new ESP32Service { Parent = parent });

        _wss.Start();
        Debug.Log("[WS] Started");

        _nextSweepTime = Time.time + SweepIntervalSeconds;
        serverStarted = true;

        ServiceLocator.RobotServer = this;
    }

    public void StopServer()
    {
        if (!serverStarted) return;
        try { _wss.Stop(); } catch { /* ignore */ }
        _wss = null;
        serverStarted = false;
    }

    // ===== Unity Update loop =====

    private void Update()
    {
        PumpMain();

        if (serverStarted && Time.time >= _nextSweepTime)
        {
            _nextSweepTime = Time.time + SweepIntervalSeconds;
            SweepForTimeouts();
        }
    }

    public void PostMain(Action a)
    {
        if (a == null) return;
        lock (_mtx) _main.Enqueue(a);
    }

    private void PumpMain()
    {
        for (; ; )
        {
            Action a = null;
            lock (_mtx)
            {
                if (_main.Count == 0) break;
                a = _main.Dequeue();
            }
            try { a?.Invoke(); }
            catch (Exception ex) { Debug.LogException(ex); }
        }
    }

    // ===== Behavior for each WebSocket connection =====

    private class ESP32Service : WebSocketBehavior
    {
        public RobotWebSocketServer Parent;

        protected override void OnOpen()
        {
            if (Parent != null) Parent.PostMain(() => Parent.OnOpened(ID));
        }

        protected override void OnClose(CloseEventArgs e)
        {
            if (Parent != null) Parent.PostMain(() => Parent.OnClosed(ID, e));
        }

        protected override void OnMessage(MessageEventArgs e)
        {
            if (Parent == null) return;

            if (e.IsText)
            {
                string data = e.Data;
                Parent.PostMain(() => Parent.HandleText(ID, data));
                return;
            }

            if (e.IsBinary)
            {
                var bytes = e.RawData;
                Parent.PostMain(() => Parent.HandleBinary(ID, bytes));
                return;
            }
        }
    }

    // ===== Host callbacks for service =====

    private void OnOpened(string sid)
    {
        if (!_bySession.ContainsKey(sid))
        {
            _bySession[sid] = new SessionInfo
            {
                RobotId = null,
                LastSeenTime = Time.time,
                NumFrames = 0
            };
        }
    }

    private void OnClosed(string sid, CloseEventArgs e)
    {
        if (_bySession.TryGetValue(sid, out var info))
        {
            var rid = info.RobotId;

            _bySession.Remove(sid);

            if (!string.IsNullOrEmpty(rid))
            {
                if (_sessionByRobot.TryGetValue(rid, out var mapSid) && mapSid == sid)
                    _sessionByRobot.Remove(rid);

                // Do NOT remove from RobotDirectory on a WebSocket close — the robot
                // may be momentarily disconnected and will re-send hello shortly.
                // Removing it here means UDP Upsert re-adds it with no session, causing
                // all subsequent commands (motors_on, ping, drive) to silently FAIL.
                // Only the heartbeat timeout sweep removes stale directory entries.
                if (VerboseLeaves) Debug.Log("[WS] Robot disconnected: " + rid);
            }
        }
    }

    public void HandleText(string sid, string json)
    {
        if (string.IsNullOrEmpty(json)) return;

        string cmd = ExtractString(json, "cmd");
        if (string.IsNullOrEmpty(cmd)) return;

        if (cmd == "hello")
        {
            // Allow robots to reconnect at any game phase — don't gate on Lobby.
            // If we kick robots that connect during Playing they can never re-register
            // and all commands fail (FAILED motors_on, FAILED ping, etc.).

            string id = ExtractString(json, "id");
            if (string.IsNullOrEmpty(id)) return;

            if (!_bySession.TryGetValue(sid, out var info))
            {
                info = new SessionInfo();
                _bySession[sid] = info;
            }
            info.RobotId = id;
            info.LastSeenTime = Time.time;
            info.NumFrames = 0;

            _sessionByRobot[id] = sid;

            _dir?.Upsert(id, id, ip: "");

            if (VerboseJoins) Debug.Log("[WS] Robot hello: " + id);
            return;
        }

        if (cmd == "hb")
        {
            if (_bySession.TryGetValue(sid, out var info))
            {
                info.LastSeenTime = Time.time;
                if (VerboseHeartbeats && info.NumFrames % 30 == 0)
                    Debug.Log("[WS] hb from " + (info.RobotId ?? sid));
            }
            return;
        }

        if (cmd == "pong")
        {
            if (!_bySession.TryGetValue(sid, out var pongInfo)) return;
            string pongRobotId = pongInfo.RobotId;
            if (string.IsNullOrEmpty(pongRobotId)) return;
            Debug.Log($"[WS<-Robot] pong from {pongRobotId}");
            OnPong?.Invoke(pongRobotId);
            return;
        }

        if (cmd == "ir_emit_ready")
        {
            if (!_bySession.TryGetValue(sid, out var info)) return;

            string robotId = info.RobotId;
            if (string.IsNullOrEmpty(robotId)) return;

            Debug.Log($"[WS<-Robot] ir_emit_ready -> {robotId}");
            OnIrEmitReady?.Invoke(robotId);
            return;
        }

        if (cmd == "ir_result")
        {
            if (!_bySession.TryGetValue(sid, out var info)) return;

            string robotId = info.RobotId;
            if (string.IsNullOrEmpty(robotId)) return;

            bool   hit = false;
            string dir = "";
            try
            {
                // Parse "hit" field
                int k = json.IndexOf("\"hit\"", StringComparison.Ordinal);
                if (k >= 0)
                {
                    int colon = json.IndexOf(':', k);
                    if (colon >= 0)
                    {
                        string tail = json.Substring(colon + 1).Trim(' ', '\t', '\r', '\n', ',', '}');
                        if (int.TryParse(tail, out var val))
                            hit = (val != 0);
                    }
                }

                // Parse optional "dir" field e.g. "dir":"SE"
                int dk = json.IndexOf("\"dir\"", StringComparison.Ordinal);
                if (dk >= 0)
                {
                    int colon = json.IndexOf(':', dk);
                    if (colon >= 0)
                    {
                        int q1 = json.IndexOf('"', colon + 1);
                        int q2 = q1 >= 0 ? json.IndexOf('"', q1 + 1) : -1;
                        if (q1 >= 0 && q2 > q1)
                            dir = json.Substring(q1 + 1, q2 - q1 - 1);
                    }
                }
            }
            catch { /* Leave defaults if parsing failed */ }

            Debug.Log($"[WS<-Robot] ir_result hit={(hit ? 1 : 0)} dir={dir} -> {robotId}");
            OnIrResult?.Invoke(robotId, hit, dir);
            return;
        }
    }

    public void HandleBinary(string sid, byte[] data)
    {
        if (data == null || data.Length == 0) return;

        if (!_bySession.TryGetValue(sid, out var info)) return;
        string robotId = info.RobotId;
        if (string.IsNullOrEmpty(robotId)) return;

        info.NumFrames++;

        var rx = ESP32VideoReceiver.Instance;
        if (rx != null) rx.ReceiveFrame(robotId, data);
    }

    private void SweepForTimeouts()
    {
        if (_bySession.Count == 0) return;

        var now = Time.time;
        var toDrop = new List<string>();

        foreach (var kv in _bySession)
        {
            if (now - kv.Value.LastSeenTime > TimeoutSeconds)
                toDrop.Add(kv.Key);
        }

        foreach (var sid in toDrop)
        {
            if (_bySession.TryGetValue(sid, out var info))
            {
                var rid = info.RobotId;

                try { ServiceSessions()?.CloseSession(sid); } catch { /* ignore */ }
                _bySession.Remove(sid);

                if (!string.IsNullOrEmpty(rid))
                {
                    if (_sessionByRobot.TryGetValue(rid, out var mapSid) && mapSid == sid)
                        _sessionByRobot.Remove(rid);

                    _dir?.Remove(rid);
                    if (VerboseLeaves) Debug.Log("[WS] Robot timeout: " + rid);
                }
            }
        }
    }

    // ===== Public send helpers for UI code =====

    public bool SendJsonToRobot(string robotId, string json)
    {
        if (string.IsNullOrEmpty(robotId) || string.IsNullOrEmpty(json)) return false;
        if (_wss == null) return false;

        if (!_sessionByRobot.TryGetValue(robotId, out var sid)) return false;

        var sessions = ServiceSessions();
        if (sessions == null) return false;

        try { sessions.SendTo(json, sid); }
        catch { return false; }

        return true;
    }

    public bool SendPing(string robotId)
    {
        bool ok = SendJsonToRobot(robotId, "{\"cmd\":\"ping\"}");
        Debug.Log(ok ? $"[WS->Robot] ping -> {robotId}" : $"[WS->Robot] FAILED ping -> {robotId}");
        return ok;
    }

    public bool SendFlashCommand(string robotId, int pin, int ms)
    {
        string json = "{\"cmd\":\"flash\",\"pin\":" + pin + ",\"ms\":" + ms + "}";
        return SendJsonToRobot(robotId, json);
    }

    public bool SendStreamOff(string robotId)
    {
        return SendJsonToRobot(robotId, "{\"cmd\":\"stream_off\"}");
    }

    public bool SendStreamOn(string robotId)
    {
        return SendJsonToRobot(robotId, "{\"cmd\":\"stream_on\"}");
    }

    public bool SendMotorsOn(string robotId)
    {
        bool ok = SendJsonToRobot(robotId, "{\"cmd\":\"motors_on\"}");
        Debug.Log(ok ? $"[WS->Robot] motors_on -> {robotId}" : $"[WS->Robot] FAILED motors_on -> {robotId}");
        return ok;
    }

    public bool SendMotorsOff(string robotId)
    {
        bool ok = SendJsonToRobot(robotId, "{\"cmd\":\"motors_off\"}");
        Debug.Log(ok ? $"[WS->Robot] motors_off -> {robotId}" : $"[WS->Robot] FAILED motors_off -> {robotId}");
        return ok;
    }

    public bool SendDrive(string robotId, float left, float right)
    {
        string l = left.ToString("F3", CultureInfo.InvariantCulture);
        string r = right.ToString("F3", CultureInfo.InvariantCulture);
        string json = $"{{\"cmd\":\"drive\",\"l\":{l},\"r\":{r}}}";
        return SendJsonToRobot(robotId, json);
    }

    public bool SendTurret(string robotId, float speed)
    {
        string s = speed.ToString("F3", CultureInfo.InvariantCulture);
        string json = $"{{\"cmd\":\"turret\",\"speed\":{s}}}";
        return SendJsonToRobot(robotId, json);
    }

    public bool SendIrEmitPrepare(string robotId)
    {
        if (string.IsNullOrEmpty(robotId))
        {
            Debug.LogWarning("[WS->Robot][IR] ir_emit_prepare: robotId null/empty");
            return false;
        }
        string json = "{\"cmd\":\"ir_emit_prepare\"}";
        bool ok = SendJsonToRobot(robotId, json);
        Debug.Log(ok
            ? $"[WS->Robot] ir_emit_prepare -> {robotId}"
            : $"[WS->Robot] FAILED ir_emit_prepare -> {robotId}");
        return ok;
    }

    public bool SendIrEmitStop(string robotId)
    {
        if (string.IsNullOrEmpty(robotId))
        {
            Debug.LogWarning("[WS->Robot][IR] ir_emit_stop: robotId null/empty");
            return false;
        }
        string json = "{\"cmd\":\"ir_emit_stop\"}";
        bool ok = SendJsonToRobot(robotId, json);
        Debug.Log(ok
            ? $"[WS->Robot] ir_emit_stop -> {robotId}"
            : $"[WS->Robot] FAILED ir_emit_stop -> {robotId}");
        return ok;
    }

    public bool SendIrListenAndReport(string robotId, int ms)
    {
        if (string.IsNullOrEmpty(robotId))
        {
            Debug.LogWarning("[WS->Robot][IR] ir_listen_and_report: robotId null/empty");
            return false;
        }
        if (ms < 1) ms = 1;
        string json = "{\"cmd\":\"ir_listen_and_report\",\"ms\":" + ms + "}";
        bool ok = SendJsonToRobot(robotId, json);
        Debug.Log(ok
            ? $"[WS->Robot] ir_listen_and_report ms={ms} -> {robotId}"
            : $"[WS->Robot] FAILED ir_listen_and_report -> {robotId}");
        return ok;
    }

    public bool SendIrListen(string robotId, int ms)
    {
        if (string.IsNullOrEmpty(robotId))
        {
            Debug.LogWarning("[WS->Robot][IR] ir_listen: robotId is null/empty");
            return false;
        }
        if (ms < 1) ms = 1;
        string json = "{\"cmd\":\"ir_listen\",\"ms\":" + ms + "}";
        Debug.Log("[WS->Robot] ir_listen ms=" + ms + " -> " + robotId);
        return SendJsonToRobot(robotId, json);
    }

    public bool SendIrLedAll(string robotId, bool on)
    {
        if (string.IsNullOrEmpty(robotId))
        {
            Debug.LogWarning("[WS->Robot][IR] ir_led_all: robotId is null/empty");
            return false;
        }
        string json = "{\"cmd\":\"ir_led_all\",\"on\":" + (on ? "1" : "0") + "}";
        Debug.Log("[WS->Robot] ir_led_all on=" + (on ? "1" : "0") + " -> " + robotId);
        return SendJsonToRobot(robotId, json);
    }

    public bool SendIrRead(string robotId)
    {
        if (string.IsNullOrEmpty(robotId))
        {
            Debug.LogWarning("[WS->Robot][IR] ir_read: robotId is null/empty");
            return false;
        }
        Debug.Log("[WS->Robot] ir_read -> " + robotId);
        return SendJsonToRobot(robotId, "{\"cmd\":\"ir_read\"}");
    }

    // ===== LED / buzzer feedback commands =====

    public bool SendFlashFire(string robotId)
    {
        if (string.IsNullOrEmpty(robotId)) return false;
        bool ok = SendJsonToRobot(robotId, "{\"cmd\":\"flash_fire\"}");
        Debug.Log(ok ? $"[WS->Robot] flash_fire -> {robotId}" : $"[WS->Robot] FAILED flash_fire -> {robotId}");
        return ok;
    }

    public bool SendFlashHit(string robotId)
    {
        if (string.IsNullOrEmpty(robotId)) return false;
        bool ok = SendJsonToRobot(robotId, "{\"cmd\":\"flash_hit\"}");
        Debug.Log(ok ? $"[WS->Robot] flash_hit -> {robotId}" : $"[WS->Robot] FAILED flash_hit -> {robotId}");
        return ok;
    }

    public bool SendSetHp(string robotId, int hp, int maxHp)
    {
        if (string.IsNullOrEmpty(robotId)) return false;
        string json = $"{{\"cmd\":\"set_hp\",\"hp\":{hp},\"max\":{maxHp}}}";
        bool ok = SendJsonToRobot(robotId, json);
        Debug.Log(ok ? $"[WS->Robot] set_hp hp={hp}/{maxHp} -> {robotId}" : $"[WS->Robot] FAILED set_hp -> {robotId}");
        return ok;
    }

    // ===== Internals =====

    private WebSocketSharp.Server.WebSocketSessionManager ServiceSessions()
    {
        if (_wss == null) return null;
        var svcHost = _wss.WebSocketServices[Path];
        return svcHost?.Sessions;
    }

    private static string ExtractString(string s, string key)
    {
        if (string.IsNullOrEmpty(s) || string.IsNullOrEmpty(key)) return null;
        try
        {
            int k = s.IndexOf("\"" + key + "\"", StringComparison.Ordinal);
            if (k < 0) return null;
            int colon = s.IndexOf(':', k);
            if (colon < 0) return null;
            int q1 = s.IndexOf('"', colon + 1);
            if (q1 < 0) return null;
            int q2 = s.IndexOf('"', q1 + 1);
            if (q2 < 0) return null;
            return s.Substring(q1 + 1, q2 - (q1 + 1));
        }
        catch
        {
            return null;
        }
    }
}
