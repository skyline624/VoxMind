using System.Text.Json.Serialization;
using VoxMind.Core.Vad;

namespace VoxMind.Core.Transcription;

public class TranscriptionResult
{
    public string Text { get; set; } = string.Empty;
    public string Language { get; set; } = string.Empty;
    public float Confidence { get; set; }
    public TimeSpan Duration { get; set; }
    public List<TranscriptionSegment> Segments { get; set; } = new();
    public DateTime ProcessedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Segments VAD bruts (samples PCM) — non sérialisés, utilisés pour la diarisation.</summary>
    [JsonIgnore]
    public IReadOnlyList<VadSegment>? VadSegments { get; set; }
}

public class TranscriptionSegment
{
    public int Id { get; set; }
    public TimeSpan Start { get; set; }
    public TimeSpan End { get; set; }
    public string Text { get; set; } = string.Empty;
    public float Confidence { get; set; }
    public List<WordTimestamp> Words { get; set; } = new();
    public Guid? SpeakerId { get; set; }
    public string? SpeakerName { get; set; }
}

public class WordTimestamp
{
    public string Word { get; set; } = string.Empty;
    public TimeSpan Start { get; set; }
    public TimeSpan End { get; set; }
}

public class ModelInfo
{
    public string ModelName { get; init; } = string.Empty;
    public ModelSize Size { get; init; }
    public ComputeBackend Backend { get; init; }
    public bool IsLoaded { get; set; }
    public long MemoryUsageBytes { get; set; }
}
