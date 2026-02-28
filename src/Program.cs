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

var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    ContentRootPath = exeDir,
});

builder.Services.AddWindowsService(options =>
{
    options.ServiceName = "GruppenRemoteAgent";
});

builder.Configuration.AddJsonFile("config.json", optional: true, reloadOnChange: true);

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

var host = builder.Build();
await host.RunAsync();
