namespace VoxMind.Core.Session;

public class SessionSummary
{
    public Guid SessionId { get; set; }
    public string? Name { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime EndedAt { get; set; }
    public TimeSpan Duration { get; set; }

    public List<ParticipantSummary> Participants { get; set; } = new();
    public List<string> KeyMoments { get; set; } = new();
    public List<string> Decisions { get; set; } = new();
    public List<string> ActionItems { get; set; } = new();

    public string FullTranscript { get; set; } = string.Empty;
    public string GeneratedSummary { get; set; } = string.Empty;

    public DateTime GeneratedAt { get; set; } = DateTime.UtcNow;
}

public class ParticipantSummary
{
    public Guid SpeakerId { get; set; }
    public string Name { get; set; } = string.Empty;
    public TimeSpan SpeakingTime { get; set; }
    public int UtteranceCount { get; set; }
    public float AverageConfidence { get; set; }
    public float PercentageOfSession { get; set; }
}
