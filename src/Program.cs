using GruppenRemoteAgent;
using GruppenRemoteAgent.Config;
using GruppenRemoteAgent.Diagnostics;
using GruppenRemoteAgent.Input;
using GruppenRemoteAgent.Network;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var dataDir = @"C:\ProgramData\Gruppen\RemoteAgent";
var logPath = Path.Combine(dataDir, "logs");
Directory.CreateDirectory(logPath);

// Write a diagnostic file BEFORE anything else so we can always see what
// the process sees, even if configuration or DI fails completely.
var diagFile = Path.Combine(logPath, "startup-diag.txt");
var exeDir = AppContext.BaseDirectory;
var exeConfigPath = Path.Combine(exeDir, "config.json");
var dataConfigPath = Path.Combine(dataDir, "config.json");

File.WriteAllText(diagFile, string.Join(Environment.NewLine,
    $"Timestamp       : {DateTime.Now:O}",
    $"ProcessPath     : {Environment.ProcessPath}",
    $"BaseDirectory   : {AppContext.BaseDirectory}",
    $"CWD             : {Environment.CurrentDirectory}",
    $"ExeDir          : {exeDir}",
    $"ExeConfig exists: {File.Exists(exeConfigPath)} -> {exeConfigPath}",
    $"DataConfig exists: {File.Exists(dataConfigPath)} -> {dataConfigPath}",
    ""));

var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    ContentRootPath = exeDir,
});

builder.Services.AddWindowsService(options =>
{
    options.ServiceName = "GruppenRemoteAgent";
});

// Load config from both locations; ProgramData values take precedence
// over the install-directory defaults and survive MSI reinstalls.
builder.Configuration.AddJsonFile(exeConfigPath, optional: true, reloadOnChange: true);
builder.Configuration.AddJsonFile(dataConfigPath, optional: true, reloadOnChange: true);

// Append resolved values to the diagnostic file
File.AppendAllText(diagFile, string.Join(Environment.NewLine,
    $"Resolved ConsoleUrl : {builder.Configuration["ConsoleUrl"]}",
    $"Resolved AgentId    : {builder.Configuration["AgentId"]}",
    $"Resolved LogPath    : {builder.Configuration["LogPath"]}",
    ""));

// Bind configuration section
builder.Services.Configure<AgentConfig>(builder.Configuration);

// Seed a user-editable config.json in ProgramData on first run so the user
// has a single well-known location to edit that won't be overwritten by MSI.
if (!File.Exists(dataConfigPath) && File.Exists(exeConfigPath))
    File.Copy(exeConfigPath, dataConfigPath);

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
