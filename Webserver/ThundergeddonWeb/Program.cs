using System.Net.NetworkInformation;
using System.Net.Sockets;
using ThundergeddonWeb.Hubs;
using ThundergeddonWeb.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSignalR();
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
