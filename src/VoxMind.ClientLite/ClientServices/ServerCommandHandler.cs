using Microsoft.Extensions.Logging;
using System.Text.Json;
using VoxMind.Grpc;
using VoxMind.ClientLite.ClientServices.AudioCapture;

namespace VoxMind.ClientLite.ClientServices;

public interface IServerCommandHandler
{
    Task HandleAsync(ServerCommand command, CancellationToken ct = default);
}

/// <summary>Interprète et exécute les commandes reçues du serveur VoxMind.</summary>
public class ServerCommandHandler : IServerCommandHandler
{
    private readonly NativeAudioCapture _audioCapture;
    private readonly ILogger<ServerCommandHandler> _logger;

    public event EventHandler? StartListeningRequested;
    public event EventHandler? StopListeningRequested;

    public ServerCommandHandler(
        NativeAudioCapture audioCapture,
        ILogger<ServerCommandHandler> logger)
    {
        _audioCapture = audioCapture;
        _logger = logger;
    }

    public async Task HandleAsync(ServerCommand command, CancellationToken ct = default)
    {
        _logger.LogInformation("Commande serveur reçue : {Command} | payload={Payload}",
            command.Command, command.PayloadJson);

        switch (command.Command.ToUpperInvariant())
        {
            case "START":
                await HandleStartAsync(command.PayloadJson, ct);
                break;

            case "STOP":
                await HandleStopAsync(ct);
                break;

            case "CONFIGURE":
                HandleConfigure(command.PayloadJson);
                break;

            default:
                _logger.LogWarning("Commande serveur inconnue : '{Command}'.", command.Command);
                break;
        }
    }

    private async Task HandleStartAsync(string? payloadJson, CancellationToken ct)
    {
        string audioSource = "microphone";
        if (!string.IsNullOrEmpty(payloadJson))
        {
            try
            {
                var payload = JsonSerializer.Deserialize<JsonElement>(payloadJson);
                if (payload.TryGetProperty("source", out var src))
                    audioSource = src.GetString() ?? "microphone";
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Impossible de désérialiser le payload START.");
            }
        }

        _logger.LogInformation("Démarrage capture audio (source: {Source}).", audioSource);
        StartListeningRequested?.Invoke(this, EventArgs.Empty);
        await _audioCapture.StartAsync(audioSource, ct);
    }

    private async Task HandleStopAsync(CancellationToken ct)
    {
        _logger.LogInformation("Arrêt capture audio sur commande serveur.");
        await _audioCapture.StopAsync();
        StopListeningRequested?.Invoke(this, EventArgs.Empty);
    }

    private void HandleConfigure(string? payloadJson)
    {
        if (string.IsNullOrEmpty(payloadJson)) return;
        try
        {
            var payload = JsonSerializer.Deserialize<JsonElement>(payloadJson);
            if (payload.TryGetProperty("sample_rate", out var sr))
                _logger.LogInformation("Configuration reçue — sample_rate={Rate} (non appliqué dynamiquement).", sr.GetInt32());
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Impossible de désérialiser le payload CONFIGURE.");
        }
    }
}
