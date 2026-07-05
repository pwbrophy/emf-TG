// CapturePointBeaconServer.cs - WebSocket host for the 3 physical capture-point
// beacon devices (North/Centre/South). Mirrors RobotWebSocketServer's session/
// heartbeat/main-thread-queue pattern, but on its own port since beacons are a
// separate device class from the robots (fixed 3 points, no camera/motors).
using System;
using System.Collections.Generic;
using UnityEngine;
using WebSocketSharp;
using WebSocketSharp.Server;

public class CapturePointBeaconServer : MonoBehaviour
{
    [Header("WebSocket Host Settings")]
    public int Port = 8082;
    public string Path = "/beacon";

    [Header("Timeouts")]
    public float TimeoutSeconds = 8f;
    public float SweepIntervalSeconds = 2f;

    [Header("Debug")]
    public bool VerboseJoins = true;
    public bool VerboseLeaves = true;

    private bool serverStarted = false;
    private WebSocketServer _wss;
    private CapturePointBeaconDirectory _dir;
    private CapturePointService _cp;
    private GameFlow _flow;

    private class SessionInfo
    {
        public int   PointIndex = -1;
        public float LastSeenTime;
    }

    private readonly Dictionary<string, SessionInfo> _bySession = new Dictionary<string, SessionInfo>();
    private readonly Dictionary<int, string> _sessionByPoint = new Dictionary<int, string>();

    private readonly Queue<Action> _main = new Queue<Action>();
    private readonly object _mtx = new object();

    private float _nextSweepTime = 0f;

    // Mirrors display.html's round-robin per-tick flash selection exactly
    // (same algorithm, same reset-on-Playing timing) so the physical beacon
    // and the spectator display flash the same capture point in sync.
    private readonly int[] _cpFlashCounter = new int[2];
    private readonly int[] _prevTeamPoints = new int[2];

    private void Awake()
    {
        ServiceLocator.BeaconServer = this;
    }

    private void Start()
    {
        StartWebSocketServer();
    }

    private void OnDestroy()
    {
        if (_cp != null)
        {
            _cp.OnPointCaptured -= OnPointCaptured;
            _cp.OnTeamPointsChanged -= OnTeamPointsChangedHandler;
        }
        if (_flow != null) _flow.OnPhaseChanged -= OnPhaseChanged;
        if (ServiceLocator.BeaconServer == this) ServiceLocator.BeaconServer = null;
        StopServer();
    }

    public void StartWebSocketServer()
    {
        if (serverStarted) return;

        _dir  = ServiceLocator.BeaconDirectory;
        _cp   = ServiceLocator.CapturePoints;
        _flow = ServiceLocator.GameFlow;

        if (_dir == null || _cp == null || _flow == null)
        {
            Debug.LogError("[BeaconWS] BeaconDirectory, CapturePointService or GameFlow is null.");
            return;
        }

        string ip = PetersUtils.GetLocalIPAddress().ToString();
        Debug.Log("[BeaconWS] Starting server on 0.0.0.0:" + Port + Path + "  (LAN IP: " + ip + ")");

        _wss = new WebSocketServer(Port);

        var parent = this;
        _wss.AddWebSocketService<BeaconService>(Path, () => new BeaconService { Parent = parent });

        _wss.Start();
        Debug.Log("[BeaconWS] Started");

        _nextSweepTime = Time.time + SweepIntervalSeconds;
        serverStarted = true;

        ServiceLocator.BeaconServer = this;

        _cp.OnPointCaptured += OnPointCaptured;
        _cp.OnTeamPointsChanged += OnTeamPointsChangedHandler;
        _flow.OnPhaseChanged += OnPhaseChanged;
    }

    public void StopServer()
    {
        if (!serverStarted) return;
        try { _wss.Stop(); } catch { /* ignore */ }
        _wss = null;
        serverStarted = false;
    }

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

    // ===== Game event handlers =====

    // Alliance 0 = Desert Squad (orange), 1 = Jungle Squad (green), -1 = neutral/off.
    private static void AllianceColor(int allianceIndex, out byte r, out byte g, out byte b)
    {
        switch (allianceIndex)
        {
            case 0:  r = 255; g = 110; b = 0;   break; // Desert — deeper orange, less yellow
            case 1:  r = 30;  g = 160; b = 15;  break; // Jungle — green, blue nearly zeroed (WS2812 blue reads strong even at low values)
            default: r = 0;   g = 0;   b = 0;   break; // neutral -> unlit
        }
    }

    // Live capture (ownership actually changed) — animate the transition.
    // A force-clear to neutral (-1) just snaps to unlit; there's nothing being "captured".
    private void OnPointCaptured(int pointIndex, int allianceIndex, string pointName)
    {
        AllianceColor(allianceIndex, out byte r, out byte g, out byte b);
        if (allianceIndex == 0 || allianceIndex == 1) SendCaptureRipple(pointIndex, r, g, b);
        else                                          SendColor(pointIndex, r, g, b);
    }

