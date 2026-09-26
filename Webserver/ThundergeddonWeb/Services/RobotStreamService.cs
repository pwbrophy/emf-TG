using System.Buffers;
using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core.Features;

namespace ThundergeddonWeb.Services;

/// <summary>
/// Maintains one HTTP connection per robot MJPEG stream and fans frames out to
/// all active subscribers (phone player + spectator display).  The robot always
/// sees exactly one streaming client regardless of how many viewers are connected.
/// </summary>
public class RobotStreamService
{
    // Every part is closed by the NEXT boundary line. Browsers only display a
    // multipart part once they see the boundary that ends it, so writing the
    // boundary straight after each JPEG (rather than before the next one) shows
    // each frame as soon as it arrives instead of one frame interval late.
    internal static readonly byte[] FirstBoundary = Encoding.ASCII.GetBytes("--frame\r\n");
    internal static readonly byte[] EndBoundary   = Encoding.ASCII.GetBytes("\r\n--frame\r\n");

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
        if (response.HttpContext.Features.Get<IHttpResponseBodyFeature>() is { } bodyFeature)
            bodyFeature.DisableBuffering();

        // No minimum send rate for video. Kestrel's default (240 B/s after a 5 s
        // grace) aborts this response during any Wi-Fi blip longer than ~5 s,
        // because video always has a write pending. The abort's FIN/RST is sent
        // while the phone is unreachable, so the phone never learns the stream
        // is dead: its <img> sits on a half-open socket forever and the picture
        // goes black, while SignalR (a WebSocket, exempt from this limit) just
        // retransmits and carries on — controls work, video doesn't. Without the
        // limit the stream rides out the blip exactly like SignalR does; a phone
        // that is really gone still ends via TCP retransmission timeout.
        if (response.HttpContext.Features.Get<IHttpMinResponseDataRateFeature>() is { } rate)
            rate.MinDataRate = null;

        response.ContentType                  = "multipart/x-mixed-replace; boundary=frame";
        response.Headers["Cache-Control"]     = "no-cache";
        response.Headers["X-Accel-Buffering"] = "no";

        try
        {
            response.BodyWriter.Write(FirstBoundary);
            await response.BodyWriter.FlushAsync(ct);
        }
        catch (OperationCanceledException) { return; }

        // Keep the client's response open across robot-side drops: if the robot's
        // camera restarts (e.g. stream_off/stream_on between games) the broadcaster
        // dies, but a phone's <img> element has no reconnect logic — so reconnect
        // here and keep appending frames to the same multipart response.
        while (!ct.IsCancellationRequested)
        {
            var broadcaster = GetOrCreate(robotUrl);
            try
            {
                await broadcaster.Subscribe(response, ct);
            }
            finally
            {
                Release(robotUrl, broadcaster);
            }
            if (ct.IsCancellationRequested) break;
            try { await Task.Delay(1000, ct); } catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>
    /// Subscribes a WebSocket client to the robot stream.  Each JPEG frame is sent
    /// as one binary WebSocket message.  This bypasses iOS Safari's inability to
    /// stream-read fetch response bodies (multipart/x-mixed-replace).
    /// Like the HTTP path, reconnects to the robot while the client stays open.
    /// </summary>
    public async Task StreamFramesToWebSocket(string robotUrl, WebSocket ws, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
        {
            var broadcaster = GetOrCreate(robotUrl);
            try
            {
                await broadcaster.SubscribeWs(ws, ct);
            }
            finally
            {
                Release(robotUrl, broadcaster);
            }
            if (ct.IsCancellationRequested || ws.State != WebSocketState.Open) break;
            try { await Task.Delay(1000, ct); } catch (OperationCanceledException) { break; }
        }
    }

    private StreamBroadcaster GetOrCreate(string robotUrl)
    {
        lock (_lock)
        {
            // A dead broadcaster (robot connection already ended) is never
            // restarted — replace it so new subscribers get a fresh connection
            // instead of a channel that completes with zero frames.
            if (!_active.TryGetValue(robotUrl, out var b) || b.IsDead)
            {
                b = new StreamBroadcaster(robotUrl, _factory, _log);
                _active[robotUrl] = b;
            }
            return b;
        }
    }

    private void Release(string robotUrl, StreamBroadcaster broadcaster)
    {
        lock (_lock)
        {
            // Only remove the exact broadcaster we used — a replacement for the
            // same URL may already be registered and serving other subscribers.
            if (_active.TryGetValue(robotUrl, out var cur) && cur == broadcaster
                && !broadcaster.HasSubscribers)
                _active.Remove(robotUrl);
        }
    }
}

/// <summary>
/// Opens one streaming connection to a robot, splits it into complete JPEG
/// frames once, and hands each frame to every subscriber.
///
/// Each subscriber has a one-slot "latest frame" mailbox: if a viewer can't keep
/// up (weak Wi-Fi, slow decode), the stale frame is replaced by the newest one
/// rather than queued. Latency therefore stays at roughly one frame however slow
/// the viewer is — it just sees a lower frame rate — and frames are never torn,
/// because whole frames are dropped rather than arbitrary network chunks.
/// </summary>
internal class StreamBroadcaster
{
    private readonly string _url;
    private readonly IHttpClientFactory _factory;
    private readonly ILogger _log;

