using GruppenRemoteAgent.Capture;
using GruppenRemoteAgent.Config;
using GruppenRemoteAgent.Diagnostics;
using GruppenRemoteAgent.Input;
using GruppenRemoteAgent.Network;
using GruppenRemoteAgent.Protocol;
using GruppenRemoteAgent.Session;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GruppenRemoteAgent;

public class AgentService : BackgroundService
{
    private readonly ILogger<AgentService> _logger;
    private readonly AgentConfig _config;
    private readonly WebSocketClient _wsClient;
    private readonly MouseSimulator _mouse;
    private readonly KeyboardSimulator _keyboard;
    private readonly SystemInfo _systemInfo;
    private readonly ILogger<DxgiCapture> _dxgiLogger;
    private readonly ILogger<GdiCapture> _gdiLogger;
    private readonly ILogger<SessionCapture> _sessionLogger;

    private IScreenCapture? _screenCapture;
    private bool _isStreaming;
    private bool _wasStreaming; // For reconnection state
    private int _streamQuality = 70;
    private int _streamFpsMax = 15;
    private uint _frameSeq;
    private DateTimeOffset _captureStart;
    private CancellationTokenSource? _captureCts;
    private Task? _captureTask;

    public AgentService(
        ILogger<AgentService> logger,
        IOptions<AgentConfig> config,
        WebSocketClient wsClient,
        MouseSimulator mouse,
        KeyboardSimulator keyboard,
        SystemInfo systemInfo,
        ILogger<DxgiCapture> dxgiLogger,
        ILogger<GdiCapture> gdiLogger,
        ILogger<SessionCapture> sessionLogger)
    {
        _logger = logger;
        _config = config.Value;
        _wsClient = wsClient;
        _mouse = mouse;
        _keyboard = keyboard;
        _systemInfo = systemInfo;
        _dxgiLogger = dxgiLogger;
        _gdiLogger = gdiLogger;
        _sessionLogger = sessionLogger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Gruppen Remote Agent starting. AgentId={AgentId}, ConsoleUrl={Url}",
            _config.AgentId, _config.ConsoleUrl);

        InitializeScreenCapture();

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _wsClient.ConnectWithRetryAsync(_config.ConsoleUrl, stoppingToken);
                await AuthenticateAsync(stoppingToken);

                // Start heartbeat loop in background
                using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                var heartbeatTask = HeartbeatLoopAsync(heartbeatCts.Token);

                // If we were streaming before disconnect, resume
                if (_wasStreaming)
                {
                    _logger.LogInformation("Resuming stream after reconnection.");
                    StartCapture(_streamQuality, _streamFpsMax);
                }

                // Main receive loop
                await ReceiveLoopAsync(stoppingToken);

