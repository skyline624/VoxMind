using Microsoft.Extensions.Logging;
using VoxMind.ClientGrpc;
using VoxMind.ClientLite.Configuration;

namespace VoxMind.ClientLite.ClientServices;

public class ClientRegistration
{
    private readonly VoxMindClientService.VoxMindClientServiceClient _grpc;
    private readonly ClientConfiguration _config;
    private readonly ILogger<ClientRegistration> _logger;

    public ClientRegistration(
        VoxMindClientService.VoxMindClientServiceClient grpc,
        ClientConfiguration config,
        ILogger<ClientRegistration> logger)
    {
        _grpc = grpc;
        _config = config;
        _logger = logger;
    }

    public async Task<bool> RegisterAsync(CancellationToken ct = default)
    {
        var response = await _grpc.RegisterClientAsync(new RegisterRequest
        {
            ClientId = _config.ClientId,
            ClientName = _config.ClientName,
            AuthToken = _config.SharedToken,
            Platform = Environment.OSVersion.Platform.ToString()
        }, cancellationToken: ct);

        if (response.Success)
            _logger.LogInformation("Client '{Name}' enregistré auprès du serveur.", _config.ClientName);
        else
            _logger.LogError("Échec de l'inscription : {Error}", response.Error);

        return response.Success;
    }

    public async Task UnregisterAsync(CancellationToken ct = default)
    {
        try
        {
            await _grpc.UnregisterClientAsync(new UnregisterRequest
            {
                ClientId = _config.ClientId,
                AuthToken = _config.SharedToken
            }, cancellationToken: ct);
            _logger.LogInformation("Client '{Name}' désinscrit du serveur.", _config.ClientName);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Erreur lors de la désinscription.");
        }
    }
}