    private readonly object _subLock = new();
    private readonly Dictionary<object, Channel<byte[]>> _subs = new();
    private Task? _readTask;
    private CancellationTokenSource? _readCts;
    private bool _dead; // read loop finished — this broadcaster never streams again

    public bool HasSubscribers
    {
        get { lock (_subLock) return _subs.Count > 0; }
    }

    public bool IsDead
    {
        get { lock (_subLock) return _dead; }
    }

    public StreamBroadcaster(string url, IHttpClientFactory factory, ILogger log)
    {
        _url     = url;
        _factory = factory;
        _log     = log;
    }

    // ── HTTP subscriber ──────────────────────────────────────────────────────

    /// <summary>
    /// Writes frames as multipart parts. The caller has already written the
    /// opening boundary; every part written here ends with the next boundary.
    /// </summary>
    public async Task Subscribe(HttpResponse response, CancellationToken clientCt)
    {
        var key = new object();
        var ch  = AddSubscriber(key);
        if (ch == null) return; // dead — caller retries with a fresh broadcaster

        var writer = response.BodyWriter;
        try
        {
            await foreach (var frame in ch.Reader.ReadAllAsync(clientCt))
            {
                // Header, JPEG and closing boundary go out in a single flush so
                // Kestrel sends them as one burst of full-size TCP segments.
                writer.Write(Encoding.ASCII.GetBytes(
                    $"Content-Type: image/jpeg\r\nContent-Length: {frame.Length}\r\n\r\n"));
                writer.Write(frame);
                writer.Write(RobotStreamService.EndBoundary);
                var flush = await writer.FlushAsync(clientCt);
                if (flush.IsCompleted || flush.IsCanceled) break;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _log.LogDebug("[Stream] HTTP write error: {msg}", ex.Message); }
        finally { RemoveSubscriber(key); }
    }

    // ── WebSocket subscriber ─────────────────────────────────────────────────

    public async Task SubscribeWs(WebSocket ws, CancellationToken ct)
    {
        var key = new object();
        var ch  = AddSubscriber(key);
        if (ch == null) return; // dead — caller retries with a fresh broadcaster

        try
        {
            await foreach (var frame in ch.Reader.ReadAllAsync(ct))
            {
                if (ws.State != WebSocketState.Open) return;
                await ws.SendAsync(new ArraySegment<byte>(frame),
                    WebSocketMessageType.Binary, endOfMessage: true, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _log.LogDebug("[VideoWs] send error: {msg}", ex.Message); }
        finally { RemoveSubscriber(key); }
    }

    // ── Subscriber bookkeeping ───────────────────────────────────────────────

    private Channel<byte[]>? AddSubscriber(object key)
    {
        var ch = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(1)
        {
            FullMode     = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = true,
        });
        lock (_subLock)
        {
            if (_dead) return null;
            _subs[key] = ch;
            EnsureReadLoop();
        }
        return ch;
    }

    private void RemoveSubscriber(object key)
    {
        lock (_subLock)
        {
            if (_subs.Remove(key, out var ch)) ch.Writer.TryComplete();
            if (_subs.Count == 0) _readCts?.Cancel();
        }
    }

    // ── Shared read loop ─────────────────────────────────────────────────────

    private void EnsureReadLoop()
    {
        // One read loop per broadcaster lifetime: when it exits, the broadcaster
        // is marked dead and RobotStreamService creates a replacement. Restarting
        // the loop here would race with the dying loop's channel-complete sweep.
        if (_readTask == null)
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
            var parser = new JpegFrameParser(_log);

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

                    // Zero-byte read first: it completes as soon as ANY data has
                    // arrived; the real read then takes whatever is there. A plain
                    // read with a large buffer doesn't do that on Windows — it only
                    // completes when the buffer fills or a segment carries the TCP
                    // PSH flag, and the robot's lwIP stack often omits PSH on the
                    // last segment of a frame. That held frames back until ~3 had
                    // piled up (bursts every ~150 ms), which the latest-frame
                    // mailboxes below then collapsed to ~7 fps.
                    await stream.ReadAsync(Memory<byte>.Empty, linked.Token);
                    read = await stream.ReadAsync(buffer, linked.Token);

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

                parser.Append(buffer.AsSpan(0, read), Publish);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _log.LogWarning("[Stream] Lost connection to {url}: {msg}", _url, ex.Message);
        }

        _log.LogInformation("[Stream] Disconnected from {url}", _url);

        // Complete all subscriber channels so their await-foreach loops exit
        // cleanly, and mark the broadcaster dead in the same locked section so
        // no new channel can slip in after the sweep and hang forever.
        lock (_subLock)
        {
            _dead = true;
            foreach (var ch in _subs.Values) ch.Writer.TryComplete();
        }
    }

    private void Publish(byte[] frame)
    {
        lock (_subLock)
        {
            foreach (var ch in _subs.Values) ch.Writer.TryWrite(frame);
        }
    }
}

/// <summary>
/// Incrementally splits an MJPEG byte stream into complete JPEG images by
/// scanning for SOI (FF D8) … EOI (FF D9). Everything between frames — multipart
/// boundaries, part headers, HTTP chunk framing — is discarded. FF D9 cannot
/// occur inside JPEG entropy-coded data (0xFF bytes there are stuffed as FF 00),
/// so the first EOI after an SOI ends the frame.
/// </summary>
internal sealed class JpegFrameParser
{
    private static readonly byte[] Soi = { 0xFF, 0xD8 };
    private static readonly byte[] Eoi = { 0xFF, 0xD9 };

