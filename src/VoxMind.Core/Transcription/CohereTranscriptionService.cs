using Microsoft.Extensions.Logging;

namespace VoxMind.Core.Transcription;

/// <summary>
/// Stub pour Cohere Transcribe (CohereLabs/cohere-transcribe-03-2026).
/// Ce modèle est PyTorch 2B params (architecture Conformer), sans export ONNX officiel.
/// Pour une intégration réelle, créer python_services/cohere_transcribe_server.py
/// sur le modèle de pyannote_server.py et appeler via gRPC.
/// </summary>
public class CohereTranscriptionService : ITranscriptionService
{
    private readonly ILogger<CohereTranscriptionService> _logger;

    public ModelInfo Info => new()
    {
        ModelName = "cohere",
        Size = ModelSize.Large,
        Backend = ComputeBackend.Auto,
        IsLoaded = false
    };

    public CohereTranscriptionService(ILogger<CohereTranscriptionService> logger)
    {
        _logger = logger;
    }

    public Task<TranscriptionResult> TranscribeFileAsync(string filePath, CancellationToken ct = default)
    {
        _logger.LogError(
            "Cohere Transcribe requiert un service Python gRPC (2B params PyTorch Conformer). " +
            "Voir python_services/ pour le template de service.");
        throw new NotSupportedException(
            "Cohere Transcribe n'est pas disponible en mode local ONNX. " +
            "Voir python_services/cohere_transcribe_server.py pour l'intégration Python gRPC.");
    }

    public Task<TranscriptionResult> TranscribeChunkAsync(byte[] audioData, CancellationToken ct = default)
        => TranscribeFileAsync(string.Empty, ct);

    public Task<string> DetectLanguageAsync(byte[] audioData)
        => Task.FromResult("unavailable");

    public Task LoadModelAsync(ModelSize size, ComputeBackend backend = ComputeBackend.Auto)
        => Task.CompletedTask;

    public void Dispose() { }
}
