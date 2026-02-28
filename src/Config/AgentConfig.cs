namespace GruppenRemoteAgent.Config;

public class AgentConfig
{
    public string ConsoleUrl { get; set; } = "ws://localhost:3001/ws/agent";
    public string AgentId { get; set; } = "agent-demo-001";
    public string AgentToken { get; set; } = "demo-token-12345";
    public string LogLevel { get; set; } = "Information";
    public string LogPath { get; set; } = @"C:\ProgramData\Gruppen\RemoteAgent\logs";
}
