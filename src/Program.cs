using GruppenRemoteAgent;
using GruppenRemoteAgent.Config;
using GruppenRemoteAgent.Diagnostics;
using GruppenRemoteAgent.Input;
using GruppenRemoteAgent.Network;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// Force ContentRootPath to the directory that contains the .exe so that
// config.json (installed by the MSI next to the executable) is always found,
// regardless of the working directory set by the Windows Service Control Manager.
var exeDir = Path.GetDirectoryName(Environment.ProcessPath)!;
var dataDir = @"C:\ProgramData\Gruppen\RemoteAgent";

var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    ContentRootPath = exeDir,
});

builder.Services.AddWindowsService(options =>
{
    options.ServiceName = "GruppenRemoteAgent";
});

// 1) Load defaults from the install directory (bundled by the MSI).
// 2) Overlay with user config from ProgramData (survives reinstalls).
builder.Configuration.AddJsonFile("config.json", optional: true, reloadOnChange: true);
builder.Configuration.AddJsonFile(
    Path.Combine(dataDir, "config.json"), optional: true, reloadOnChange: true);

// Bind configuration section
builder.Services.Configure<AgentConfig>(builder.Configuration);

// Configure logging
var logPath = builder.Configuration["LogPath"]
              ?? @"C:\ProgramData\Gruppen\RemoteAgent\logs";

Directory.CreateDirectory(logPath);

// Seed a user-editable config.json in ProgramData on first run so the user
// has a single well-known location to edit that won't be overwritten by MSI.
var userConfig = Path.Combine(dataDir, "config.json");
if (!File.Exists(userConfig))
{
    var defaultConfig = Path.Combine(exeDir, "config.json");
    if (File.Exists(defaultConfig))
        File.Copy(defaultConfig, userConfig);
}

builder.Services.AddLogging(logging =>
{
    logging.ClearProviders();
    logging.AddConsole();
    logging.AddFile(logPath + "/agent-{Date}.log");
});

// Register services
builder.Services.AddSingleton<WebSocketClient>();
builder.Services.AddSingleton<MouseSimulator>();
builder.Services.AddSingleton<KeyboardSimulator>();
builder.Services.AddSingleton<SystemInfo>();

// Register the main background service
builder.Services.AddHostedService<AgentService>();

var host = builder.Build();
await host.RunAsync();
