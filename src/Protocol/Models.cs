using System.Text.Json.Serialization;

namespace GruppenRemoteAgent.Protocol;

public class AuthPayload
{
    [JsonPropertyName("agent_id")]
    public string AgentId { get; set; } = string.Empty;

    [JsonPropertyName("token")]
    public string Token { get; set; } = string.Empty;

    [JsonPropertyName("hostname")]
    public string Hostname { get; set; } = string.Empty;

    [JsonPropertyName("os_info")]
    public string OsInfo { get; set; } = string.Empty;
}

public class AuthOkResponse
{
    [JsonPropertyName("agent_id")]
    public string AgentId { get; set; } = string.Empty;
}

public class StartStreamConfig
{
    [JsonPropertyName("quality")]
    public int Quality { get; set; } = 70;

    [JsonPropertyName("fps_max")]
    public int FpsMax { get; set; } = 15;
}

public class MouseEvent
{
    [JsonPropertyName("x")]
    public int X { get; set; }

    [JsonPropertyName("y")]
    public int Y { get; set; }

    [JsonPropertyName("button")]
    public int Button { get; set; }

    [JsonPropertyName("action")]
    public string Action { get; set; } = "move";
}

public class KeyEvent
{
    [JsonPropertyName("key")]
    public string Key { get; set; } = string.Empty;

    [JsonPropertyName("action")]
    public string Action { get; set; } = "down";

    [JsonPropertyName("modifiers")]
    public string[] Modifiers { get; set; } = Array.Empty<string>();
}

public class HeartbeatPayload
{
    [JsonPropertyName("uptime")]
    public long Uptime { get; set; }

    [JsonPropertyName("cpu")]
    public double Cpu { get; set; }

    [JsonPropertyName("mem")]
    public double Mem { get; set; }
}

public class ErrorResponse
{
    [JsonPropertyName("code")]
    public string Code { get; set; } = string.Empty;

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;
}

public class SessionInfoDto
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("username")]
    public string Username { get; set; } = string.Empty;

    [JsonPropertyName("state")]
    public string State { get; set; } = string.Empty;

    [JsonPropertyName("is_console")]
    public bool IsConsole { get; set; }
}

public class SessionListPayload
{
    [JsonPropertyName("sessions")]
    public SessionInfoDto[] Sessions { get; set; } = Array.Empty<SessionInfoDto>();
}

public class SelectSessionPayload
{
    [JsonPropertyName("session_id")]
    public int? SessionId { get; set; }

    [JsonPropertyName("credentials")]
    public SessionCredentials? Credentials { get; set; }
}

public class SessionCredentials
{
    [JsonPropertyName("domain")]
    public string Domain { get; set; } = string.Empty;

    [JsonPropertyName("user")]
    public string User { get; set; } = string.Empty;

    [JsonPropertyName("pass")]
    public string Pass { get; set; } = string.Empty;
}

public class NotifyRemotePayload
{
    [JsonPropertyName("technician_name")]
    public string TechnicianName { get; set; } = string.Empty;

    [JsonPropertyName("connected")]
    public bool Connected { get; set; }
}
