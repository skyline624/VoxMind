using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace VoxMind.Api.DTOs;

public record CreateSpeakerRequest(
    [property: Required, JsonPropertyName("name")] string Name
);

public record RenameSpeakerRequest(
    [property: Required, JsonPropertyName("name")] string Name
);

public record SpeakerResponse(
    [property: JsonPropertyName("id")]             Guid Id,
    [property: JsonPropertyName("name")]           string Name,
    [property: JsonPropertyName("created_at")]     DateTime CreatedAt,
    [property: JsonPropertyName("embedding_count")] int EmbeddingCount,
    [property: JsonPropertyName("last_seen_at")]   DateTime? LastSeenAt,
    [property: JsonPropertyName("detection_count")] int DetectionCount
);

public record ReportUnknownRequest(
    [property: Required, JsonPropertyName("unknown_speaker_id")] Guid UnknownSpeakerId,
    [property: JsonPropertyName("correct_speaker_id")]           Guid? CorrectSpeakerId,
    [property: JsonPropertyName("correct_name")]                 string? CorrectName
);
