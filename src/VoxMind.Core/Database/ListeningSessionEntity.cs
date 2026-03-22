namespace VoxMind.Core.Database;

public class ListeningSessionEntity
{
    public Guid Id { get; set; }
    public string? Name { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime? EndedAt { get; set; }
    public string? ParticipantsJson { get; set; }   // JSON array de speaker IDs
    public string? ConfigJson { get; set; }          // JSON de la configuration
    public string Status { get; set; } = "active";  // active, paused, completed

    // Navigation
    public List<SessionSegmentEntity> Segments { get; set; } = new();
    public SessionSummaryEntity? Summary { get; set; }
}
