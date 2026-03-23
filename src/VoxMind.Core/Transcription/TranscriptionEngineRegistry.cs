namespace VoxMind.Core.Transcription;

/// <summary>
/// Registre des moteurs de transcription disponibles.
/// Sélectionne le bon moteur par nom ("parakeet", "whisper", "cohere")
/// avec fallback sur le moteur par défaut configuré.
/// </summary>
public class TranscriptionEngineRegistry
{
    private readonly IReadOnlyDictionary<string, ITranscriptionService> _engines;
    private readonly string _defaultModel;

    public TranscriptionEngineRegistry(
        IReadOnlyDictionary<string, ITranscriptionService> engines,
        string defaultModel)
    {
        _engines = engines;
        _defaultModel = defaultModel.ToLowerInvariant();
    }

    /// <summary>
    /// Retourne le moteur correspondant au nom demandé.
    /// Si le nom est null ou inconnu, retourne le moteur par défaut.
    /// </summary>
    public ITranscriptionService Get(string? modelName = null)
    {
        var key = (modelName ?? _defaultModel).ToLowerInvariant();
        return _engines.TryGetValue(key, out var engine)
            ? engine
            : _engines[_defaultModel];
    }

    /// <summary>Liste tous les moteurs avec leurs infos (disponibilité, backend, etc.).</summary>
    public IEnumerable<(string Name, ModelInfo Info)> ListAll()
        => _engines.Select(e => (e.Key, e.Value.Info));

    /// <summary>Retourne true si le moteur nommé est chargé et disponible.</summary>
    public bool IsAvailable(string modelName)
        => _engines.TryGetValue(modelName.ToLowerInvariant(), out var e) && e.Info.IsLoaded;
}
