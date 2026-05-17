using System.Collections.Concurrent;

namespace ThundergeddonWeb.Services;

/// <summary>
/// Maintains one HTTP connection per robot MJPEG stream and fans raw bytes out to
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
/// Opens one streaming connection to a robot and forwards every chunk of bytes it
/// receives to all currently-subscribed HTTP responses.
/// </summary>
internal class StreamBroadcaster
{
    private readonly string _url;
    private readonly IHttpClientFactory _factory;
    private readonly ILogger _log;

    private readonly object _subLock = new();
    private readonly List<HttpResponse> _subscribers = new();
    private Task? _readTask;
    private CancellationTokenSource? _readCts;

    public bool HasSubscribers { get { lock (_subLock) return _subscribers.Count > 0; } }

    public StreamBroadcaster(string url, IHttpClientFactory factory, ILogger log)
    {
        _url     = url;
        _factory = factory;
        _log     = log;
    }

    public async Task Subscribe(HttpResponse response, CancellationToken clientCt)
    {
        response.ContentType                  = "multipart/x-mixed-replace; boundary=frame";
        response.Headers["Cache-Control"]     = "no-cache";
        response.Headers["X-Accel-Buffering"] = "no";

        lock (_subLock)
        {
            _subscribers.Add(response);
            if (_readTask == null || _readTask.IsCompleted)
            {
                _readCts  = new CancellationTokenSource();
                _readTask = Task.Run(() => ReadLoop(_readCts.Token));
            }
        }

        // Hold open until the client disconnects or the server shuts down.
        try { await Task.Delay(Timeout.Infinite, clientCt); }
        catch (OperationCanceledException) { }
    }

    public void Unsubscribe(HttpResponse response)
    {
        lock (_subLock)
        {
            _subscribers.Remove(response);
            if (_subscribers.Count == 0)
                _readCts?.Cancel();
        }
    }

    private async Task ReadLoop(CancellationToken ct)
    {
        _log.LogInformation("[Stream] Connecting to {url}", _url);
        try
        {
            var client = _factory.CreateClient();
            // Long timeout — robot may be slow to start streaming.
            client.Timeout = TimeSpan.FromSeconds(30);

            using var resp = await client.GetAsync(_url, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();

            _log.LogInformation("[Stream] Connected to {url} ({ct})", _url,
                resp.Content.Headers.ContentType);

            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            var buffer = new byte[65536];

            while (!ct.IsCancellationRequested)
            {
                int read = await stream.ReadAsync(buffer, 0, buffer.Length, ct);
                if (read == 0) break;

                var chunk = buffer.AsMemory(0, read);

                List<HttpResponse> subs;
                lock (_subLock) subs = [.. _subscribers];

                await Task.WhenAll(subs.Select(async sub =>
                {
                    try
                    {
                        await sub.Body.WriteAsync(chunk, ct);
                        await sub.Body.FlushAsync(ct);
                    }
                    catch
                    {
                        // Subscriber disconnected — will be removed by Unsubscribe().
                    }
                }));
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _log.LogWarning("[Stream] Lost connection to {url}: {msg}", _url, ex.Message);
        }

        _log.LogInformation("[Stream] Disconnected from {url}", _url);
    }
}
