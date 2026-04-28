using VoxMind.Core.Transcription;

namespace VoxMind.Core.Tts;

/// <summary>
/// Registre multi-engine TTS — calque exact de <see cref="TranscriptionEngineRegistry"/>.
/// Permet d'ajouter Zipvoice, Piper, ElevenLabs, etc. à côté de F5-TTS sans toucher à l'API.
/// </summary>
public sealed class TtsEngineRegistry
{
    private readonly IReadOnlyDictionary<string, ITtsService> _engines;
    private readonly string _defaultEngine;

    public TtsEngineRegistry(
        IReadOnlyDictionary<string, ITtsService> engines,
        string defaultEngine)
    {
        if (engines.Count == 0)
            throw new ArgumentException("La registry TTS doit contenir au moins un moteur.", nameof(engines));

        _engines = engines;
        _defaultEngine = defaultEngine.ToLowerInvariant();

        if (!_engines.ContainsKey(_defaultEngine))
            throw new ArgumentException(
                $"Le moteur TTS par défaut '{_defaultEngine}' n'est pas enregistré (présents : {string.Join(", ", _engines.Keys)}).",
                nameof(defaultEngine));
    }

    /// <summary>
    /// Retourne le moteur correspondant au nom demandé. Inconnu → moteur par défaut.
    /// </summary>
    public ITtsService Get(string? engineName = null)
    {
        var key = (engineName ?? _defaultEngine).ToLowerInvariant();
        return _engines.TryGetValue(key, out var engine)
            ? engine
            : _engines[_defaultEngine];
    }

    public IReadOnlyDictionary<string, TtsModelInfo> ListAll()
        => _engines.ToDictionary(kv => kv.Key, kv => kv.Value.Info);
}
