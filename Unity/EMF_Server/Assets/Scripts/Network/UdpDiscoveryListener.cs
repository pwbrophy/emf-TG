// UdpDiscoveryListener.cs
// Listens for robot UDP announces while in Lobby, replies with the WebSocket URL.
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;

public class UdpDiscoveryListener : MonoBehaviour
{
    [Header("Ports and paths")]
    [SerializeField] private int discoveryPort = 30560;
    [SerializeField] private int websocketPort = 8080;
    [SerializeField] private string websocketPath = "/esp32";

    private IRobotDirectory _dir;
    private GameFlow _flow;

    private UdpClient _udp;
    private Thread _rxThread;
    private volatile bool _running;

    // Robots we've already logged a reply for this discovery session — avoids log spam
    // on the 2-second broadcast cadence.  Cleared when listener restarts.
    private readonly HashSet<string> _repliedTo = new HashSet<string>();

    private readonly object _mtx = new object();
    private readonly Queue<Action> _main = new Queue<Action>();

    private void Start()
    {
        StartUDPServer();
    }

    public void StartUDPServer()
    {
        _dir = ServiceLocator.RobotDirectory;
        _flow = ServiceLocator.GameFlow;

        if (_dir == null || _flow == null)
        {
            Debug.LogError("[UDP] RobotDirectory or GameFlow is null. Check AppBootstrap.");
            return;
        }

        _flow.OnPhaseChanged += HandlePhaseChanged;

        if (_flow.Phase == GamePhase.Lobby)
            StartListener();
    }

    private void OnDisable()
    {
        if (_flow != null)
            _flow.OnPhaseChanged -= HandlePhaseChanged;

        StopListener();
    }

    private void Update()
    {
        Action a = null;
        while (true)
        {
            lock (_mtx)
            {
                if (_main.Count == 0) break;
                a = _main.Dequeue();
            }
            try { if (a != null) a(); }
            catch (Exception ex) { Debug.LogException(ex); }
        }
    }

    private void HandlePhaseChanged(GamePhase phase)
    {
        if (phase == GamePhase.Lobby)
            StartListener();
        else
            StopListener();
    }

    private void StartListener()
    {
        if (_running) return;

        lock (_mtx) _repliedTo.Clear();

        try
        {
            _udp = new UdpClient(new IPEndPoint(IPAddress.Any, discoveryPort));
            _udp.EnableBroadcast = true;

            _running = true;

            _rxThread = new Thread(ReceiveLoop);
            _rxThread.IsBackground = true;
            _rxThread.Start();

            Debug.Log("[UDP] Discovery listener started on 0.0.0.0:" + discoveryPort);
        }
        catch (Exception ex)
        {
            Debug.LogError("[UDP] Start failed: " + ex.Message);
            StopListener();
        }
    }

    private void StopListener()
    {
        if (!_running) return;

        _running = false;

        try
        {
            if (_udp != null)
            {
                _udp.Close();
                _udp = null;
            }
        }
        catch { /* ignore */ }

        try
        {
            if (_rxThread != null)
            {
                _rxThread.Join(200);
                _rxThread = null;
            }
        }
        catch { /* ignore */ }

        Debug.Log("[UDP] Discovery listener stopped");
    }

    private void ReceiveLoop()
    {
        // IMPORTANT: This method must NOT call UnityEngine APIs.
        // We only do pure .NET networking here and queue any Unity work to the main thread.

        IPEndPoint remote = new IPEndPoint(IPAddress.Any, 0);

        while (_running)
        {
            try
            {
                byte[] data = _udp.Receive(ref remote);
                string text = Encoding.UTF8.GetString(data);

                string robotId = ExtractJsonString(text, "robotId");
                string callsign = ExtractJsonString(text, "callsign");

                if (!string.IsNullOrEmpty(robotId))
                {
                    string senderIp = remote.Address.ToString();

                    PostMain(() =>
                    {
                        _dir.Upsert(robotId, callsign, senderIp);
                    });

                    string ip = PetersUtils.GetLocalIPAddress().ToString();
                    string wsUrl = "ws://" + ip + ":" + websocketPort + websocketPath;
                    string reply = "{\"ws\":\"" + wsUrl + "\"}";
                    byte[] outBytes = Encoding.UTF8.GetBytes(reply);

                    _udp.Send(outBytes, outBytes.Length, remote);

                    bool firstReply;
                    lock (_mtx) firstReply = _repliedTo.Add(robotId);
                    if (firstReply)
                        Debug.Log("[UDP] Replied to " + robotId + " at " + senderIp + " with " + wsUrl);
                }
            }
            catch (SocketException)
            {
                if (_running)
                {
                    // Socket closed intentionally - exit gracefully
                }
            }
            catch (Exception)
            {
                // Keep the loop alive for robustness
            }
        }
    }

    private void PostMain(Action a)
    {
        if (a == null) return;
        lock (_mtx) { _main.Enqueue(a); }
    }

    private static string ExtractJsonString(string s, string key)
    {
        try
        {
            string needle = "\"" + key + "\"";
            int k = s.IndexOf(needle, StringComparison.Ordinal);
            if (k < 0) return null;
            int colon = s.IndexOf(':', k);
            if (colon < 0) return null;
            int q1 = s.IndexOf('"', colon + 1);
            int q2 = s.IndexOf('"', q1 + 1);
            if (q1 < 0 || q2 < 0) return null;
            return s.Substring(q1 + 1, q2 - (q1 + 1));
        }
        catch { return null; }
    }
}
