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

var app = builder.Build();

app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapHub<GameHub>("/gamehub");

app.MapGet("/api/serverip", () =>
{
    string ip = GetLanIpv4();
    return Results.Ok(new { ip, url = $"http://{ip}:5000" });
});

// Lightweight pre-join poll: phone checks this every 2 s so the join button
// reflects the real Unity-bridge state before the user has connected SignalR.
app.MapGet("/api/server-ready", (UnityBridgeService bridge) =>
    Results.Ok(new { ready = bridge.IsConnectedToUnity }));

// Fan-out MJPEG proxy: one connection to the robot, shared between all subscribers
// (phone player + spectator display).  The robot always sees exactly one HTTP client.
app.MapGet("/api/robot-stream", async (string url, RobotStreamService streamer, HttpContext ctx, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(url))
    {
        ctx.Response.StatusCode = 400;
        return;
    }
    await streamer.StreamToSubscriber(url, ctx.Response, ct);
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
    using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
    await streamer.StreamFramesToWebSocket(robotUrl, ws, ctx.RequestAborted);
});

app.Run();

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
