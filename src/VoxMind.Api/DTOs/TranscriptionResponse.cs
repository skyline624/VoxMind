using System.Text.Json.Serialization;
using VoxMind.Core.SpeakerRecognition;
using VoxMind.Core.Transcription;

namespace VoxMind.Api.DTOs;

/// <summary>Réponse OpenAI-compatible pour POST /v1/audio/transcriptions.</summary>
public record TranscriptionResponse(
    [property: JsonPropertyName("text")]     string Text,
    [property: JsonPropertyName("duration")] double Duration,
    [property: JsonPropertyName("language")] string Language,
    [property: JsonPropertyName("speakers")] IReadOnlyList<SpeakerResult> Speakers,
    [property: JsonPropertyName("segments")] IReadOnlyList<SegmentResult> Segments
);

public record SpeakerResult(
    [property: JsonPropertyName("speaker_id")]  string SpeakerId,
    [property: JsonPropertyName("name")]        string Name,
    [property: JsonPropertyName("confidence")]  float Confidence,
    [property: JsonPropertyName("segments")]    IReadOnlyList<SegmentTimespan> Segments
);

public record SegmentResult(
    [property: JsonPropertyName("text")]       string Text,
    [property: JsonPropertyName("start")]      double Start,
    [property: JsonPropertyName("end")]        double End,
    [property: JsonPropertyName("confidence")] float Confidence
);

public record SegmentTimespan(
    [property: JsonPropertyName("start")] double Start,
    [property: JsonPropertyName("end")]   double End
);

public static class TranscriptionMapper
{
    public static TranscriptionResponse Map(
        TranscriptionResult result,
        SpeakerIdentificationResult? speaker = null)
    {
        var segments = result.Segments.Select(s => new SegmentResult(
            s.Text,
            s.Start.TotalSeconds,
            s.End.TotalSeconds,
            s.Confidence
        )).ToList();

        var speakers = new List<SpeakerResult>();
        if (speaker is { IsIdentified: true })
        {
            speakers.Add(new SpeakerResult(
                speaker.ProfileId?.ToString() ?? "unknown",
                speaker.SpeakerName ?? "unknown",
                speaker.Confidence,
                segments.Select(s => new SegmentTimespan(s.Start, s.End)).ToList()
            ));
        }

        return new TranscriptionResponse(
            result.Text,
            result.Duration.TotalSeconds,
            result.Language,
            speakers,
            segments
        );
    }
}
