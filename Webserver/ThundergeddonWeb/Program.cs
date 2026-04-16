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

app.Run();
