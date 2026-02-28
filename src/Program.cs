using GruppenRemoteAgent;
using GruppenRemoteAgent.Config;
using GruppenRemoteAgent.Diagnostics;
using GruppenRemoteAgent.Input;
using GruppenRemoteAgent.Network;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = Host.CreateApplicationBuilder(args);

// Resolve config.json relative to the executable, not the working directory.
// Windows services run with CWD = C:\Windows\System32, so a relative path
// would never find the file sitting next to the .exe.
var exeDir = Path.GetDirectoryName(Environment.ProcessPath)!;
var configPath = Path.Combine(exeDir, "config.json");

builder.Configuration.AddJsonFile(configPath, optional: true, reloadOnChange: true);

// Bind configuration section
builder.Services.Configure<AgentConfig>(builder.Configuration);

// Configure logging
var logPath = builder.Configuration["LogPath"]
              ?? @"C:\ProgramData\Gruppen\RemoteAgent\logs";

Directory.CreateDirectory(logPath);

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

// Enable running as Windows Service
builder.Services.AddWindowsService(options =>
{
    options.ServiceName = "GruppenRemoteAgent";
});

var host = builder.Build();
await host.RunAsync();
