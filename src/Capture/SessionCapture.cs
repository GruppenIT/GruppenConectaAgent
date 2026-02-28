using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Extensions.Logging;

namespace GruppenRemoteAgent.Capture;

/// <summary>
/// Screen capture that works from Session 0 by launching a helper process
/// in the interactive user session and communicating via named pipe.
///
/// Protocol (request/response per frame):
///   Service  → Helper : [1 byte quality]
///   Helper   → Service: [4 bytes BE length][N bytes JPEG data]
/// </summary>
public class SessionCapture : IScreenCapture
{
    private readonly ILogger _logger;
    private NamedPipeServerStream? _pipe;
    private string? _pipeName;

    public SessionCapture(ILogger<SessionCapture> logger)
    {
        _logger = logger;
    }

    public bool IsAvailable() => SessionProcess.IsSession0();

    public byte[] CaptureScreen(int jpegQuality)
    {
        EnsureHelperRunning();

        // Request: send quality byte
        _pipe!.WriteByte((byte)Math.Clamp(jpegQuality, 1, 100));
        _pipe.Flush();

        // Response: 4 bytes BE length + JPEG data
        byte[] lenBuf = new byte[4];
        _pipe.ReadExactly(lenBuf, 0, 4);
        int len = (lenBuf[0] << 24) | (lenBuf[1] << 16) | (lenBuf[2] << 8) | lenBuf[3];

        if (len <= 0 || len > 10 * 1024 * 1024) // sanity: max 10 MB
            throw new InvalidOperationException($"Invalid frame length from helper: {len}");

        byte[] jpeg = new byte[len];
        _pipe.ReadExactly(jpeg, 0, len);
        return jpeg;
    }

    private void EnsureHelperRunning()
    {
        if (_pipe is not null && _pipe.IsConnected)
            return;

        // Clean up any previous pipe
        _pipe?.Dispose();

        _pipeName = $"gruppen-capture-{Guid.NewGuid():N}";

        // The service runs as LocalSystem (Session 0) but the helper runs as
        // the logged-in user (Session 1+). Grant authenticated users read/write
        // access so the helper can connect to the pipe.
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
            PipeAccessRights.ReadWrite,
            AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl,
            AccessControlType.Allow));

        _pipe = NamedPipeServerStreamAcl.Create(
            _pipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.None,
            inBufferSize: 0, outBufferSize: 1024 * 1024,
            pipeSecurity: security);

        string exePath = Environment.ProcessPath!;
        string cmdLine = $"\"{exePath}\" --capture-helper {_pipeName}";

        SessionProcess.LaunchInUserSession(cmdLine, _logger);

        // Wait for the helper to connect (10 s timeout)
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            _pipe.WaitForConnectionAsync(cts.Token).Wait(cts.Token);
        }
        catch (OperationCanceledException)
        {
            _pipe.Dispose();
            _pipe = null;
            throw new TimeoutException("Capture helper did not connect within 10 seconds.");
        }

        _logger.LogInformation("Capture helper connected on pipe {Pipe}.", _pipeName);
    }

    public void Dispose()
    {
        _pipe?.Dispose();
        _pipe = null;
    }
}
