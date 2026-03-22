using VoxMind.Core.Audio;

namespace VoxMind.Core.Session;

public class ListeningSession
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string? Name { get; init; }
    public DateTime StartedAt { get; init; } = DateTime.UtcNow;
    public DateTime? EndedAt { get; set; }
    public TimeSpan Duration => (EndedAt ?? DateTime.UtcNow) - StartedAt;
    public SessionStatus Status { get; set; } = SessionStatus.Listening;
    public AudioConfiguration Config { get; set; } = new();
    public List<Guid> ParticipantIds { get; set; } = new();
    public int SegmentCount { get; set; }
    public SessionSummary? Summary { get; set; }
}

public class SessionSegment
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SessionId { get; set; }
    public Guid? SpeakerId { get; set; }
    public string? SpeakerName { get; set; }
    public double StartSeconds { get; set; }
    public double EndSeconds { get; set; }
    public string Text { get; set; } = string.Empty;
    public float Confidence { get; set; }
}

public class DiarizedTranscription
{
    public TimeSpan Start { get; set; }
    public TimeSpan End { get; set; }
    public Guid? SpeakerId { get; set; }
    public string? SpeakerName { get; set; }
    public string Text { get; set; } = string.Empty;
    public float Confidence { get; set; }
}
