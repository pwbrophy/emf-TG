// RobotWebSocketServer.cs - WebSocket host for ESP32 robots (text control + binary JPEG frames)
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
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

    // Robots currently streaming camera video (maintained by SendStreamOn/Off)
    private readonly HashSet<string> _activeStreams = new HashSet<string>();

    private readonly Dictionary<string, SessionInfo> _bySession = new Dictionary<string, SessionInfo>();
    private readonly Dictionary<string, string> _sessionByRobot = new Dictionary<string, string>();

    // Main-thread queue for Unity safety
    private readonly Queue<Action> _main = new Queue<Action>();
    private readonly object _mtx = new object();

    private float _nextSweepTime = 0f;

    // Fired when a robot replies to a ping.  Arg: robotId.
    public event Action<string> OnPong;

    // Handshake IR protocol events.
    public event Action<string>       OnIrEmitAck;      // shooter acknowledged ir_emit_left/right
    public event Action<string, byte> OnIrWindowResult; // enemy finished a listen window; byte = hit mask

    // Fired when a robot scans an RFID tag.
    // Args: robotId, uid
    public event Action<string, string> OnRfidTag;


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

                _activeStreams.Remove(rid);

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
            // Accept registrations in Lobby (new robot joining) or Playing (mid-game
            // reconnect after a brief Wi-Fi drop). Reject in MainMenu and Ended so
            // robots can't join a finished match or before setup has started.
            var phase = _flow?.Phase;
            if (phase != GamePhase.Lobby && phase != GamePhase.Playing)
            {
                Debug.LogWarning($"[WS] hello rejected — wrong phase ({phase}): {ExtractString(json, "id")}");
                return;
            }

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

            string helloIp   = ExtractString(json, "ip")   ?? "";
            string helloName = ExtractString(json, "name") ?? "";
            // Use the robot's saved name if it sent one; otherwise fall back to the id
            // so RobotDirectory can apply its own saved name or generate a generic one.
            string callsign = string.IsNullOrWhiteSpace(helloName) ? id : helloName;
            _dir?.Upsert(id, callsign, ip: helloIp);

            bool hflip = ExtractInt(json, "hflip") != 0;
            bool vflip = ExtractInt(json, "vflip") != 0;
            _dir?.SetFlip(id, hflip, vflip);

            bool invThrottle = ExtractInt(json, "inv_throttle") != 0;
            bool invSteer    = ExtractInt(json, "inv_steer")    != 0;
            bool invTurret   = ExtractInt(json, "inv_turret")   != 0;
            _dir?.SetDriveConfig(id, invThrottle, invSteer, invTurret);

            var gs = ServiceLocator.GameSettings;
            if (gs != null)
            {
                SendPhysics(id, gs.DriveAcceleration, gs.DriveDeceleration,
                            gs.TurretAcceleration, gs.TurretDeceleration);
                SendBuzzerEnabled(id, gs.BuzzerEnabled);
            }

            if (VerboseJoins) Debug.Log("[WS] Robot hello: " + id);

            // Mid-game reconnect: restore robot state and notify phone player.
            if (_flow?.Phase == GamePhase.Playing)
            {
                var gameState = ServiceLocator.Game?.State;
                bool isDead        = gameState != null && gameState.DeadRobots.Contains(id);
                bool isRespawning  = gameState != null && gameState.RespawningRobots.Contains(id);

                // Always restart the camera stream so the phone gets video back.
                SendStreamOn(id);

                // Motors only if the robot is not in the death / dead-walk state.
                if (!isDead && !isRespawning)
                    SendMotorsOn(id);

                // Restore the HP LED bar.
                if (gameState != null && gs != null)
                {
                    int hp = gameState.RobotHp.TryGetValue(id, out int v) ? v : gs.MaxHp;
                    SendSetHp(id, hp, gs.MaxHp);
                }

                // Tell the assigned phone player their robot is back (sends game_started
                // with the robot's current IP so the video URL is refreshed).
                ServiceLocator.PlayerServer?.NotifyRobotRejoined(id);
            }

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

        if (cmd == "ir_emit_ack")
        {
            if (!_bySession.TryGetValue(sid, out var info)) return;
            string robotId = info.RobotId;
            if (string.IsNullOrEmpty(robotId)) return;
            Debug.Log($"[WS<-Robot] ir_emit_ack -> {robotId}");
            OnIrEmitAck?.Invoke(robotId);
            return;
        }

        if (cmd == "ir_window_result")
        {
            if (!_bySession.TryGetValue(sid, out var info)) return;
            string robotId = info.RobotId;
            if (string.IsNullOrEmpty(robotId)) return;
            byte mask = (byte)ExtractInt(json, "mask");
            Debug.Log($"[WS<-Robot] ir_window_result mask=0x{mask:X2} -> {robotId}");
            OnIrWindowResult?.Invoke(robotId, mask);
            return;
        }

        if (cmd == "rfid")
        {
            if (!_bySession.TryGetValue(sid, out var info)) return;
            string robotId = info.RobotId;
            if (string.IsNullOrEmpty(robotId)) return;
            string uid = ExtractString(json, "uid");
            if (string.IsNullOrEmpty(uid)) return;
            Debug.Log($"[WS<-Robot] rfid uid={uid} from {robotId}");
            OnRfidTag?.Invoke(robotId, uid);
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
        _activeStreams.Remove(robotId);
        return SendJsonToRobot(robotId, "{\"cmd\":\"stream_off\"}");
    }

    public bool SendStreamOn(string robotId)
    {
        _activeStreams.Add(robotId);
        return SendJsonToRobot(robotId, "{\"cmd\":\"stream_on\"}");
    }

    // Pauses all active camera streams; returns the set of robot IDs that were streaming.
    HashSet<string> PauseAllStreams()
    {
        var was = new HashSet<string>(_activeStreams);
        _activeStreams.Clear();
        foreach (var id in was) SendJsonToRobot(id, "{\"cmd\":\"stream_off\"}");
        return was;
    }

    void RestoreStreams(HashSet<string> toRestore)
    {
        foreach (var id in toRestore) SendStreamOn(id);
    }

    public bool SendDriveConfig(string robotId, bool invThrottle, bool invSteer, bool invTurret)
    {
        if (string.IsNullOrEmpty(robotId)) return false;
        string json = $"{{\"cmd\":\"set_drive_config\",\"inv_throttle\":{(invThrottle ? 1 : 0)}" +
                      $",\"inv_steer\":{(invSteer ? 1 : 0)},\"inv_turret\":{(invTurret ? 1 : 0)}}}";
        bool ok = SendJsonToRobot(robotId, json);
        Debug.Log(ok
            ? $"[WS->Robot] set_drive_config th={invThrottle} st={invSteer} tu={invTurret} -> {robotId}"
            : $"[WS->Robot] FAILED set_drive_config -> {robotId}");
        return ok;
    }

    public bool SendSetName(string robotId, string name)
    {
        if (string.IsNullOrEmpty(robotId) || string.IsNullOrEmpty(name)) return false;
        string escaped = name.Replace("\\", "\\\\").Replace("\"", "\\\"");
        string json = "{\"cmd\":\"set_name\",\"name\":\"" + escaped + "\"}";
        bool ok = SendJsonToRobot(robotId, json);
        Debug.Log(ok
            ? $"[WS->Robot] set_name '{name}' -> {robotId}"
            : $"[WS->Robot] FAILED set_name -> {robotId}");
        return ok;
    }

    public bool SendVideoFlip(string robotId, bool hflip, bool vflip)
    {
        if (string.IsNullOrEmpty(robotId)) return false;
        string json = "{\"cmd\":\"set_video_flip\",\"h\":" + (hflip ? 1 : 0) +
                      ",\"v\":" + (vflip ? 1 : 0) + "}";
        bool ok = SendJsonToRobot(robotId, json);
        Debug.Log(ok
            ? $"[WS->Robot] set_video_flip h={hflip} v={vflip} -> {robotId}"
            : $"[WS->Robot] FAILED set_video_flip -> {robotId}");
        return ok;
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

    public bool SendPhysics(string robotId, float driveAccel, float driveDecel,
                            float turretAccel, float turretDecel)
    {
        if (string.IsNullOrEmpty(robotId)) return false;
        string da = driveAccel.ToString("F3",  CultureInfo.InvariantCulture);
        string dd = driveDecel.ToString("F3",  CultureInfo.InvariantCulture);
        string ta = turretAccel.ToString("F3", CultureInfo.InvariantCulture);
        string td = turretDecel.ToString("F3", CultureInfo.InvariantCulture);
        string json = $"{{\"cmd\":\"set_physics\",\"drive_accel\":{da},\"drive_decel\":{dd}," +
                      $"\"turret_accel\":{ta},\"turret_decel\":{td}}}";
        return SendJsonToRobot(robotId, json);
    }

    public void BroadcastPhysicsToAll(GameSettings settings)
    {
        if (settings == null) return;
        foreach (var robotId in _sessionByRobot.Keys.ToList())
            SendPhysics(robotId, settings.DriveAcceleration, settings.DriveDeceleration,
                        settings.TurretAcceleration, settings.TurretDeceleration);
    }

    public bool SendBuzzerEnabled(string robotId, bool enabled)
    {
        if (string.IsNullOrEmpty(robotId)) return false;
        string json = $"{{\"cmd\":\"set_buzzer\",\"enabled\":{(enabled ? 1 : 0)}}}";
        bool ok = SendJsonToRobot(robotId, json);
        Debug.Log(ok
            ? $"[WS->Robot] set_buzzer enabled={enabled} -> {robotId}"
            : $"[WS->Robot] FAILED set_buzzer -> {robotId}");
        return ok;
    }

    public void BroadcastBuzzerToAll(bool enabled)
    {
        foreach (var robotId in _sessionByRobot.Keys.ToList())
            SendBuzzerEnabled(robotId, enabled);
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


    // ===== Handshake IR commands =====

    public bool SendIrEmitLeft(string robotId)
    {
        if (string.IsNullOrEmpty(robotId)) return false;
        bool ok = SendJsonToRobot(robotId, "{\"cmd\":\"ir_emit_left\"}");
        Debug.Log(ok ? $"[WS->Robot] ir_emit_left -> {robotId}" : $"[WS->Robot] FAILED ir_emit_left -> {robotId}");
        return ok;
    }

    public bool SendIrEmitRight(string robotId)
    {
        if (string.IsNullOrEmpty(robotId)) return false;
        bool ok = SendJsonToRobot(robotId, "{\"cmd\":\"ir_emit_right\"}");
        Debug.Log(ok ? $"[WS->Robot] ir_emit_right -> {robotId}" : $"[WS->Robot] FAILED ir_emit_right -> {robotId}");
        return ok;
    }

    public bool SendIrListenWindow(string robotId, int ms)
    {
        if (string.IsNullOrEmpty(robotId)) return false;
        string json = $"{{\"cmd\":\"ir_listen_window\",\"ms\":{ms}}}";
        bool ok = SendJsonToRobot(robotId, json);
        Debug.Log(ok ? $"[WS->Robot] ir_listen_window ms={ms} -> {robotId}" : $"[WS->Robot] FAILED ir_listen_window -> {robotId}");
        return ok;
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

    public bool SendFlashHeal(string robotId)
    {
        if (string.IsNullOrEmpty(robotId)) return false;
        bool ok = SendJsonToRobot(robotId, "{\"cmd\":\"flash_heal\"}");
        Debug.Log(ok ? $"[WS->Robot] flash_heal -> {robotId}" : $"[WS->Robot] FAILED flash_heal -> {robotId}");
        return ok;
    }

    public bool SendFlashCapture(string robotId)
    {
        if (string.IsNullOrEmpty(robotId)) return false;
        bool ok = SendJsonToRobot(robotId, "{\"cmd\":\"flash_capture\"}");
        Debug.Log(ok ? $"[WS->Robot] flash_capture -> {robotId}" : $"[WS->Robot] FAILED flash_capture -> {robotId}");
        return ok;
    }

    public bool SendFlashDeath(string robotId)
    {
        if (string.IsNullOrEmpty(robotId)) return false;
        bool ok = SendJsonToRobot(robotId, "{\"cmd\":\"flash_death\"}");
        Debug.Log(ok ? $"[WS->Robot] flash_death -> {robotId}" : $"[WS->Robot] FAILED flash_death -> {robotId}");
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

    public bool SendCountdownTick(string robotId, int count, int total)
    {
        if (string.IsNullOrEmpty(robotId)) return false;
        string json = $"{{\"cmd\":\"countdown_tick\",\"count\":{count},\"total\":{total}}}";
        return SendJsonToRobot(robotId, json);
    }

    public bool SendGameStartFanfare(string robotId)
    {
        if (string.IsNullOrEmpty(robotId)) return false;
        return SendJsonToRobot(robotId, "{\"cmd\":\"game_start_fanfare\"}");
    }

    // ===== Internals =====

    private WebSocketSharp.Server.WebSocketSessionManager ServiceSessions()
    {
        if (_wss == null) return null;
        var svcHost = _wss.WebSocketServices[Path];
        return svcHost?.Sessions;
    }

    private static int ExtractInt(string s, string key)
    {
        if (string.IsNullOrEmpty(s) || string.IsNullOrEmpty(key)) return 0;
        try
        {
            int k = s.IndexOf("\"" + key + "\"", StringComparison.Ordinal);
            if (k < 0) return 0;
            int colon = s.IndexOf(':', k);
            if (colon < 0) return 0;
            int i = colon + 1;
            while (i < s.Length && (s[i] == ' ' || s[i] == '\t')) i++;
            int start = i;
            if (i < s.Length && s[i] == '-') i++;
            while (i < s.Length && char.IsDigit(s[i])) i++;
            if (i == start) return 0;
            if (int.TryParse(s.Substring(start, i - start), out int val)) return val;
            return 0;
        }
        catch { return 0; }
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
