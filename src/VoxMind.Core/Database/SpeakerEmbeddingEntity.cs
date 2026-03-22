namespace VoxMind.Core.Database;

public class SpeakerEmbeddingEntity
{
    public Guid Id { get; set; }
    public Guid ProfileId { get; set; }
    public string FilePath { get; set; } = string.Empty;   // Chemin vers le .bin
    public DateTime CapturedAt { get; set; }
    public float InitialConfidence { get; set; }
    public int AudioDurationSeconds { get; set; }

    // Navigation
    public SpeakerProfileEntity Profile { get; set; } = null!;
}
