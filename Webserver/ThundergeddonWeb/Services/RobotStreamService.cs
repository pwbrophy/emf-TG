using System.Net.WebSockets;
using System.Threading.Channels;
using Microsoft.AspNetCore.Http.Features;

namespace ThundergeddonWeb.Services;

/// <summary>
/// Maintains one HTTP connection per robot MJPEG stream and fans frames out to
/// all active subscribers (phone player + spectator display).  The robot always
/// sees exactly one streaming client regardless of how many viewers are connected.
/// </summary>
public class RobotStreamService
{
    private readonly IHttpClientFactory _factory;
    private readonly ILogger<RobotStreamService> _log;
    private readonly object _lock = new();
    private readonly Dictionary<string, StreamBroadcaster> _active = new();

    public RobotStreamService(IHttpClientFactory factory, ILogger<RobotStreamService> log)
    {
        _factory = factory;
        _log     = log;
    }

    public async Task StreamToSubscriber(string robotUrl, HttpResponse response, CancellationToken ct)
    {
        var broadcaster = GetOrCreate(robotUrl);
        try
        {
            await broadcaster.Subscribe(response, ct);
        }
        finally
        {
            broadcaster.Unsubscribe(response);
            lock (_lock) { if (!broadcaster.HasSubscribers) _active.Remove(robotUrl); }
        }
    }

    /// <summary>
    /// Subscribes a WebSocket client to the robot stream.  Individual JPEG frames
    /// are parsed server-side from the MJPEG multipart stream and sent as binary
    /// WebSocket messages.  This bypasses iOS Safari's inability to stream-read
    /// fetch response bodies (multipart/x-mixed-replace).
    /// </summary>
    public async Task StreamFramesToWebSocket(string robotUrl, WebSocket ws, CancellationToken ct)
    {
        var broadcaster = GetOrCreate(robotUrl);
        try
        {
            await broadcaster.SubscribeWs(ws, ct);
        }
        finally
        {
            lock (_lock) { if (!broadcaster.HasSubscribers) _active.Remove(robotUrl); }
        }
    }

    private StreamBroadcaster GetOrCreate(string robotUrl)
    {
        lock (_lock)
        {
            if (!_active.TryGetValue(robotUrl, out var b))
            {
                b = new StreamBroadcaster(robotUrl, _factory, _log);
                _active[robotUrl] = b;
            }
            return b;
        }
    }
}

/// <summary>
/// Opens one streaming connection to a robot and fans raw MJPEG chunks to HTTP
/// subscribers and parsed JPEG frames to WebSocket subscribers.
/// Each subscriber gets its own Channel so a slow client never blocks the others.
/// </summary>
internal class StreamBroadcaster
{
    private readonly string _url;
    private readonly IHttpClientFactory _factory;
    private readonly ILogger _log;

    private readonly object _subLock = new();
    private readonly Dictionary<HttpResponse, Channel<byte[]>> _channels = new();
    private readonly Dictionary<Guid, Channel<byte[]>>         _wsChannels = new();
    private Task? _readTask;
    private CancellationTokenSource? _readCts;

    public bool HasSubscribers
    {
        get { lock (_subLock) return _channels.Count > 0 || _wsChannels.Count > 0; }
    }

    public StreamBroadcaster(string url, IHttpClientFactory factory, ILogger log)
    {
        _url     = url;
        _factory = factory;
        _log     = log;
    }

    // ── HTTP subscriber ──────────────────────────────────────────────────────

