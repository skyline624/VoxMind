namespace VoxMind.Core.SpeakerRecognition;

public class SpeakerProfile
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public List<string> Aliases { get; set; } = new();
    public List<SpeakerEmbedding> Embeddings { get; set; } = new();
    public DateTime CreatedAt { get; set; }
    public DateTime? LastSeenAt { get; set; }
    public int DetectionCount { get; set; }
    public bool IsActive { get; set; } = true;
    public string? Notes { get; set; }
}

public class SpeakerEmbedding
{
    public Guid Id { get; set; }
    public Guid ProfileId { get; set; }
    public string FilePath { get; set; } = string.Empty;
    public DateTime CapturedAt { get; set; }
    public float InitialConfidence { get; set; }
    public int AudioDurationSeconds { get; set; }

    /// <summary>Vecteur d'embedding en cache mémoire (chargé depuis FilePath)</summary>
    public float[]? EmbeddingVector { get; set; }
}
