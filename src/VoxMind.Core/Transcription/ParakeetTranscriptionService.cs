using Google.Protobuf;
using Grpc.Net.Client;
using Microsoft.Extensions.Logging;
using VoxMind.Grpc;
using WellKnownEmpty = Google.Protobuf.WellKnownTypes.Empty;

namespace VoxMind.Core.Transcription;

/// <summary>Service de transcription utilisant NVIDIA NeMo Parakeet via gRPC.</summary>
public class ParakeetTranscriptionService : ITranscriptionService
{
    private readonly ParakeetTranscription.ParakeetTranscriptionClient _client;
    private readonly GrpcChannel _channel;
    private readonly ILogger<ParakeetTranscriptionService> _logger;
    private ModelInfo _info;

    public ModelInfo Info => _info;

    public ParakeetTranscriptionService(string endpoint, ILogger<ParakeetTranscriptionService> logger)
    {
        _logger = logger;
        _channel = GrpcChannel.ForAddress($"http://{endpoint}");
        _client = new ParakeetTranscription.ParakeetTranscriptionClient(_channel);
        _info = new ModelInfo
        {
            ModelName = "parakeet-ctc-1.1b",
            Size = ModelSize.Large,
            Backend = ComputeBackend.CPU,
            IsLoaded = false
        };
    }

    public async Task<TranscriptionResult> TranscribeChunkAsync(byte[] audioData, CancellationToken ct = default)
    {
        try
        {
            var request = new ParakeetAudioData
            {
                AudioData = ByteString.CopyFrom(audioData),
                SampleRate = 16000
            };

            var response = await _client.TranscribeAsync(request, cancellationToken: ct);

            return new TranscriptionResult
            {
                Text = response.Text,
                Language = string.IsNullOrEmpty(response.Language) ? "en" : response.Language,
                Confidence = response.Confidence > 0 ? response.Confidence : 0.9f,
                Duration = TimeSpan.Zero,
                Segments = new List<TranscriptionSegment>
                {
                    new() { Id = 0, Text = response.Text, Confidence = response.Confidence }
                }
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Erreur lors de la transcription Parakeet.");
            return new TranscriptionResult { Text = string.Empty };
        }
    }

    public async Task<TranscriptionResult> TranscribeFileAsync(string filePath, CancellationToken ct = default)
    {
        var audioData = await File.ReadAllBytesAsync(filePath, ct);
        return await TranscribeChunkAsync(audioData, ct);
    }

    public Task<string> DetectLanguageAsync(byte[] audioData)
    {
        // Parakeet est optimisé pour l'anglais
        return Task.FromResult("en");
    }

    public async Task LoadModelAsync(ModelSize size, ComputeBackend backend = ComputeBackend.Auto)
    {
        try
        {
            var info = await _client.GetModelInfoAsync(new WellKnownEmpty());
            _info = new ModelInfo
            {
                ModelName = info.ModelName,
                Size = size,
                Backend = backend == ComputeBackend.Auto ? ComputeBackend.CPU : backend,
                IsLoaded = info.IsLoaded
            };
            _logger.LogInformation("Parakeet connecté : {Model} (chargé={Loaded})", info.ModelName, info.IsLoaded);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Impossible de contacter le serveur Parakeet ({Endpoint}).", _channel.Target);
        }
    }

    public void Dispose()
    {
        _channel.Dispose();
    }
}
