using System.Net.WebSockets;
using GruppenRemoteAgent.Protocol;
using Microsoft.Extensions.Logging;

namespace GruppenRemoteAgent.Network;

public class WebSocketClient : IDisposable
{
    private readonly ILogger<WebSocketClient> _logger;
    private ClientWebSocket? _ws;
    private readonly byte[] _receiveBuffer = new byte[1024 * 1024]; // 1 MB
    private bool _disposed;

    public bool IsConnected => _ws?.State == WebSocketState.Open;

    public WebSocketClient(ILogger<WebSocketClient> logger)
    {
        _logger = logger;
    }

    public async Task ConnectAsync(string url, CancellationToken ct)
    {
        _ws?.Dispose();
        _ws = new ClientWebSocket();
        _ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);

        _logger.LogInformation("Connecting to {Url}...", url);
        await _ws.ConnectAsync(new Uri(url), ct);
        _logger.LogInformation("Connected to console.");
    }

    public async Task ConnectWithRetryAsync(string url, CancellationToken ct)
    {
        int attempt = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await ConnectAsync(url, ct);
                return;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                attempt++;
                int delaySec = Math.Min((int)Math.Pow(2, attempt), 60);
                _logger.LogWarning(ex, "Connection attempt {Attempt} failed. Retrying in {Delay}s...", attempt, delaySec);
                await Task.Delay(TimeSpan.FromSeconds(delaySec), ct);
            }
        }
    }

    public async Task SendAsync(byte[] data, CancellationToken ct)
    {
        if (_ws is null || _ws.State != WebSocketState.Open)
            throw new InvalidOperationException("WebSocket is not connected.");

        await _ws.SendAsync(
            new ArraySegment<byte>(data),
            WebSocketMessageType.Binary,
            endOfMessage: true,
            cancellationToken: ct);
    }

    public async Task<(byte Type, ReadOnlyMemory<byte> Payload)?> ReceiveMessageAsync(CancellationToken ct)
    {
        if (_ws is null || _ws.State != WebSocketState.Open)
            return null;

        int totalBytes = 0;
        WebSocketReceiveResult result;

        do
        {
            result = await _ws.ReceiveAsync(
                new ArraySegment<byte>(_receiveBuffer, totalBytes, _receiveBuffer.Length - totalBytes),
                ct);

            if (result.MessageType == WebSocketMessageType.Close)
            {
                _logger.LogWarning("Server closed the connection: {Status} {Description}",
                    result.CloseStatus, result.CloseStatusDescription);
                return null;
            }

            totalBytes += result.Count;
        }
        while (!result.EndOfMessage);

        if (totalBytes < BinaryProtocol.HeaderSize)
        {
            _logger.LogWarning("Received message too short ({Bytes} bytes).", totalBytes);
            return null;
        }

        var (type, payload) = BinaryProtocol.Decode(_receiveBuffer, totalBytes);
        return (type, payload);
    }

    public async Task CloseAsync(CancellationToken ct)
    {
        if (_ws is not null && _ws.State == WebSocketState.Open)
        {
            try
            {
                await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Agent shutting down", ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error closing WebSocket.");
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _ws?.Dispose();
    }
}
