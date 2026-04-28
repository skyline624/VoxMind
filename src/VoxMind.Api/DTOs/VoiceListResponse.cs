using System.Text.Json.Serialization;

namespace VoxMind.Api.DTOs;

/// <summary>Réponse de <c>GET /v1/voices</c> — liste des moteurs et langues disponibles.</summary>
public sealed record VoiceListResponse(
    [property: JsonPropertyName("data")] IReadOnlyList<VoiceEngineEntry> Data
);

public sealed record VoiceEngineEntry(
    [property: JsonPropertyName("engine")] string Engine,
    [property: JsonPropertyName("loaded")] bool IsLoaded,
    [property: JsonPropertyName("available_languages")] IReadOnlyList<string> AvailableLanguages,
    [property: JsonPropertyName("resident_languages")] IReadOnlyList<string> ResidentLanguages
);
