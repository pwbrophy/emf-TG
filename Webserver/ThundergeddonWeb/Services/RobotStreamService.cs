using System.Collections.Concurrent;
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
        StreamBroadcaster broadcaster;
        lock (_lock)
        {
            if (!_active.TryGetValue(robotUrl, out broadcaster!))
            {
                broadcaster = new StreamBroadcaster(robotUrl, _factory, _log);
                _active[robotUrl] = broadcaster;
            }
        }

        try
        {
            await broadcaster.Subscribe(response, ct);
        }
        finally
        {
            broadcaster.Unsubscribe(response);
            lock (_lock)
            {
                if (!broadcaster.HasSubscribers)
                    _active.Remove(robotUrl);
            }
        }
    }
}

/// <summary>
/// Opens one streaming connection to a robot and fans frames to all subscribers.
/// Each subscriber gets its own Channel so a slow client never blocks the others.
/// </summary>
internal class StreamBroadcaster
{
    private readonly string _url;
    private readonly IHttpClientFactory _factory;
    private readonly ILogger _log;

    private readonly object _subLock = new();
    // Map from response → dedicated channel for that subscriber
    private readonly Dictionary<HttpResponse, Channel<byte[]>> _channels = new();
    private Task? _readTask;
    private CancellationTokenSource? _readCts;

    public bool HasSubscribers { get { lock (_subLock) return _channels.Count > 0; } }

    public StreamBroadcaster(string url, IHttpClientFactory factory, ILogger log)
    {
        _url     = url;
        _factory = factory;
        _log     = log;
    }

    public async Task Subscribe(HttpResponse response, CancellationToken clientCt)
    {
        // Disable ASP.NET / Kestrel response buffering so chunks reach the browser
        // the instant they are flushed rather than being held until the response ends.
        if (response.HttpContext.Features.Get<IHttpResponseBodyFeature>() is { } bodyFeature)
            bodyFeature.DisableBuffering();

        response.ContentType                  = "multipart/x-mixed-replace; boundary=frame";
        response.Headers["Cache-Control"]     = "no-cache";
        response.Headers["X-Accel-Buffering"] = "no";

        // Bounded channel: drop old frames rather than blocking the broadcaster.
        var ch = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(4)
        {
            FullMode          = BoundedChannelFullMode.DropOldest,
            SingleReader      = true,
            SingleWriter      = false,
        });

        lock (_subLock)
        {
            _channels[response] = ch;
            if (_readTask == null || _readTask.IsCompleted)
            {
                _readCts  = new CancellationTokenSource();
                _readTask = Task.Run(() => ReadLoop(_readCts.Token));
            }
        }

        // Drain the channel into the HTTP response until the client disconnects.
        try
        {
            await foreach (var frame in ch.Reader.ReadAllAsync(clientCt))
            {
                await response.Body.WriteAsync(frame, clientCt);
                await response.Body.FlushAsync(clientCt);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _log.LogDebug("[Stream] Subscriber write error: {msg}", ex.Message);
        }
    }

    public void Unsubscribe(HttpResponse response)
    {
        lock (_subLock)
        {
            if (_channels.Remove(response, out var ch))
                ch.Writer.TryComplete();

            if (_channels.Count == 0)
                _readCts?.Cancel();
        }
    }

    private async Task ReadLoop(CancellationToken ct)
    {
        _log.LogInformation("[Stream] Connecting to {url}", _url);
        try
        {
            var client = _factory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(30);

            using var resp = await client.GetAsync(_url, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();
            _log.LogInformation("[Stream] Connected to {url}", _url);

            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            var buffer = new byte[32768];

            while (!ct.IsCancellationRequested)
            {
                int read = await stream.ReadAsync(buffer, 0, buffer.Length, ct);
                if (read == 0) break;

                // Copy frame bytes once, then give each subscriber its own reference.
                byte[] frame = buffer[..read].ToArray();

                List<Channel<byte[]>> channels;
                lock (_subLock) channels = [.. _channels.Values];

                foreach (var ch in channels)
                    ch.Writer.TryWrite(frame); // non-blocking; DropOldest handles backpressure
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _log.LogWarning("[Stream] Lost connection to {url}: {msg}", _url, ex.Message);
        }

        _log.LogInformation("[Stream] Disconnected from {url}", _url);

        // Signal all subscriber channels that no more data is coming.
        lock (_subLock)
        {
            foreach (var ch in _channels.Values) ch.Writer.TryComplete();
        }
    }
}
