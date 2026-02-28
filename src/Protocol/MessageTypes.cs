namespace GruppenRemoteAgent.Protocol;

public static class MessageTypes
{
    public const byte AUTH = 0x01;
    public const byte AUTH_OK = 0x02;
    public const byte START_STREAM = 0x03;
    public const byte FRAME = 0x04;
    public const byte MOUSE_EVENT = 0x05;
    public const byte KEY_EVENT = 0x06;
    public const byte STOP_STREAM = 0x07;
    public const byte HEARTBEAT = 0x08;
    public const byte HEARTBEAT_ACK = 0x09;
    public const byte ERROR = 0xFF;
}
