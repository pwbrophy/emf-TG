namespace ThundergeddonWeb.Services;

/// <summary>
/// Writes video diagnostics — the stream proxy's per-tank in/out frame rates and
/// each phone's self-reported display rate — to the console and to a daily file,
/// logs/video-YYYY-MM-DD.log under the content root. The file survives restarts,
/// so after an event it shows whether choppy video came from a tank, the Wi-Fi
/// link to a phone, or a phone too slow to draw what it received.
/// </summary>
public sealed class VideoStatsLog
{
    private readonly ILogger<VideoStatsLog> _log;
    private readonly string _dir;
    private readonly object _lock = new();

    public VideoStatsLog(IWebHostEnvironment env, ILogger<VideoStatsLog> log)
    {
        _log = log;
        _dir = Path.Combine(env.ContentRootPath, "logs");
        _log.LogInformation("[VideoStats] logging to {dir}", _dir);
    }

    public void Write(string line)
    {
        _log.LogInformation("{line}", line);
        try
        {
            var now = DateTime.Now;
            lock (_lock)
            {
                Directory.CreateDirectory(_dir);
                File.AppendAllText(Path.Combine(_dir, $"video-{now:yyyy-MM-dd}.log"),
                                   $"{now:HH:mm:ss} {line}{Environment.NewLine}");
            }
        }
        catch (Exception ex)
        {
            _log.LogDebug("[VideoStats] file write failed: {msg}", ex.Message);
        }
    }
}

/// <summary>What a phone measured about its own video over a short sample window.</summary>
public sealed class VideoStatsReport
{
    public string? Name     { get; set; }
    public string? Path     { get; set; } // "img" (Android <img> MJPEG) or "ws" (WebSocket + canvas)
    public double  ShownFps { get; set; } // new frames actually painted on screen
    public double  RecvFps  { get; set; } // ws path only: frames received
    public double  DecodeMs { get; set; } // ws path only: average decode + draw time per frame
    public double  PageFps  { get; set; } // requestAnimationFrame rate — low = phone is struggling
    public string? Device   { get; set; }
    public int     Cores    { get; set; }
    public double  MemoryGb { get; set; } // navigator.deviceMemory (Chrome only, rounded by the browser)
    public string? Screen   { get; set; }
    public bool    LowFx    { get; set; }
}