    private void OnPhaseChanged(GamePhase phase)
    {
        if (phase == GamePhase.Playing)
        {
            // Match just started — all points are neutral until captured.
            for (int i = 0; i < 3; i++) SendColor(i, 0, 0, 0);
            _cpFlashCounter[0] = _cpFlashCounter[1] = 0;
            _prevTeamPoints[0] = _prevTeamPoints[1] = 0;
        }
        else
        {
            // Lobby / MainMenu / Ended — back to the idle bounce.
            for (int i = 0; i < 3; i++) SendIdle(i);
        }
    }

    // Mirrors display.html's applyDisplayUpdate: a gain of 1-3 points is a
    // normal capture-tick (kill bonuses are configured higher and skipped),
    // and the flashed point is chosen round-robin among the team's owned
    // points — same algorithm, same counters reset at the same moment, so
    // the beacon and the display always agree on which point flashes.
    private void OnTeamPointsChangedHandler()
    {
        var gs = ServiceLocator.Game?.State;
        if (gs?.TeamPoints == null) return;

        for (int team = 0; team < gs.TeamPoints.Length && team < 2; team++)
        {
            int gained = gs.TeamPoints[team] - _prevTeamPoints[team];
            if (gained >= 1 && gained <= 3) TriggerCpFlash(team, gs);
            _prevTeamPoints[team] = gs.TeamPoints[team];
        }
    }

    private void TriggerCpFlash(int team, GameState gs)
    {
        var owned = new List<int>();
        for (int i = 0; i < gs.CapturePointOwners.Length; i++)
            if (gs.CapturePointOwners[i] == team) owned.Add(i);
        if (owned.Count == 0) return;

        int pointIndex = owned[_cpFlashCounter[team] % owned.Count];
        _cpFlashCounter[team]++;
        SendVpRipple(pointIndex);
    }

    // ===== WebSocket plumbing =====

    private class BeaconService : WebSocketBehavior
    {
        public CapturePointBeaconServer Parent;

        protected override void OnOpen()
        {
            if (Parent != null) Parent.PostMain(() => Parent.OnOpened(ID));
        }

        protected override void OnClose(CloseEventArgs e)
        {
            if (Parent != null) Parent.PostMain(() => Parent.OnClosed(ID));
        }

        protected override void OnMessage(MessageEventArgs e)
        {
            if (Parent == null || !e.IsText) return;
            string data = e.Data;
            Parent.PostMain(() => Parent.HandleText(ID, data));
        }
    }

    private void OnOpened(string sid)
    {
        if (!_bySession.ContainsKey(sid))
            _bySession[sid] = new SessionInfo { LastSeenTime = Time.time };
    }

    private void OnClosed(string sid)
    {
        if (!_bySession.TryGetValue(sid, out var info)) return;

        _bySession.Remove(sid);

        // Do NOT mark disconnected here — mirrors the robot gotcha: a beacon may
        // reconnect within a second or two, and only the heartbeat sweep should
        // decide it's actually gone.
        if (info.PointIndex >= 0)
        {
            if (_sessionByPoint.TryGetValue(info.PointIndex, out var mapSid) && mapSid == sid)
                _sessionByPoint.Remove(info.PointIndex);

            if (VerboseLeaves) Debug.Log("[BeaconWS] Beacon disconnected (session): point=" + info.PointIndex);
        }
    }

    private void HandleText(string sid, string json)
    {
        if (string.IsNullOrEmpty(json)) return;

        string cmd = ExtractString(json, "cmd");
        if (string.IsNullOrEmpty(cmd)) return;

        if (cmd == "hello")
        {
            // Beacons are always-on infrastructure, not gameplay actors — accept
            // hello at any game phase, unlike the robot's Lobby/Playing-only gate.
            int point = ExtractInt(json, "point");
            if (point < 0 || point > 2)
            {
                Debug.LogWarning("[BeaconWS] hello rejected — bad point index: " + point);
                return;
            }

            if (!_bySession.TryGetValue(sid, out var info))
            {
                info = new SessionInfo();
                _bySession[sid] = info;
            }
            info.PointIndex   = point;
            info.LastSeenTime = Time.time;
            _sessionByPoint[point] = sid;

            string id = ExtractString(json, "id") ?? "";
            string ip = ExtractString(json, "ip") ?? "";
            _dir?.Upsert(point, id, ip, Time.time);

            if (VerboseJoins) Debug.Log("[BeaconWS] Beacon hello: point=" + point + " id=" + id);

            // Resync the beacon to whatever it should currently be showing, in
            // case it reconnected mid-match after a brief Wi-Fi drop.
            ResyncBeacon(point);
            return;
        }

        if (cmd == "hb")
        {
            if (_bySession.TryGetValue(sid, out var info) && info.PointIndex >= 0)
            {
                info.LastSeenTime = Time.time;
                _dir?.Upsert(info.PointIndex, null, null, Time.time);
            }
            return;
        }
    }