    public async Task Subscribe(HttpResponse response, CancellationToken clientCt)
    {
        if (response.HttpContext.Features.Get<IHttpResponseBodyFeature>() is { } bodyFeature)
            bodyFeature.DisableBuffering();

        response.ContentType                  = "multipart/x-mixed-replace; boundary=frame";
        response.Headers["Cache-Control"]     = "no-cache";
        response.Headers["X-Accel-Buffering"] = "no";

        var ch = MakeChannel();
        lock (_subLock)
        {
            _channels[response] = ch;
            EnsureReadLoop();
        }

        try
        {
            await foreach (var chunk in ch.Reader.ReadAllAsync(clientCt))
            {
                await response.Body.WriteAsync(chunk, clientCt);
                await response.Body.FlushAsync(clientCt);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _log.LogDebug("[Stream] HTTP write error: {msg}", ex.Message); }
    }

    public void Unsubscribe(HttpResponse response)
    {
        lock (_subLock)
        {
            if (_channels.Remove(response, out var ch)) ch.Writer.TryComplete();
            if (!HasSubscribers) _readCts?.Cancel();
        }
    }

    // ── WebSocket subscriber ─────────────────────────────────────────────────

    public async Task SubscribeWs(WebSocket ws, CancellationToken ct)
    {
        var id = Guid.NewGuid();
        var ch = MakeChannel();
        lock (_subLock)
        {
            _wsChannels[id] = ch;
            EnsureReadLoop();
        }

        // Buffer incoming raw MJPEG chunks, extract complete JPEG frames,
        // and send each frame as a binary WebSocket message.
        var buf = Array.Empty<byte>();
        try
        {
            await foreach (var chunk in ch.Reader.ReadAllAsync(ct))
            {
                // Append chunk to running buffer
                var merged = new byte[buf.Length + chunk.Length];
                buf.CopyTo(merged, 0);
                chunk.CopyTo(merged, buf.Length);
                buf = merged;

                // Send every complete JPEG frame found in the buffer
                while (buf.Length > 3)
                {
                    var (frame, remaining) = ExtractJpegFrame(buf);
                    if (frame is null) break;
                    buf = remaining;
                    if (ws.State != WebSocketState.Open) return;
                    await ws.SendAsync(new ArraySegment<byte>(frame),
                        WebSocketMessageType.Binary, endOfMessage: true, ct);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _log.LogDebug("[VideoWs] send error: {msg}", ex.Message); }
        finally
        {
            lock (_subLock)
            {
                if (_wsChannels.Remove(id)) ch.Writer.TryComplete();
                if (!HasSubscribers) _readCts?.Cancel();
            }
        }
    }

    // ── Shared read loop ─────────────────────────────────────────────────────

    private void EnsureReadLoop()
    {
        if (_readTask == null || _readTask.IsCompleted)
        {
            _readCts  = new CancellationTokenSource();
            _readTask = Task.Run(() => ReadLoop(_readCts.Token));
        }
    }

    // Maximum time to wait for any single ReadAsync on the robot's MJPEG stream.
    // At 10 fps a frame arrives every ~100 ms; 5 s of silence means the robot's
    // camera has crashed or the TCP connection is silently dead.
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(5);

    private async Task ReadLoop(CancellationToken ct)
    {
        _log.LogInformation("[Stream] Connecting to {url}", _url);
        try
        {
            var client = _factory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(30); // initial connect + headers only

            using var resp = await client.GetAsync(_url, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();
            _log.LogInformation("[Stream] Connected to {url}", _url);

            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            var buffer = new byte[32768];

            while (!ct.IsCancellationRequested)
            {
                int read;
                try
                {
                    // Per-read timeout: HttpClient.Timeout covers the initial request
                    // but NOT the streaming body.  Without this, a camera crash leaves
                    // subscribers with a frozen frame and a blocked read indefinitely.
                    using var readTimeout = new CancellationTokenSource(ReadTimeout);
                    using var linked      = CancellationTokenSource
                        .CreateLinkedTokenSource(ct, readTimeout.Token);

                    read = await stream.ReadAsync(buffer, 0, buffer.Length, linked.Token);

                    if (readTimeout.IsCancellationRequested)
                    {
                        // Timed out before any data — camera is silent.
                        _log.LogWarning("[Stream] {url}: read timed out — camera crash?", _url);
                        break;
                    }
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    // Per-read timeout fired; outer ct is still live (subscribers present).
                    _log.LogWarning("[Stream] {url}: read timed out — camera crash?", _url);
                    break;
                }
                catch (OperationCanceledException) { break; } // outer ct cancelled — normal exit

                if (read == 0) break; // graceful stream end

                byte[] chunk = buffer[..read].ToArray();

                List<Channel<byte[]>> all;
                lock (_subLock) all = [.. _channels.Values, .. _wsChannels.Values];

                foreach (var ch in all)
                    ch.Writer.TryWrite(chunk);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _log.LogWarning("[Stream] Lost connection to {url}: {msg}", _url, ex.Message);
        }

        _log.LogInformation("[Stream] Disconnected from {url}", _url);

        // Complete all subscriber channels so their await-foreach loops exit cleanly.
        lock (_subLock)
        {
            foreach (var ch in _channels.Values)  ch.Writer.TryComplete();
            foreach (var ch in _wsChannels.Values) ch.Writer.TryComplete();
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static Channel<byte[]> MakeChannel() =>
        Channel.CreateBounded<byte[]>(new BoundedChannelOptions(4)
        {
            FullMode     = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });

    /// <summary>
    /// Scans <paramref name="buf"/> for the first complete JPEG (SOI FF D8 … EOI FF D9).
    /// Returns (frame bytes, remaining buffer) on success, or (null, original buf) if the
    /// frame is incomplete.
    /// </summary>
    private static (byte[]? frame, byte[] remaining) ExtractJpegFrame(byte[] buf)
    {
        int soi = -1;
        for (int i = 0; i < buf.Length - 1; i++)
            if (buf[i] == 0xFF && buf[i + 1] == 0xD8) { soi = i; break; }
        if (soi < 0) return (null, Array.Empty<byte>());

        int eoi = -1;
        for (int i = soi + 2; i < buf.Length - 1; i++)
            if (buf[i] == 0xFF && buf[i + 1] == 0xD9) { eoi = i + 2; break; }
        if (eoi < 0) return (null, buf[soi..]);

        return (buf[soi..eoi], buf[eoi..]);
    }
}
