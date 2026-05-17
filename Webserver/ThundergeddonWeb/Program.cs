using System.Net.NetworkInformation;
using System.Net.Sockets;
using ThundergeddonWeb.Hubs;
using ThundergeddonWeb.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSignalR();
builder.Services.AddHttpClient();
builder.Services.AddSingleton<UnityBridgeService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<UnityBridgeService>());

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();
app.MapHub<GameHub>("/gamehub");

app.MapGet("/api/serverip", () =>
{
    string ip = GetLanIpv4();
    return Results.Ok(new { ip, url = $"http://{ip}:5000" });
});

// Proxy the robot's MJPEG stream so the display browser never connects directly to the
// robot (esp_http_server is single-threaded; a second direct client would block the first).
app.MapGet("/api/spectate-stream", async (string url, IHttpClientFactory factory, HttpContext ctx, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(url))
    {
        ctx.Response.StatusCode = 400;
        return;
    }
    var client = factory.CreateClient();
    using var resp = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
    ctx.Response.ContentType = resp.Content.Headers.ContentType?.ToString()
        ?? "multipart/x-mixed-replace; boundary=frame";
    ctx.Response.Headers["Cache-Control"]      = "no-cache";
    ctx.Response.Headers["X-Accel-Buffering"]  = "no";
    await using var stream = await resp.Content.ReadAsStreamAsync(ct);
    await stream.CopyToAsync(ctx.Response.Body, ct);
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
