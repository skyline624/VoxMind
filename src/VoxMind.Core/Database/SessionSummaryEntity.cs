namespace VoxMind.Core.Database;

public class SessionSummaryEntity
{
    public Guid Id { get; set; }
    public Guid SessionId { get; set; }
    public DateTime GeneratedAt { get; set; }
    public string? FullTranscript { get; set; }
    public string? KeyMomentsJson { get; set; }     // JSON array
    public string? DecisionsJson { get; set; }       // JSON array
    public string? ActionItemsJson { get; set; }     // JSON array
    public string? GeneratedSummary { get; set; }

    // Navigation
    public ListeningSessionEntity Session { get; set; } = null!;
}