                // Connection lost — cancel heartbeat
                heartbeatCts.Cancel();
                await heartbeatTask;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Connection error. Will reconnect...");
                StopCapture();
                _wasStreaming = _isStreaming;
            }
        }

        StopCapture();
        await _wsClient.CloseAsync(CancellationToken.None);
        _screenCapture?.Dispose();
        _logger.LogInformation("Gruppen Remote Agent stopped.");
    }

    private void InitializeScreenCapture()
    {
        // Session 0 (service session) has no interactive desktop — CopyFromScreen
        // will fail with "The handle is invalid". Use a helper process in the user
        // session to do the actual capture and relay frames via named pipe.
        if (SessionProcess.IsSession0())
        {
            _screenCapture = new SessionCapture(_sessionLogger);
            _logger.LogInformation("Running in Session 0. Screen capture will use a helper in the user session.");
            return;
        }

        // Direct capture (running in a user session — e.g. during development)
        try
        {
            var dxgi = new DxgiCapture(_dxgiLogger);
            if (dxgi.IsAvailable())
            {
                _screenCapture = dxgi;
                _logger.LogInformation("Using DXGI Desktop Duplication for screen capture.");
                return;
            }
            dxgi.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "DXGI not available, falling back to GDI+.");
        }

        _screenCapture = new GdiCapture(_gdiLogger);
        _logger.LogInformation("Using GDI+ fallback for screen capture.");
    }

    private async Task AuthenticateAsync(CancellationToken ct)
    {
        _logger.LogInformation("Sending AUTH...");

        var authPayload = new AuthPayload
        {
            AgentId = _config.AgentId,
            Token = _config.AgentToken,
            Hostname = _systemInfo.GetHostname(),
            OsInfo = _systemInfo.GetOsInfo()
        };

        var msg = BinaryProtocol.EncodeJson(MessageTypes.AUTH, authPayload);
        await _wsClient.SendAsync(msg, ct);

        // Wait for AUTH_OK with 10s timeout
        using var authCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        authCts.CancelAfter(TimeSpan.FromSeconds(10));

        var response = await _wsClient.ReceiveMessageAsync(authCts.Token);
        if (response is null)
            throw new InvalidOperationException("Connection closed during authentication.");

        var (type, payload) = response.Value;

        if (type == MessageTypes.AUTH_OK)
        {
            var authOk = BinaryProtocol.DeserializePayload<AuthOkResponse>(payload);
            _logger.LogInformation("Authenticated as {AgentId}.", authOk.AgentId);
        }
        else if (type == MessageTypes.ERROR)
        {
            var error = BinaryProtocol.DeserializePayload<ErrorResponse>(payload);
            throw new InvalidOperationException($"Authentication failed: {error.Code} - {error.Message}");
        }
        else
        {
            throw new InvalidOperationException($"Unexpected message type 0x{type:X2} during auth.");
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _wsClient.IsConnected)
        {
            var response = await _wsClient.ReceiveMessageAsync(ct);
            if (response is null)
            {
                _logger.LogWarning("Connection lost.");
                _wasStreaming = _isStreaming;
                break;
            }

            var (type, payload) = response.Value;

            switch (type)
            {
                case MessageTypes.START_STREAM:
                    var config = BinaryProtocol.DeserializePayload<StartStreamConfig>(payload);
                    _logger.LogInformation("START_STREAM: quality={Quality}, fps_max={Fps}", config.Quality, config.FpsMax);
                    StartCapture(config.Quality, config.FpsMax);
                    break;

                case MessageTypes.STOP_STREAM:
                    _logger.LogInformation("STOP_STREAM received.");
                    StopCapture();
                    break;

                case MessageTypes.MOUSE_EVENT:
                    var mouseEvt = BinaryProtocol.DeserializePayload<MouseEvent>(payload);
                    if (_screenCapture is SessionCapture sessionMouse)
                        sessionMouse.SendMouseEvent(mouseEvt);
                    else
                        _mouse.Simulate(mouseEvt);
                    break;

                case MessageTypes.KEY_EVENT:
                    var keyEvt = BinaryProtocol.DeserializePayload<KeyEvent>(payload);
                    if (_screenCapture is SessionCapture sessionKey)
                        sessionKey.SendKeyEvent(keyEvt);
                    else
                        _keyboard.Simulate(keyEvt);
                    break;

                case MessageTypes.LIST_SESSIONS:
                    await HandleListSessionsAsync(ct);
                    break;

                case MessageTypes.SELECT_SESSION:
                    HandleSelectSession(payload);
                    break;

                case MessageTypes.NOTIFY_REMOTE:
                    HandleNotifyRemote(payload);
                    break;

                case MessageTypes.HEARTBEAT_ACK:
                    _logger.LogDebug("HEARTBEAT_ACK received.");
                    break;

                case MessageTypes.ERROR:
                    var error = BinaryProtocol.DeserializePayload<ErrorResponse>(payload);
                    _logger.LogError("Server error: {Code} - {Message}", error.Code, error.Message);
                    break;

                default:
                    _logger.LogWarning("Unknown message type: 0x{Type:X2}", type);
                    break;
            }
        }
    }

    // -----------------------------------------------------------------------
    // LIST_SESSIONS handler
    // -----------------------------------------------------------------------
    private async Task HandleListSessionsAsync(CancellationToken ct)
    {
        _logger.LogInformation("LIST_SESSIONS received, enumerating sessions...");
        try
        {
            var sessions = WtsSessionManager.GetSessions();
            var response = new SessionListPayload { Sessions = sessions };
            var msg = BinaryProtocol.EncodeJson(MessageTypes.SESSION_LIST, response);
            await _wsClient.SendAsync(msg, ct);
            _logger.LogInformation("SESSION_LIST sent with {Count} session(s).", sessions.Length);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error enumerating sessions.");
        }
    }

    // -----------------------------------------------------------------------
    // SELECT_SESSION handler
    // -----------------------------------------------------------------------
    private void HandleSelectSession(ReadOnlyMemory<byte> payload)
    {
        var req = BinaryProtocol.DeserializePayload<SelectSessionPayload>(payload);

        if (req.SessionId.HasValue)
        {
            _logger.LogInformation("SELECT_SESSION: switching to session {SessionId}.", req.SessionId.Value);
            if (_screenCapture is SessionCapture session)
            {
                session.SwitchToSession(req.SessionId.Value);
            }
            else
            {
                _logger.LogWarning("SELECT_SESSION ignored: not running in Session 0 mode.");
            }
        }
        else if (req.Credentials is not null)
        {
            _logger.LogInformation("SELECT_SESSION: credentials received for {Domain}\\{User}. " +
                "Programmatic session creation is not yet supported — " +
                "the user must log in via RDP or console.",
                req.Credentials.Domain, req.Credentials.User);
        }
        else
        {
            _logger.LogWarning("SELECT_SESSION: no session_id or credentials provided.");
        }
    }

    // -----------------------------------------------------------------------
    // NOTIFY_REMOTE handler
    // -----------------------------------------------------------------------
    private void HandleNotifyRemote(ReadOnlyMemory<byte> payload)
    {
        var notify = BinaryProtocol.DeserializePayload<NotifyRemotePayload>(payload);
        _logger.LogInformation("NOTIFY_REMOTE: technician={Name}, connected={Connected}",
            notify.TechnicianName, notify.Connected);

        if (_screenCapture is SessionCapture session)
        {
            session.SendNotification(notify);
        }
        else
        {
            _logger.LogWarning("NOTIFY_REMOTE ignored: not running in Session 0 mode.");
        }
    }

    private void StartCapture(int quality, int fpsMax)
    {
        StopCapture();

        _streamQuality = quality;
        _streamFpsMax = fpsMax;
        _frameSeq = 0;
        _captureStart = DateTimeOffset.UtcNow;
        _isStreaming = true;
        _wasStreaming = true;
        _captureCts = new CancellationTokenSource();
        _captureTask = CaptureLoopAsync(_captureCts.Token);
    }

    private void StopCapture()
    {
        if (!_isStreaming) return;

        _isStreaming = false;
        _captureCts?.Cancel();

        try
        {
            _captureTask?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            // Expected on cancellation
        }

        _captureCts?.Dispose();
        _captureCts = null;
        _captureTask = null;
        _logger.LogInformation("Screen capture stopped.");
    }

    private async Task CaptureLoopAsync(CancellationToken ct)
    {
        int frameIntervalMs = 1000 / _streamFpsMax;
        _logger.LogInformation("Capture loop started: quality={Q}, interval={I}ms", _streamQuality, frameIntervalMs);

        while (!ct.IsCancellationRequested)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();

            try
            {
                if (_screenCapture is null) break;

                byte[] jpeg = _screenCapture.CaptureScreen(_streamQuality);

                // Frame skipping: empty array means screen unchanged
                if (jpeg.Length == 0)
                {
                    sw.Stop();
                    int skipDelay = frameIntervalMs - (int)sw.ElapsedMilliseconds;
                    if (skipDelay > 0) await Task.Delay(skipDelay, ct);
                    continue;
                }

                _frameSeq++;
                uint tsMs = (uint)(DateTimeOffset.UtcNow - _captureStart).TotalMilliseconds;

                byte[] frameMsg = BinaryProtocol.EncodeFrame(_frameSeq, tsMs, jpeg);
                await _wsClient.SendAsync(frameMsg, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error capturing/sending frame.");
                break;
            }

            sw.Stop();
            int elapsed = (int)sw.ElapsedMilliseconds;
            int delay = frameIntervalMs - elapsed;
            if (delay > 0)
                await Task.Delay(delay, ct);
        }
    }

    private async Task HeartbeatLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(30), ct);

                var heartbeat = new HeartbeatPayload
                {
                    Uptime = _systemInfo.GetUptimeSeconds(),
                    Cpu = _systemInfo.GetCpuUsage(),
                    Mem = _systemInfo.GetMemoryUsage()
                };

                var msg = BinaryProtocol.EncodeJson(MessageTypes.HEARTBEAT, heartbeat);
                await _wsClient.SendAsync(msg, ct);
                _logger.LogDebug("HEARTBEAT sent: uptime={Up}s cpu={Cpu}% mem={Mem}%",
                    heartbeat.Uptime, heartbeat.Cpu, heartbeat.Mem);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error sending heartbeat.");
                break;
            }
        }
    }
}
