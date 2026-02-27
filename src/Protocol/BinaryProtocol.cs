using System.Buffers.Binary;
using System.Text.Json;

namespace GruppenRemoteAgent.Protocol;

public static class BinaryProtocol
{
    public const int HeaderSize = 5;

    public static byte[] Encode(byte type, byte[] payload)
    {
        var msg = new byte[HeaderSize + payload.Length];
        msg[0] = type;
        BinaryPrimitives.WriteUInt32BigEndian(msg.AsSpan(1), (uint)payload.Length);
        payload.CopyTo(msg, HeaderSize);
        return msg;
    }

    public static byte[] EncodeJson<T>(byte type, T obj)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(obj);
        return Encode(type, payload);
    }

    public static byte[] EncodeFrame(uint seq, uint timestampMs, byte[] jpegData)
    {
        var payload = new byte[8 + jpegData.Length];
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(0), seq);
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(4), timestampMs);
        jpegData.CopyTo(payload, 8);
        return Encode(MessageTypes.FRAME, payload);
    }

    public static (byte Type, ReadOnlyMemory<byte> Payload) Decode(byte[] buffer, int length)
    {
        if (length < HeaderSize)
            throw new InvalidOperationException("Message too short to contain header.");

        byte type = buffer[0];
        uint payloadSize = BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(1));

        if (length < HeaderSize + (int)payloadSize)
            throw new InvalidOperationException("Message shorter than declared payload size.");

        var payload = new ReadOnlyMemory<byte>(buffer, HeaderSize, (int)payloadSize);
        return (type, payload);
    }

    public static T DeserializePayload<T>(ReadOnlyMemory<byte> payload)
    {
        return JsonSerializer.Deserialize<T>(payload.Span)
               ?? throw new InvalidOperationException($"Failed to deserialize payload to {typeof(T).Name}.");
    }
}
