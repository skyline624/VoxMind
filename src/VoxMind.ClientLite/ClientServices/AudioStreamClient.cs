using Google.Protobuf;
using Grpc.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using VoxMind.Grpc;
using VoxMind.ClientLite.ClientServices.AudioCapture;
using VoxMind.ClientLite.Configuration;

namespace VoxMind.ClientLite.ClientServices;

/// <summary>Stream l'audio capturé vers le serveur et reçoit les commandes serveur.</summary>
public class AudioStreamClient : BackgroundService
{
    private readonly VoxMindClientService.VoxMindClientServiceClient _grpc;
    private readonly NativeAudioCapture _audioCapture;
    private readonly ServerCommandHandler _commandHandler;
    private readonly ClientConfiguration _config;
    private readonly ILogger<AudioStreamClient> _logger;

    private AsyncClientStreamingCall<AudioChunkMessage, StreamAudioResponse>? _streamCall;

    public AudioStreamClient(
        VoxMindClientService.VoxMindClientServiceClient grpc,
        NativeAudioCapture audioCapture,
        ServerCommandHandler commandHandler,
        ClientConfiguration config,
        ILogger<AudioStreamClient> logger)
    {
        _grpc = grpc;
        _audioCapture = audioCapture;
        _commandHandler = commandHandler;
        _config = config;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Ouvrir le stream audio (la capture démarre sur commande START du serveur)
        _streamCall = _grpc.StreamAudio(cancellationToken: stoppingToken);

        // Abonner la capture au stream — utilise fire-and-forget avec Task pour éviter async void
        _audioCapture.AudioDataAvailable += OnAudioDataSafe;

        // Démarrer automatiquement la capture si configuré
        if (_config.AudioSource != "manual")
            await _audioCapture.StartAsync(stoppingToken);

        // Recevoir les commandes serveur en parallèle
        _logger.LogInformation("Stream audio ouvert. En attente de commandes serveur.");
        await ReceiveCommandsAsync(stoppingToken);
    }

    // Pattern safe : le handler void délègue à une méthode Task, les exceptions sont capturées
    private void OnAudioDataSafe(object? sender, byte[] wavData)
    {
        _ = SendAudioChunkAsync(wavData);
    }

    private async Task SendAudioChunkAsync(byte[] wavData)
    {
        if (_streamCall is null) return;
        try
        {
            await _streamCall.RequestStream.WriteAsync(new AudioChunkMessage
            {
                ClientId = _config.ClientId,
                AudioData = ByteString.CopyFrom(wavData),
                SampleRate = 16000,
                TimestampMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            });
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Cancelled) { }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Erreur lors de l'envoi d'un chunk audio.");
        }
    }

    private async Task ReceiveCommandsAsync(CancellationToken ct)
    {
        try
        {
            using var call = _grpc.SubscribeCommands(new SubscribeRequest
            {
                ClientId = _config.ClientId,
                AuthToken = _config.SharedToken
            }, cancellationToken: ct);

            await foreach (var cmd in call.ResponseStream.ReadAllAsync(ct))
            {
                await _commandHandler.HandleAsync(cmd, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Cancelled) { }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Flux de commandes serveur interrompu.");
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _audioCapture.AudioDataAvailable -= OnAudioDataSafe;
        await _audioCapture.StopAsync();

        if (_streamCall is not null)
        {
            try { await _streamCall.RequestStream.CompleteAsync(); }
            catch { /* ignoré à l'arrêt */ }
            _streamCall.Dispose();
        }

        await base.StopAsync(cancellationToken);
    }
}
