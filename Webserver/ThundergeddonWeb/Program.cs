using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Net.WebSockets;
using ThundergeddonWeb.Hubs;
using ThundergeddonWeb.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSignalR();
builder.Services.AddHttpClient();
builder.Services.AddSingleton<UnityBridgeService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<UnityBridgeService>());
builder.Services.AddSingleton<RobotStreamService>();
builder.Services.AddSingleton<VideoStatsLog>();

var app = builder.Build();

app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });
app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    // Phones otherwise heuristically cache the HTML pages and keep showing a stale
    // layout after an edit. no-cache = always revalidate (cheap 304 via ETag).
    OnPrepareResponse = ctx =>
    {
        if (ctx.File.Name.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
            ctx.Context.Response.Headers.CacheControl = "no-cache";
    }
});
app.MapHub<GameHub>("/gamehub");

app.MapGet("/api/serverip", () =>
{
    string ip = GetLanIpv4();
    return Results.Ok(new { ip, url = $"http://{ip}:5000" });
});

// Lightweight pre-join poll: phone checks this every 2 s so the join button
// reflects the real Unity-bridge state before the user has connected SignalR.
app.MapGet("/api/server-ready", (UnityBridgeService bridge) =>
    Results.Ok(new { ready = bridge.IsGameReady, phase = bridge.CurrentPhase }));

// Fan-out MJPEG proxy: one connection to the robot, shared between all subscribers
// (phone player + spectator display).  The robot always sees exactly one HTTP client.
// who = viewer name for the stats log (the player's name, or "display").
app.MapGet("/api/robot-stream", async (string url, string? who, RobotStreamService streamer, HttpContext ctx, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(url))
    {
        ctx.Response.StatusCode = 400;
        return;
    }
    await streamer.StreamToSubscriber(url, ViewerName(who), ctx.Response, ct);
});

// WebSocket video endpoint: parses MJPEG frames server-side and pushes each JPEG
// as a binary WebSocket message.  Used by iOS Safari/Chrome which cannot stream-read
// multipart fetch response bodies.
app.Map("/api/video-ws", async (HttpContext ctx) =>
{
    if (!ctx.WebSockets.IsWebSocketRequest)
    {
        ctx.Response.StatusCode = 400;
        return;
    }
    var robotUrl = ctx.Request.Query["url"].ToString();
    if (string.IsNullOrWhiteSpace(robotUrl))
    {
        ctx.Response.StatusCode = 400;
        return;
    }
    var streamer = ctx.RequestServices.GetRequiredService<RobotStreamService>();
    int.TryParse(ctx.Request.Query["maxfps"], out int maxFps); // 0 / absent = uncapped
    using var ws  = await ctx.WebSockets.AcceptWebSocketAsync();
    using var cts = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted);

    // Watch for the browser closing the socket. Its ws.close() sends a close frame
    // and waits for ours, but nothing else ever reads this socket — so the server
    // never saw the close and kept streaming video into it until the browser gave
    // up on the handshake (~25–30 s measured). That doubled a phone's video for
    // half a minute after every stall-restart, and kept all ten display streams
    // running after each match.
    var closeWatch = Task.Run(async () =>
    {
        var buf = new byte[256];
        try
        {
            while (ws.State == WebSocketState.Open)
            {
                var r = await ws.ReceiveAsync(buf, cts.Token);
                if (r.MessageType == WebSocketMessageType.Close) break;
            }
        }
        catch { /* socket gone or cancelled */ }
        cts.Cancel();
    });

    await streamer.StreamFramesToWebSocket(robotUrl, ViewerName(ctx.Request.Query["who"]), maxFps, ws, cts.Token);
    cts.Cancel();
    if (ws.State == WebSocketState.CloseReceived)
    {
        try { await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None); }
        catch { /* already gone */ }
    }
    await closeWatch;
});

app.Run();

// Stats-log label for a video viewer: trimmed, no control characters, capped.
static string ViewerName(string? who)
{
    var clean = new string((who ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();
    if (clean.Length == 0) return "viewer";
    return clean.Length <= 24 ? clean : clean[..24];
}

static string GetLanIpv4()
{
    foreach (var iface in NetworkInterface.GetAllNetworkInterfaces())
    {
        if (iface.OperationalStatus != OperationalStatus.Up) continue;
        if (iface.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
        foreach (var addr in iface.GetIPProperties().UnicastAddresses)
        {
            if (addr.Address.AddressFamily == AddressFamily.InterNetwork
                && !addr.Address.ToString().StartsWith("169.254")) // skip APIPA
                return addr.Address.ToString();
        }
    }
    return "localhost";
}
