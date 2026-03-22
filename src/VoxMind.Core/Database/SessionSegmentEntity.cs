namespace VoxMind.Core.Database;

public class SessionSegmentEntity
{
    public Guid Id { get; set; }
    public Guid SessionId { get; set; }
    public Guid? SpeakerId { get; set; }        // NULL si inconnu
    public double StartTime { get; set; }        // secondes
    public double EndTime { get; set; }
    public string Transcript { get; set; } = string.Empty;
    public float Confidence { get; set; }

    // Navigation
    public ListeningSessionEntity Session { get; set; } = null!;
}
