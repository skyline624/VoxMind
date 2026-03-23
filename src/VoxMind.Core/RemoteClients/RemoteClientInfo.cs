using System.Threading.Channels;
using VoxMind.ClientGrpc;

namespace VoxMind.Core.RemoteClients;

public class RemoteClientInfo
{
    public string ClientId { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Platform { get; init; } = string.Empty;
    public DateTime RegisteredAt { get; init; } = DateTime.UtcNow;
    public DateTime LastHeartbeatAt { get; set; } = DateTime.UtcNow;

    /// <summary>Canal pour pousser des commandes vers ce client via SubscribeCommands.</summary>
    public Channel<ServerCommand> CommandChannel { get; } =
        Channel.CreateUnbounded<ServerCommand>(new UnboundedChannelOptions { SingleReader = true });
}
