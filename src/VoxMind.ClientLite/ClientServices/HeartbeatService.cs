using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using VoxMind.Grpc;
using VoxMind.ClientLite.Configuration;

namespace VoxMind.ClientLite.ClientServices;

public class HeartbeatService : BackgroundService
{
    private readonly VoxMindClientService.VoxMindClientServiceClient _grpc;
    private readonly ClientConfiguration _config;
    private readonly ILogger<HeartbeatService> _logger;

    public HeartbeatService(
        VoxMindClientService.VoxMindClientServiceClient grpc,
        ClientConfiguration config,
        ILogger<HeartbeatService> logger)
    {
        _grpc = grpc;
        _config = config;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(_config.HeartbeatIntervalSeconds);
        _logger.LogDebug("HeartbeatService démarré (intervalle: {Interval}s).", _config.HeartbeatIntervalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _grpc.SendHeartbeatAsync(
                    new HeartbeatRequest { ClientId = _config.ClientId },
                    cancellationToken: stoppingToken);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Heartbeat échoué.");
            }

            await Task.Delay(interval, stoppingToken).ConfigureAwait(false);
        }
    }
}
