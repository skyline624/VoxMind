using Microsoft.Extensions.Logging;
using VoxMind.Core.Transcription;

namespace VoxMind.Core.Tts;

/// <summary>
/// Stub Coqui XTTS-v2 — voie d'extension future si on veut un moteur cloning
/// multi-locuteurs natif (XTTS gère le cross-lingual sur 17 langues mais
/// nécessite un service Python car aucun export ONNX officiel n'existe).
///
/// Pattern dupliqué de <see cref="VoxMind.Core.Transcription.CohereTranscriptionService"/>.
/// </summary>
public sealed class CoquiXttsTtsService : ITtsService
{
    private readonly ILogger<CoquiXttsTtsService> _logger;

    public TtsModelInfo Info => new()
    {
        EngineName = "xtts-v2",
        Backend = ComputeBackend.CPU,
        IsLoaded = false,
        AvailableLanguages = Array.Empty<string>(),
    };

    public CoquiXttsTtsService(ILogger<CoquiXttsTtsService> logger)
    {
        _logger = logger;
    }

    public Task<TtsResult> SynthesizeAsync(
        string text,
        string? language = null,
        byte[]? referenceWav = null,
        string? referenceText = null,
        CancellationToken ct = default)
    {
        _logger.LogError(
            "Coqui XTTS-v2 requiert un service Python (pas d'export ONNX officiel). " +
            "Voir python_services/ pour le template d'intégration gRPC.");
        throw new NotSupportedException(
            "Coqui XTTS-v2 n'est pas disponible en mode local ONNX. " +
            "Voir python_services/xtts_server.py pour l'intégration Python gRPC.");
    }

    public void Dispose() { }
}
