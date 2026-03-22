namespace VoxMind.Core.Database;

public class SpeakerProfileEntity
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? AliasesJson { get; set; }            // JSON array
    public DateTime CreatedAt { get; set; }
    public DateTime? LastSeenAt { get; set; }
    public int DetectionCount { get; set; } = 0;
    public bool IsActive { get; set; } = true;
    public string? Notes { get; set; }

    // Navigation
    public List<SpeakerEmbeddingEntity> Embeddings { get; set; } = new();
}