    // Frames are <100 KB even at VGA. A "frame" bigger than this means the stream
    // is corrupt and never delivered an EOI — drop it and resync on the next SOI.
    private const int MaxFrameBytes = 512 * 1024;

    private readonly ILogger _log;
    private byte[] _buf = new byte[64 * 1024];
    private int  _len;      // valid bytes in _buf
    private bool _inFrame;  // _buf starts with the SOI of the frame being collected
    private int  _scan;     // where the next marker search resumes

    public JpegFrameParser(ILogger log) { _log = log; }

    public void Append(ReadOnlySpan<byte> data, Action<byte[]> onFrame)
    {
        if (_len + data.Length > _buf.Length)
            Array.Resize(ref _buf, Math.Max(_buf.Length * 2, _len + data.Length));
        data.CopyTo(_buf.AsSpan(_len));
        _len += data.Length;

        while (true)
        {
            if (!_inFrame)
            {
                int soi = _buf.AsSpan(_scan, _len - _scan).IndexOf(Soi);
                if (soi < 0)
                {
                    // Keep a trailing 0xFF: it may be the first half of an SOI.
                    bool keepLast = _len > 0 && _buf[_len - 1] == 0xFF;
                    if (keepLast) _buf[0] = 0xFF;
                    _len  = keepLast ? 1 : 0;
                    _scan = 0;
                    return;
                }
                Discard(_scan + soi); // SOI now at _buf[0]
                _inFrame = true;
                _scan    = 2;
            }

            int eoi = _buf.AsSpan(_scan, _len - _scan).IndexOf(Eoi);
            if (eoi < 0)
            {
                if (_len > MaxFrameBytes)
                {
                    _log.LogWarning("[Stream] frame buffer overflow ({len} bytes) — resyncing", _len);
                    _len = 0; _scan = 0; _inFrame = false;
                    return;
                }
                _scan = Math.Max(2, _len - 1); // an EOI may straddle this read and the next
                return;
            }

            int end = _scan + eoi + 2;
            onFrame(_buf.AsSpan(0, end).ToArray());
            Discard(end);
            _inFrame = false;
            _scan    = 0;
        }
    }

    // Drops the first n bytes, shifting the rest to the front of the buffer.
    private void Discard(int n)
    {
        if (n <= 0) return;
        Buffer.BlockCopy(_buf, n, _buf, 0, _len - n);
        _len -= n;
    }
}
