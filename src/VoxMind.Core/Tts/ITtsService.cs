using System.Runtime.CompilerServices;

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
    /// <param name="instructions">
    /// Instructions de style/émotion en langage naturel (ex. « d'un ton enjoué »). Exploité par les moteurs
    /// expressifs (Qwen3-TTS via vLLM) ; ignoré par les moteurs qui ne le supportent pas (Kokoro, F5, …).
    /// </param>
    Task<TtsResult> SynthesizeAsync(
        string text,
        string? language = null,
        byte[]? referenceWav = null,
        string? referenceText = null,
        string? instructions = null,
        CancellationToken ct = default);

    /// <summary>
    /// Variante <b>streaming</b> de <see cref="SynthesizeAsync"/> : émet l'audio par segments (phrases) au
    /// fur et à mesure de la synthèse, pour que l'appelant puisse commencer la lecture avant la fin de la
    /// réponse (latence du premier son ≈ synthèse de la 1ʳᵉ phrase, au lieu de toute la réponse). L'endpoint
    /// HTTP pousse alors chaque segment sur le fil en <c>Transfer-Encoding: chunked</c>.
    ///
    /// Chaque <see cref="TtsResult"/> porte le PCM d'un segment (mono float32 [-1, 1]). L'implémentation par
    /// défaut émet <b>un seul</b> segment = la réponse complète (aucun gain de latence) : suffisant pour les
    /// moteurs autorégressifs (F5/Coqui) qui ne produisent l'audio qu'en fin de passe. Les moteurs
    /// non-autorégressifs comme Kokoro la surchargent pour une vraie synthèse incrémentale (une passe ONNX
    /// par phrase).
    /// </summary>
    async IAsyncEnumerable<TtsResult> SynthesizeStreamAsync(
        string text,
        string? language = null,
        string? instructions = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var result = await SynthesizeAsync(text, language, null, null, instructions, ct).ConfigureAwait(false);
        if (result.Pcm.Length > 0)
        {
            yield return result;
        }
    }

    /// <summary>Métadonnées de chargement du moteur.</summary>
    TtsModelInfo Info { get; }
}
