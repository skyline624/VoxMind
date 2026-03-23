using Google.Protobuf;
using Grpc.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using VoxMind.ClientGrpc;
using VoxMind.ClientLite.ClientServices.AudioCapture;
using VoxMind.ClientLite.Configuration;

namespace VoxMind.ClientLite.ClientServices;

/// <summary>Stream l'audio capturé vers le serveur et reçoit les commandes serveur.</summary>
public class AudioStreamClient : BackgroundService
{
    private readonly VoxMindClientService.VoxMindClientServiceClient _grpc;
    private readonly NativeAudioCapture _audioCapture;
    private readonly ClientConfiguration _config;
    private readonly ILogger<AudioStreamClient> _logger;

    private AsyncClientStreamingCall<AudioChunkMessage, StreamAudioResponse>? _streamCall;
    private readonly object _streamLock = new();

    public AudioStreamClient(
        VoxMindClientService.VoxMindClientServiceClient grpc,
        NativeAudioCapture audioCapture,
        ClientConfiguration config,
        ILogger<AudioStreamClient> logger)
    {
        _grpc = grpc;
        _audioCapture = audioCapture;
        _config = config;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Démarrer le stream audio vers le serveur
        _streamCall = _grpc.StreamAudio(cancellationToken: stoppingToken);

        // Abonner la capture audio au stream
        _audioCapture.AudioDataAvailable += OnAudioData;

        // Démarrer la capture
        await _audioCapture.StartAsync(stoppingToken);

        // Démarrer la réception des commandes serveur en parallèle
        var commandTask = ReceiveCommandsAsync(stoppingToken);

        _logger.LogInformation("Streaming audio démarré vers le serveur.");

        await commandTask;
    }

    private async void OnAudioData(object? sender, byte[] wavData)
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
                _logger.LogInformation("Commande reçue du serveur : {Command} | {Payload}", cmd.Command, cmd.PayloadJson);
                // La gestion des commandes (START, STOP, etc.) serait ici
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
        _audioCapture.AudioDataAvailable -= OnAudioData;
        await _audioCapture.StopAsync();

        if (_streamCall is not null)
        {
            await _streamCall.RequestStream.CompleteAsync();
            _streamCall.Dispose();
        }

        await base.StopAsync(cancellationToken);
    }
}
