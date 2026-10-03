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

    // Shared secret robots must include as "tok" in hello. Loaded from ws_token.txt
    // (Unity project root, gitignored — must match WS_HELLO_TOKEN in the firmware's
    // secrets.h). Port 8080 is open to the whole venue LAN; without this, anyone can
    // register fake robots or spoof ir_window_result. Empty = gate disabled.
    private string _helloToken = "";

    // Robots only send small flat JSON; anything larger is a stranger poking the
    // open port. Cap before parsing so a junk flood can't burn CPU or memory.
    private const int MaxInboundTextChars = 4096;

    // Per-session data
    private class SessionInfo
    {
        public string RobotId;
        public float LastSeenTime;
        public int NumFrames;
    }

    // ── Health log ── one line per minute so a long event day leaves a record of
    // whether Unity, the robots' memory or their connections degraded over time.
    [Header("Health log")]
    public float HealthLogIntervalSeconds = 60f;
    // A single frame longer than this is logged straight away as a main-thread stall.
    public float StallWarnSeconds = 0.5f;

    private class RobotHealth
    {
        public int  Heap = -1;        // free internal heap from the latest hb (bytes)
        public int  HeapMin = -1;     // lowest free heap seen this interval
        public int  MaxBlock = -1;    // largest free internal block (fragmentation), newer firmware only
        public int  Hellos;           // hello count this interval (> 1 = reconnecting)
        public bool Seen;             // sent hb or hello this interval
    }
    private readonly Dictionary<string, RobotHealth> _health = new Dictionary<string, RobotHealth>();
    private float _healthNextLog;
    private float _frameTimeSum, _frameTimeMax;
    private int   _frameCount;

    RobotHealth HealthFor(string robotId)
    {
        if (!_health.TryGetValue(robotId, out var h)) _health[robotId] = h = new RobotHealth();
        return h;
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
        if (_flow != null) _flow.OnPhaseChanged -= OnPhaseChanged;
        if (_self == this) _self = null;
        if (ServiceLocator.RobotServer == this) ServiceLocator.RobotServer = null;
        StopServer();
    }

    private void OnPhaseChanged(GamePhase phase)
    {
        if (phase == GamePhase.Ended)
            BroadcastResetIdleToAll();
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

        LoadHelloToken();

        string ip = PetersUtils.GetLocalIPAddress().ToString();

        // Bind to all interfaces (0.0.0.0) so robots on any subnet can connect.
        // Binding to a specific IP in WebSocketSharp can silently reject connections
        // that arrive on a different adapter or after a DHCP renewal.
        Debug.Log("[WS] Starting server on 0.0.0.0:" + Port + Path + "  (LAN IP: " + ip + ")");

        _wss = new WebSocketServer(Port);
        // websocket-sharp's own "keep clean" sweep pings every session once a minute
        // and drops any that don't pong within 1 s. On congested event Wi-Fi a healthy
        // robot can miss that, get kicked, and restart its camera. Our own
        // heartbeat sweep (TimeoutSeconds) already removes genuinely dead sessions.
        _wss.KeepClean = false;

        var parent = this;
        _wss.AddWebSocketService<ESP32Service>(Path, () => new ESP32Service { Parent = parent });

        _wss.Start();
        Debug.Log("[WS] Started");

        _nextSweepTime = Time.time + SweepIntervalSeconds;
        serverStarted = true;

        ServiceLocator.RobotServer = this;

        _flow.OnPhaseChanged += OnPhaseChanged;
    }

    // Loads the shared hello token from ws_token.txt in the Unity project root
    // (next to Assets/, gitignored — must match WS_HELLO_TOKEN in firmware secrets.h).
    // Missing file = token gate disabled with a warning, so a fresh clone still
    // works against un-tokened firmware.
    private void LoadHelloToken()
    {
        try
        {
            string tokenPath = System.IO.Path.Combine(Application.dataPath, "..", "ws_token.txt");
            if (System.IO.File.Exists(tokenPath))
            {
                _helloToken = System.IO.File.ReadAllText(tokenPath).Trim();
                Debug.Log("[WS] hello token loaded — robot registration is gated");
            }
            else
            {
                _helloToken = "";
                Debug.LogWarning("[WS] ws_token.txt not found next to Assets/ — accepting ANY robot hello (no auth). " +
                                 "Create it containing the WS_HELLO_TOKEN value from firmware secrets.h.");
            }
        }
        catch (Exception ex)
        {
            _helloToken = "";
            Debug.LogWarning("[WS] failed to load ws_token.txt: " + ex.Message);
        }
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

        UpdateHealthLog();
    }

    private void UpdateHealthLog()
    {
        // unscaledDeltaTime is the real wall-clock gap (Time.deltaTime is capped by
        // maximumDeltaTime, so it would hide exactly the long stalls we want to see).
        float dt = Time.unscaledDeltaTime;
        _frameTimeSum += dt;
        _frameCount++;
        if (dt > _frameTimeMax) _frameTimeMax = dt;
        if (dt > StallWarnSeconds && Time.frameCount > 10)
            Debug.LogWarning($"[Health] main thread stalled for {dt:F1} s");

        float now = Time.realtimeSinceStartup;
        if (_healthNextLog == 0f) _healthNextLog = now + HealthLogIntervalSeconds;
        if (now < _healthNextLog) return;
        _healthNextLog = now + HealthLogIntervalSeconds;

        var sb = new System.Text.StringBuilder();
        float avgMs = _frameCount > 0 ? _frameTimeSum / _frameCount * 1000f : 0f;
        sb.Append($"[Health] frame avg={avgMs:F1}ms worst={_frameTimeMax * 1000f:F0}ms");
        sb.Append($" | managed heap={UnityEngine.Profiling.Profiler.GetMonoUsedSizeLong() / (1024 * 1024)}MB");
        sb.Append($" | robots connected={_sessionByRobot.Count}");
        var gone = new List<string>();
        foreach (var kv in _health)
        {
            var h = kv.Value;
            // A robot silent for a whole interval has gone (switched off / timed
            // out): drop it rather than repeating its last numbers forever.
            if (!h.Seen) { gone.Add(kv.Key); continue; }
            h.Seen = false;
            sb.Append($" | {RobotLabel(kv.Key)} heap={h.Heap / 1024}k min={h.HeapMin / 1024}k");
            if (h.MaxBlock >= 0) sb.Append($" blk={h.MaxBlock / 1024}k");
            if (h.Hellos > 0) sb.Append($" hellos={h.Hellos}");
            h.HeapMin = h.Heap;
            h.Hellos  = 0;
        }
        foreach (var id in gone) _health.Remove(id);
        var beacons = ServiceLocator.BeaconServer;
        if (beacons != null) sb.Append(" | ").Append(beacons.TakeHealthSummary());
        Debug.Log(sb.ToString());

        _frameTimeSum = 0f; _frameTimeMax = 0f; _frameCount = 0;
    }

    // Successful sends are only logged when NetLog.Verbose; failures always are.
    static void LogSend(bool ok, string success, string failure)
    {
        if (ok) NetLog.Log(success);
        else    Debug.Log(failure);
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
            PetersUtils.DisableNagle(Context); // drive/turret must not wait on the robot's delayed ACKs
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
        if (json.Length > MaxInboundTextChars)
        {
            Debug.LogWarning($"[WS] dropped oversized message ({json.Length} chars) from session {sid}");
            return;
        }

        string cmd = ExtractString(json, "cmd");
        if (string.IsNullOrEmpty(cmd)) return;

        if (cmd == "hello")
        {
            if (!string.IsNullOrEmpty(_helloToken))
            {
                string tok = ExtractString(json, "tok");
                if (tok != _helloToken)
                {
                    Debug.LogWarning($"[WS] hello rejected — missing/bad token from session {sid} (id={ExtractString(json, "id")})");
                    return;
                }
            }

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
            var hh = HealthFor(id);
            hh.Hellos++;
            hh.Seen = true;

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
                SendVideoConfig(id, gs.VideoFps, gs.VideoFrameSize, gs.VideoJpegQuality);
            }

            if (VerboseJoins) Debug.Log("[WS] Robot hello: " + id);

            // RFID reader health — logged even with VerboseJoins off so a dead reader is
            // visible. Older firmware doesn't send the field; stay quiet for those robots.
            if (json.Contains("\"rfid\":"))
            {
                int rfidRecoveries = ExtractInt(json, "rfid_recoveries");
                if (ExtractInt(json, "rfid") == 0)
                    Debug.LogWarning($"[RFID] {RobotLabel(id)}: reader not responding");
                else if (rfidRecoveries > 0)
                    Debug.LogWarning($"[RFID] {RobotLabel(id)}: reader has recovered {rfidRecoveries}x since boot");
            }

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
            else if (_flow?.Phase == GamePhase.Lobby)
            {
                // Reset to idle bounce — clears any leftover HP bar / death animation
                // from a previous game so the white dot animation plays while waiting.
                SendResetIdle(id);
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

                if (!string.IsNullOrEmpty(info.RobotId) && json.Contains("\"heap\":"))
                {
                    var h = HealthFor(info.RobotId);
                    h.Seen = true;
                    h.Heap = ExtractInt(json, "heap");
                    if (h.HeapMin < 0 || h.Heap < h.HeapMin) h.HeapMin = h.Heap;
                    if (json.Contains("\"maxblk\":")) h.MaxBlock = ExtractInt(json, "maxblk");
                }
            }
            return;
        }

        if (cmd == "pong")
        {
            if (!_bySession.TryGetValue(sid, out var pongInfo)) return;
            string pongRobotId = pongInfo.RobotId;
            if (string.IsNullOrEmpty(pongRobotId)) return;
            NetLog.Log($"[WS<-Robot] pong from {pongRobotId}");
            OnPong?.Invoke(pongRobotId);
            return;
        }

        if (cmd == "ir_emit_ack")
        {
            if (!_bySession.TryGetValue(sid, out var info)) return;
            string robotId = info.RobotId;
            if (string.IsNullOrEmpty(robotId)) return;
            NetLog.Log($"[WS<-Robot] ir_emit_ack -> {robotId}");
            OnIrEmitAck?.Invoke(robotId);
            return;
        }

        if (cmd == "ir_window_result")
        {
            if (!_bySession.TryGetValue(sid, out var info)) return;
            string robotId = info.RobotId;
            if (string.IsNullOrEmpty(robotId)) return;
            byte mask = (byte)ExtractInt(json, "mask");
            NetLog.Log($"[WS<-Robot] ir_window_result mask=0x{mask:X2} -> {robotId}");
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

        // Firmware self-heal report: the RFID reader stopped answering, or was
        // re-initialised after a chip reset (power dip / RST glitch). Logged as warnings
        // so they stand out — repeated recoveries on one tank point at its wiring.
        if (cmd == "rfid_status")
        {
            if (!_bySession.TryGetValue(sid, out var info)) return;
            string robotId = info.RobotId;
            if (string.IsNullOrEmpty(robotId)) return;
            bool ok        = ExtractInt(json, "ok") != 0;
            int recoveries = ExtractInt(json, "recoveries");
            if (!ok)
                Debug.LogWarning($"[RFID] {RobotLabel(robotId)}: reader stopped responding");
            else if (recoveries > 0)
                Debug.LogWarning($"[RFID] {RobotLabel(robotId)}: reader re-initialised (recovery #{recoveries} since boot)");
            else
                Debug.Log($"[RFID] {RobotLabel(robotId)}: reader came up after boot");
            return;
        }
    }

    // "Desert-03 (78F218697090)" when the robot has a callsign, otherwise just the id.
    string RobotLabel(string robotId)
    {
        if (_dir != null && _dir.TryGet(robotId, out var r) &&
            !string.IsNullOrEmpty(r.Callsign) && r.Callsign != robotId)
            return $"{r.Callsign} ({robotId})";
        return robotId;
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

                CloseSessionInBackground(ServiceSessions(), sid);
                _bySession.Remove(sid);

                if (!string.IsNullOrEmpty(rid))
                {
                    if (_sessionByRobot.TryGetValue(rid, out var mapSid) && mapSid == sid)
                        _sessionByRobot.Remove(rid);

                    _activeStreams.Remove(rid);

                    // During Playing, keep the directory entry (and its player
                    // assignment) so a brief Wi-Fi drop doesn't orphan the player.
                    // Reconnect via hello refreshes the session with no dead-control
                    // window. In other phases, drop it so stale robots clear from the
                    // lobby list.
                    if (_flow?.Phase == GamePhase.Playing)
                    {
                        if (VerboseLeaves) Debug.Log("[WS] Robot session timeout (kept in directory during Playing): " + rid);
                    }
                    else
                    {
                        _dir?.Remove(rid);
                        if (VerboseLeaves) Debug.Log("[WS] Robot timeout: " + rid);
                    }
                }
            }
        }
    }

    // websocket-sharp's CloseSession sends a close frame and then waits up to 1 s for
    // the peer to answer. A timed-out robot never answers, so on the main thread each
    // one froze the whole game for a second — ten robots dropping together after a
    // Wi-Fi blip froze it for ~10 s. The session is already forgotten here (and the
    // OnClose it triggers finds nothing), so closing it off-thread is safe.
    internal static void CloseSessionInBackground(WebSocketSessionManager sessions, string sid)
    {
        if (sessions == null) return;
        System.Threading.ThreadPool.QueueUserWorkItem(_ =>
        {
            try { sessions.CloseSession(sid); } catch { /* already gone */ }
        });
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
        LogSend(ok, $"[WS->Robot] ping -> {robotId}", $"[WS->Robot] FAILED ping -> {robotId}");
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
        LogSend(ok, $"[WS->Robot] set_drive_config th={invThrottle} st={invSteer} tu={invTurret} -> {robotId}", $"[WS->Robot] FAILED set_drive_config -> {robotId}");
        return ok;
    }

    public bool SendSetName(string robotId, string name)
    {
        if (string.IsNullOrEmpty(robotId) || string.IsNullOrEmpty(name)) return false;
        string escaped = name.Replace("\\", "\\\\").Replace("\"", "\\\"");
        string json = "{\"cmd\":\"set_name\",\"name\":\"" + escaped + "\"}";
        bool ok = SendJsonToRobot(robotId, json);
        LogSend(ok, $"[WS->Robot] set_name '{name}' -> {robotId}", $"[WS->Robot] FAILED set_name -> {robotId}");
        return ok;
    }

    public bool SendVideoFlip(string robotId, bool hflip, bool vflip)
    {
        if (string.IsNullOrEmpty(robotId)) return false;
        string json = "{\"cmd\":\"set_video_flip\",\"h\":" + (hflip ? 1 : 0) +
                      ",\"v\":" + (vflip ? 1 : 0) + "}";
        bool ok = SendJsonToRobot(robotId, json);
        LogSend(ok, $"[WS->Robot] set_video_flip h={hflip} v={vflip} -> {robotId}", $"[WS->Robot] FAILED set_video_flip -> {robotId}");
        return ok;
    }

    public bool SendMotorsOn(string robotId)
    {
        bool ok = SendJsonToRobot(robotId, "{\"cmd\":\"motors_on\"}");
        LogSend(ok, $"[WS->Robot] motors_on -> {robotId}", $"[WS->Robot] FAILED motors_on -> {robotId}");
        return ok;
    }

    public bool SendMotorsOff(string robotId)
    {
        bool ok = SendJsonToRobot(robotId, "{\"cmd\":\"motors_off\"}");
        LogSend(ok, $"[WS->Robot] motors_off -> {robotId}", $"[WS->Robot] FAILED motors_off -> {robotId}");
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
        LogSend(ok, $"[WS->Robot] set_buzzer enabled={enabled} -> {robotId}", $"[WS->Robot] FAILED set_buzzer -> {robotId}");
        return ok;
    }

    public void BroadcastBuzzerToAll(bool enabled)
    {
        foreach (var robotId in _sessionByRobot.Keys.ToList())
            SendBuzzerEnabled(robotId, enabled);
    }

    public bool SendVideoConfig(string robotId, int fps, int frameSize, int quality)
    {
        if (string.IsNullOrEmpty(robotId)) return false;
        string json = $"{{\"cmd\":\"set_video\",\"fps\":{fps},\"framesize\":{frameSize},\"quality\":{quality}}}";
        bool ok = SendJsonToRobot(robotId, json);
        LogSend(ok, $"[WS->Robot] set_video fps={fps} fsize={frameSize} q={quality} -> {robotId}", $"[WS->Robot] FAILED set_video -> {robotId}");
        return ok;
    }

    public void BroadcastVideoConfigToAll(GameSettings settings)
    {
        if (settings == null) return;
        foreach (var robotId in _sessionByRobot.Keys.ToList())
            SendVideoConfig(robotId, settings.VideoFps, settings.VideoFrameSize, settings.VideoJpegQuality);
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
        LogSend(ok, $"[WS->Robot] ir_emit_stop -> {robotId}", $"[WS->Robot] FAILED ir_emit_stop -> {robotId}");
        return ok;
    }


    // ===== Handshake IR commands =====

    public bool SendIrEmitLeft(string robotId)
    {
        if (string.IsNullOrEmpty(robotId)) return false;
        bool ok = SendJsonToRobot(robotId, "{\"cmd\":\"ir_emit_left\"}");
        LogSend(ok, $"[WS->Robot] ir_emit_left -> {robotId}", $"[WS->Robot] FAILED ir_emit_left -> {robotId}");
        return ok;
    }

    public bool SendIrEmitRight(string robotId)
    {
        if (string.IsNullOrEmpty(robotId)) return false;
        bool ok = SendJsonToRobot(robotId, "{\"cmd\":\"ir_emit_right\"}");
        LogSend(ok, $"[WS->Robot] ir_emit_right -> {robotId}", $"[WS->Robot] FAILED ir_emit_right -> {robotId}");
        return ok;
    }

    public bool SendIrListenWindow(string robotId, int ms)
    {
        if (string.IsNullOrEmpty(robotId)) return false;
        string json = $"{{\"cmd\":\"ir_listen_window\",\"ms\":{ms}}}";
        bool ok = SendJsonToRobot(robotId, json);
        LogSend(ok, $"[WS->Robot] ir_listen_window ms={ms} -> {robotId}", $"[WS->Robot] FAILED ir_listen_window -> {robotId}");
        return ok;
    }

    // ===== LED / buzzer feedback commands =====

    public bool SendFlashFire(string robotId)
    {
        if (string.IsNullOrEmpty(robotId)) return false;
        bool ok = SendJsonToRobot(robotId, "{\"cmd\":\"flash_fire\"}");
        LogSend(ok, $"[WS->Robot] flash_fire -> {robotId}", $"[WS->Robot] FAILED flash_fire -> {robotId}");
        return ok;
    }

    public bool SendFlashHit(string robotId, bool isRear = false)
    {
        if (string.IsNullOrEmpty(robotId)) return false;
        string json = isRear ? "{\"cmd\":\"flash_hit\",\"rear\":true}" : "{\"cmd\":\"flash_hit\"}";
        bool ok = SendJsonToRobot(robotId, json);
        LogSend(ok, $"[WS->Robot] flash_hit{(isRear ? " (rear)" : "")} -> {robotId}", $"[WS->Robot] FAILED flash_hit -> {robotId}");
        return ok;
    }

    public bool SendFlashHeal(string robotId)
    {
        if (string.IsNullOrEmpty(robotId)) return false;
        bool ok = SendJsonToRobot(robotId, "{\"cmd\":\"flash_heal\"}");
        LogSend(ok, $"[WS->Robot] flash_heal -> {robotId}", $"[WS->Robot] FAILED flash_heal -> {robotId}");
        return ok;
    }

    public bool SendFlashCapture(string robotId)
    {
        if (string.IsNullOrEmpty(robotId)) return false;
        bool ok = SendJsonToRobot(robotId, "{\"cmd\":\"flash_capture\"}");
        LogSend(ok, $"[WS->Robot] flash_capture -> {robotId}", $"[WS->Robot] FAILED flash_capture -> {robotId}");
        return ok;
    }

    public bool SendFlashDeath(string robotId)
    {
        if (string.IsNullOrEmpty(robotId)) return false;
        bool ok = SendJsonToRobot(robotId, "{\"cmd\":\"flash_death\"}");
        LogSend(ok, $"[WS->Robot] flash_death -> {robotId}", $"[WS->Robot] FAILED flash_death -> {robotId}");
        return ok;
    }

    public bool SendInvulnStart(string robotId)
    {
        if (string.IsNullOrEmpty(robotId)) return false;
        bool ok = SendJsonToRobot(robotId, "{\"cmd\":\"invuln_start\"}");
        LogSend(ok, $"[WS->Robot] invuln_start -> {robotId}", $"[WS->Robot] FAILED invuln_start -> {robotId}");
        return ok;
    }

    public bool SendInvulnEnd(string robotId)
    {
        if (string.IsNullOrEmpty(robotId)) return false;
        bool ok = SendJsonToRobot(robotId, "{\"cmd\":\"invuln_end\"}");
        LogSend(ok, $"[WS->Robot] invuln_end -> {robotId}", $"[WS->Robot] FAILED invuln_end -> {robotId}");
        return ok;
    }

    public bool SendSetHp(string robotId, int hp, int maxHp)
    {
        if (string.IsNullOrEmpty(robotId)) return false;
        string json = $"{{\"cmd\":\"set_hp\",\"hp\":{hp},\"max\":{maxHp}}}";
        bool ok = SendJsonToRobot(robotId, json);
        LogSend(ok, $"[WS->Robot] set_hp hp={hp}/{maxHp} -> {robotId}", $"[WS->Robot] FAILED set_hp -> {robotId}");
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

    public bool SendResetIdle(string robotId)
    {
        if (string.IsNullOrEmpty(robotId)) return false;
        bool ok = SendJsonToRobot(robotId, "{\"cmd\":\"reset_idle\"}");
        LogSend(ok, $"[WS->Robot] reset_idle -> {robotId}", $"[WS->Robot] FAILED reset_idle -> {robotId}");
        return ok;
    }

    public void BroadcastResetIdleToAll()
    {
        foreach (var robotId in _sessionByRobot.Keys.ToList())
            SendResetIdle(robotId);
    }

    public bool SendPlayerColor(string robotId, byte r, byte g, byte b)
    {
        if (string.IsNullOrEmpty(robotId)) return false;
        string json = $"{{\"cmd\":\"set_player_color\",\"r\":{r},\"g\":{g},\"b\":{b}}}";
        bool ok = SendJsonToRobot(robotId, json);
        LogSend(ok, $"[WS->Robot] set_player_color r={r} g={g} b={b} -> {robotId}", $"[WS->Robot] FAILED set_player_color -> {robotId}");
        return ok;
    }

    public bool SendClearPlayerColor(string robotId)
    {
        if (string.IsNullOrEmpty(robotId)) return false;
        bool ok = SendJsonToRobot(robotId, "{\"cmd\":\"clear_player_color\"}");
        LogSend(ok, $"[WS->Robot] clear_player_color -> {robotId}", $"[WS->Robot] FAILED clear_player_color -> {robotId}");
        return ok;
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