    // A reconnecting beacon should snap straight to the correct state, not
    // replay the capture-ripple animation — so this calls SendColor directly
    // rather than going through OnPointCaptured.
    private void ResyncBeacon(int pointIndex)
    {
        if (_flow?.Phase == GamePhase.Playing)
        {
            var gs = ServiceLocator.Game?.State;
            int owner = (gs != null && pointIndex < gs.CapturePointOwners.Length)
                ? gs.CapturePointOwners[pointIndex] : -1;
            AllianceColor(owner, out byte r, out byte g, out byte b);
            SendColor(pointIndex, r, g, b);
        }
        else
        {
            SendIdle(pointIndex);
        }
    }

    private void SweepForTimeouts()
    {
        if (_bySession.Count == 0) return;

        var now = Time.time;
        var toDrop = new List<string>();

        foreach (var kv in _bySession)
            if (now - kv.Value.LastSeenTime > TimeoutSeconds)
                toDrop.Add(kv.Key);

        foreach (var sid in toDrop)
        {
            if (!_bySession.TryGetValue(sid, out var info)) continue;

            try { ServiceSessions()?.CloseSession(sid); } catch { /* ignore */ }
            _bySession.Remove(sid);

            if (info.PointIndex >= 0)
            {
                if (_sessionByPoint.TryGetValue(info.PointIndex, out var mapSid) && mapSid == sid)
                    _sessionByPoint.Remove(info.PointIndex);

                _dir?.MarkDisconnected(info.PointIndex);
                if (VerboseLeaves) Debug.Log("[BeaconWS] Beacon timeout: point=" + info.PointIndex);
            }
        }
    }

    // ===== Public send helpers =====

    public bool SendJsonToBeacon(int pointIndex, string json)
    {
        if (_wss == null) return false;
        if (!_sessionByPoint.TryGetValue(pointIndex, out var sid)) return false;

        var sessions = ServiceSessions();
        if (sessions == null) return false;

        try { sessions.SendTo(json, sid); }
        catch { return false; }

        return true;
    }

    public bool SendColor(int pointIndex, byte r, byte g, byte b)
    {
        string json = $"{{\"cmd\":\"set_color\",\"r\":{r},\"g\":{g},\"b\":{b}}}";
        bool ok = SendJsonToBeacon(pointIndex, json);
        Debug.Log(ok
            ? $"[BeaconWS] set_color r={r} g={g} b={b} -> point {pointIndex}"
            : $"[BeaconWS] FAILED set_color -> point {pointIndex} (not connected)");
        return ok;
    }

    public bool SendIdle(int pointIndex)
    {
        bool ok = SendJsonToBeacon(pointIndex, "{\"cmd\":\"beacon_idle\"}");
        Debug.Log(ok
            ? $"[BeaconWS] beacon_idle -> point {pointIndex}"
            : $"[BeaconWS] FAILED beacon_idle -> point {pointIndex} (not connected)");
        return ok;
    }

    // ~1s animated capture transition (white ripples closing in, settling into the colour).
    public bool SendCaptureRipple(int pointIndex, byte r, byte g, byte b)
    {
        string json = $"{{\"cmd\":\"capture_ripple\",\"r\":{r},\"g\":{g},\"b\":{b}}}";
        bool ok = SendJsonToBeacon(pointIndex, json);
        Debug.Log(ok
            ? $"[BeaconWS] capture_ripple r={r} g={g} b={b} -> point {pointIndex}"
            : $"[BeaconWS] FAILED capture_ripple -> point {pointIndex} (not connected)");
        return ok;
    }

    // Brief single white ripple mirroring the spectator display's score-tick flash.
    public bool SendVpRipple(int pointIndex)
    {
        bool ok = SendJsonToBeacon(pointIndex, "{\"cmd\":\"vp_ripple\"}");
        Debug.Log(ok
            ? $"[BeaconWS] vp_ripple -> point {pointIndex}"
            : $"[BeaconWS] FAILED vp_ripple -> point {pointIndex} (not connected)");
        return ok;
    }

    // ===== Internals =====

    private WebSocketSessionManager ServiceSessions()
    {
        if (_wss == null) return null;
        var svcHost = _wss.WebSocketServices[Path];
        return svcHost?.Sessions;
    }

    private static int ExtractInt(string s, string key)
    {
        if (string.IsNullOrEmpty(s) || string.IsNullOrEmpty(key)) return -1;
        try
        {
            int k = s.IndexOf("\"" + key + "\"", StringComparison.Ordinal);
            if (k < 0) return -1;
            int colon = s.IndexOf(':', k);
            if (colon < 0) return -1;
            int i = colon + 1;
            while (i < s.Length && (s[i] == ' ' || s[i] == '\t')) i++;
            int start = i;
            if (i < s.Length && s[i] == '-') i++;
            while (i < s.Length && char.IsDigit(s[i])) i++;
            if (i == start) return -1;
            if (int.TryParse(s.Substring(start, i - start), out int val)) return val;
            return -1;
        }
        catch { return -1; }
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
