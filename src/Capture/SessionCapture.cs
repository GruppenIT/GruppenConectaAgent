using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using GruppenRemoteAgent.Protocol;
using Microsoft.Extensions.Logging;

namespace GruppenRemoteAgent.Capture;

/// <summary>
/// Screen capture that works from Session 0 by launching a helper process
/// in the interactive user session and communicating via named pipe.
///
/// Capture pipe (bidirectional, request/response per frame):
///   Service  → Helper : [1 byte quality (1-100)]
///   Helper   → Service: [4 bytes BE length][N bytes JPEG data]  (length 0 = no change)
///
/// Input pipe (one-way, service → helper):
///   [1 byte type][4 bytes BE length][N bytes JSON]
///   type 1 = mouse, type 2 = key, type 3 = notify
/// </summary>
public class SessionCapture : IScreenCapture
{
    private const byte INPUT_TYPE_MOUSE = 1;
    private const byte INPUT_TYPE_KEY = 2;
    private const byte INPUT_TYPE_NOTIFY = 3;

    private readonly ILogger _logger;
    private NamedPipeServerStream? _capturePipe;
    private NamedPipeServerStream? _inputPipe;
    private string? _capturePipeName;
    private string? _inputPipeName;
    private readonly object _inputLock = new();
    private int? _targetSessionId;

    public SessionCapture(ILogger<SessionCapture> logger)
    {
        _logger = logger;
    }

    public bool IsAvailable() => SessionProcess.IsSession0();

    public byte[] CaptureScreen(int jpegQuality)
    {
        EnsureHelperRunning();

        // Request: send quality byte
        _capturePipe!.WriteByte((byte)Math.Clamp(jpegQuality, 1, 100));
        _capturePipe.Flush();

        // Response: 4 bytes BE length + JPEG data (length 0 = no change)
        byte[] lenBuf = new byte[4];
        _capturePipe.ReadExactly(lenBuf, 0, 4);
        int len = (lenBuf[0] << 24) | (lenBuf[1] << 16) | (lenBuf[2] << 8) | lenBuf[3];

        if (len == 0)
            return Array.Empty<byte>(); // No screen change

        if (len < 0 || len > 10 * 1024 * 1024)
            throw new InvalidOperationException($"Invalid frame length from helper: {len}");

        byte[] jpeg = new byte[len];
        _capturePipe.ReadExactly(jpeg, 0, len);
        return jpeg;
    }

    /// <summary>
    /// Sends a mouse event to the helper for execution in the user session.
    /// </summary>
    public void SendMouseEvent(MouseEvent evt)
    {
        SendInputCommand(INPUT_TYPE_MOUSE, evt);
    }

    /// <summary>
    /// Sends a key event to the helper for execution in the user session.
    /// </summary>
    public void SendKeyEvent(KeyEvent evt)
    {
        SendInputCommand(INPUT_TYPE_KEY, evt);
    }

    /// <summary>
    /// Sends a notification command to the helper for display in the user session.
    /// </summary>
    public void SendNotification(NotifyRemotePayload payload)
    {
        SendInputCommand(INPUT_TYPE_NOTIFY, payload);
    }

    /// <summary>
    /// Switch the capture helper to a specific session.
    /// Tears down the current helper and restarts in the target session.
    /// </summary>
    public void SwitchToSession(int sessionId)
    {
        _logger.LogInformation("Switching capture to session {SessionId}.", sessionId);
        _targetSessionId = sessionId;

        // Force helper restart on next CaptureScreen call
        _capturePipe?.Dispose();
        _capturePipe = null;
        _inputPipe?.Dispose();
        _inputPipe = null;
    }

    private void SendInputCommand<T>(byte type, T payload)
    {
        lock (_inputLock)
        {
            if (_inputPipe is null || !_inputPipe.IsConnected)
            {
                _logger.LogWarning("Input pipe not connected, dropping input event.");
                return;
            }

            try
            {
                byte[] json = JsonSerializer.SerializeToUtf8Bytes(payload);
                byte[] header = new byte[5];
                header[0] = type;
                header[1] = (byte)(json.Length >> 24);
                header[2] = (byte)(json.Length >> 16);
                header[3] = (byte)(json.Length >> 8);
                header[4] = (byte)json.Length;

                _inputPipe.Write(header, 0, 5);
                _inputPipe.Write(json, 0, json.Length);
                _inputPipe.Flush();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error sending input command to helper.");
            }
        }
    }

    private void EnsureHelperRunning()
    {
        if (_capturePipe is not null && _capturePipe.IsConnected)
            return;

        // Clean up any previous pipes
        _capturePipe?.Dispose();
        _inputPipe?.Dispose();

        var guid = Guid.NewGuid().ToString("N");
        _capturePipeName = $"gruppen-capture-{guid}";
        _inputPipeName = $"gruppen-input-{guid}";

        var security = CreatePipeSecurity();

        _capturePipe = NamedPipeServerStreamAcl.Create(
            _capturePipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.None,
            inBufferSize: 0, outBufferSize: 1024 * 1024,
            pipeSecurity: security);

        _inputPipe = NamedPipeServerStreamAcl.Create(
            _inputPipeName, PipeDirection.Out, 1,
            PipeTransmissionMode.Byte, PipeOptions.None,
            inBufferSize: 0, outBufferSize: 64 * 1024,
            pipeSecurity: security);

        string exePath = Environment.ProcessPath!;
        string cmdLine = $"\"{exePath}\" --capture-helper {_capturePipeName} {_inputPipeName}";

        if (_targetSessionId.HasValue)
            SessionProcess.LaunchInSession((uint)_targetSessionId.Value, cmdLine, _logger);
        else
            SessionProcess.LaunchInUserSession(cmdLine, _logger);

        // Wait for the helper to connect both pipes (10 s timeout)
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            _capturePipe.WaitForConnectionAsync(cts.Token).Wait(cts.Token);
            _inputPipe.WaitForConnectionAsync(cts.Token).Wait(cts.Token);
        }
        catch (OperationCanceledException)
        {
            _capturePipe.Dispose();
            _capturePipe = null;
            _inputPipe.Dispose();
            _inputPipe = null;
            throw new TimeoutException("Capture helper did not connect within 10 seconds.");
        }

        _logger.LogInformation("Capture helper connected on pipes capture={Capture}, input={Input}.",
            _capturePipeName, _inputPipeName);
    }

    private static PipeSecurity CreatePipeSecurity()
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
            PipeAccessRights.ReadWrite,
            AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl,
            AccessControlType.Allow));
        return security;
    }

    public void Dispose()
    {
        _capturePipe?.Dispose();
        _capturePipe = null;
        _inputPipe?.Dispose();
        _inputPipe = null;
    }
}
