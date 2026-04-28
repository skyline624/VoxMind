namespace VoxMind.Core.Tts;

/// <summary>
/// Service de synthèse vocale (text-to-speech).
///
/// La langue est un paramètre de première classe car chaque moteur F5-TTS est
/// un fine-tune par langue (FR utilise un checkpoint différent de EN). Le router
/// charge la session ONNX correspondante à la demande, avec cache LRU.
/// </summary>
public interface ITtsService : IDisposable
{
    /// <summary>Synthétise un texte en audio PCM 24 kHz mono.</summary>
    /// <param name="text">Texte à dire.</param>
    /// <param name="language">
    /// Code ISO 639-1 (<c>"fr"</c>, <c>"en"</c>, …). Doit être présent dans
    /// <see cref="TtsModelInfo.AvailableLanguages"/>. Si null, le service utilise
    /// la langue par défaut configurée.
    /// </param>
    /// <param name="referenceWav">
    /// Audio de référence pour le voice cloning zero-shot, PCM 24 kHz mono.
    /// Si null, le service utilise l'échantillon par défaut configuré pour la langue.
    /// </param>
    /// <param name="referenceText">
    /// Transcription de <paramref name="referenceWav"/>. Requis si referenceWav est fourni
    /// (F5-TTS conditionne le flow-matching sur paire audio+texte). Sinon utilise le texte
    /// par défaut associé à la voix de référence configurée.
    /// </param>
    Task<TtsResult> SynthesizeAsync(
        string text,
        string? language = null,
        byte[]? referenceWav = null,
        string? referenceText = null,
        CancellationToken ct = default);

    /// <summary>Métadonnées de chargement du moteur.</summary>
    TtsModelInfo Info { get; }
}
