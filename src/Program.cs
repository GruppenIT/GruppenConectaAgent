using GruppenRemoteAgent;
using GruppenRemoteAgent.Capture;
using GruppenRemoteAgent.Config;
using GruppenRemoteAgent.Diagnostics;
using GruppenRemoteAgent.Input;
using GruppenRemoteAgent.Network;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// -----------------------------------------------------------------------
// Capture-helper mode: launched by the service in the user's interactive
// session to perform screen capture and relay JPEG frames via named pipe.
// -----------------------------------------------------------------------
if (args.Length >= 2 && args[0] == "--capture-helper")
{
    string capturePipe = args[1];
    string? inputPipe = args.Length >= 3 ? args[2] : null;
    CaptureHelper.Run(capturePipe, inputPipe);
    return;
}

// -----------------------------------------------------------------------
// Normal service mode
// -----------------------------------------------------------------------
var dataDir = @"C:\ProgramData\Gruppen\RemoteAgent";
var logPath = Path.Combine(dataDir, "logs");
Directory.CreateDirectory(logPath);

// Write a diagnostic file so we can always see what the process sees.
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

// Load config from both locations; ProgramData values take precedence.
builder.Configuration.AddJsonFile(exeConfigPath, optional: true, reloadOnChange: true);
builder.Configuration.AddJsonFile(dataConfigPath, optional: true, reloadOnChange: true);

File.AppendAllText(diagFile, string.Join(Environment.NewLine,
    $"Resolved ConsoleUrl : {builder.Configuration["ConsoleUrl"]}",
    $"Resolved AgentId    : {builder.Configuration["AgentId"]}",
    $"Resolved LogPath    : {builder.Configuration["LogPath"]}",
    ""));

builder.Services.Configure<AgentConfig>(builder.Configuration);

if (!File.Exists(dataConfigPath) && File.Exists(exeConfigPath))
    File.Copy(exeConfigPath, dataConfigPath);

builder.Services.AddLogging(logging =>
{
    logging.ClearProviders();
    logging.AddConsole();
    logging.AddFile(logPath + "/agent-{Date}.log");
});

builder.Services.AddSingleton<WebSocketClient>();
builder.Services.AddSingleton<MouseSimulator>();
builder.Services.AddSingleton<KeyboardSimulator>();
builder.Services.AddSingleton<SystemInfo>();
builder.Services.AddHostedService<AgentService>();

var host = builder.Build();
await host.RunAsync();
