namespace VoxMind.ClientLite.Configuration;

public class ClientConfiguration
{
    public string ClientId { get; set; } = Guid.NewGuid().ToString();
    public string ClientName { get; set; } = Environment.MachineName;
    public string ServerAddress { get; set; } = "http://localhost:50052";
    public string SharedToken { get; set; } = "changeme";
    public string AudioSource { get; set; } = "microphone"; // "microphone" | "system"
    public int HeartbeatIntervalSeconds { get; set; } = 10;
}
