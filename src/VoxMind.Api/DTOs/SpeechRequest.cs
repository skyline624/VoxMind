using System.Text.Json.Serialization;

namespace VoxMind.Api.DTOs;

/// <summary>
/// Requête OpenAI-compatible pour <c>POST /v1/audio/speech</c>.
/// Corps JSON. La voix de référence par défaut est associée à la langue côté config ;
/// pour utiliser une voix custom, l'appelant uploade un WAV via un autre endpoint
/// (à venir) et passe son identifiant dans <c>voice</c>.
/// </summary>
public sealed record SpeechRequest(
    [property: JsonPropertyName("input")] string Input,
    [property: JsonPropertyName("language")] string? Language = null,
    [property: JsonPropertyName("model")] string? Model = null,
    [property: JsonPropertyName("voice")] string? Voice = null,
    [property: JsonPropertyName("response_format")] string? ResponseFormat = "wav"
);
