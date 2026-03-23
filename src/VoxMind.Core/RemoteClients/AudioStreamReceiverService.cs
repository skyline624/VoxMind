using Grpc.Core;
using Microsoft.Extensions.Logging;
using VoxMind.ClientGrpc;
using VoxMind.Core.Configuration;
using VoxMind.Core.Session;

namespace VoxMind.Core.RemoteClients;

/// <summary>Service gRPC côté serveur : reçoit les connexions des VoxMindClientLite.</summary>
public class AudioStreamReceiverService : VoxMindClientService.VoxMindClientServiceBase
{
    private readonly IRemoteClientRegistry _registry;
    private readonly ISessionManager _sessionManager;
    private readonly RemoteClientsConfig _config;
    private readonly ILogger<AudioStreamReceiverService> _logger;

    public AudioStreamReceiverService(
        IRemoteClientRegistry registry,
        ISessionManager sessionManager,
        AppConfiguration appConfig,
        ILogger<AudioStreamReceiverService> logger)
    {
        _registry = registry;
        _sessionManager = sessionManager;
        _config = appConfig.RemoteClients;
        _logger = logger;
    }

    public override Task<RegisterResponse> RegisterClient(RegisterRequest request, ServerCallContext context)
    {
        if (request.AuthToken != _config.SharedToken)
        {
            _logger.LogWarning("Tentative d'inscription refusée pour le client '{Name}' (token invalide).", request.ClientName);
            return Task.FromResult(new RegisterResponse { Success = false, Error = "Token d'authentification invalide." });
        }

        var client = new RemoteClientInfo
        {
            ClientId = request.ClientId,
            Name = request.ClientName,
            Platform = request.Platform
        };
        _registry.Register(client);
        return Task.FromResult(new RegisterResponse { Success = true });
    }

    public override Task<Empty> UnregisterClient(UnregisterRequest request, ServerCallContext context)
    {
        _registry.Unregister(request.ClientId);
        return Task.FromResult(new Empty());
    }

    public override Task<HeartbeatResponse> SendHeartbeat(HeartbeatRequest request, ServerCallContext context)
    {
        _registry.UpdateHeartbeat(request.ClientId);
        return Task.FromResult(new HeartbeatResponse { Alive = true });
    }

    public override async Task<StreamAudioResponse> StreamAudio(
        IAsyncStreamReader<AudioChunkMessage> requestStream,
        ServerCallContext context)
    {
        long chunksReceived = 0;
        var ct = context.CancellationToken;

        await foreach (var chunk in requestStream.ReadAllAsync(ct))
        {
            if (!_registry.Exists(chunk.ClientId))
            {
                _logger.LogWarning("Chunk audio rejeté : client '{Id}' non enregistré.", chunk.ClientId);
                continue;
            }

            try
            {
                await _sessionManager.InjectAudioChunkAsync(chunk.AudioData.ToByteArray(), ct);
                chunksReceived++;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Erreur lors de l'injection du chunk audio depuis '{Id}'.", chunk.ClientId);
            }
        }

        return new StreamAudioResponse { Accepted = true, ChunksReceived = chunksReceived };
    }

    public override async Task SubscribeCommands(
        SubscribeRequest request,
        IServerStreamWriter<ServerCommand> responseStream,
        ServerCallContext context)
    {
        if (request.AuthToken != _config.SharedToken)
        {
            _logger.LogWarning("SubscribeCommands refusé pour '{Id}' (token invalide).", request.ClientId);
            return;
        }

        var client = _registry.Get(request.ClientId);
        if (client is null)
        {
            _logger.LogWarning("SubscribeCommands refusé : client '{Id}' non enregistré.", request.ClientId);
            return;
        }

        var ct = context.CancellationToken;
        _logger.LogInformation("Client '{Name}' abonné aux commandes serveur.", client.Name);

        await foreach (var cmd in client.CommandChannel.Reader.ReadAllAsync(ct))
        {
            try
            {
                await responseStream.WriteAsync(cmd, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _logger.LogInformation("Flux de commandes terminé pour '{Name}'.", client.Name);
    }
}
