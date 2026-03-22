namespace VoxMind.Core.SpeakerRecognition;

public class EmbeddingResult
{
    public bool Success { get; init; }
    public float[] Embedding { get; init; } = Array.Empty<float>();
    public float DurationUsed { get; init; }
    public string? Error { get; init; }
}

public class ComparisonResult
{
    public float CosineSimilarity { get; init; }
    public float EuclideanDistance { get; init; }
    public bool IsSameSpeaker { get; init; }
}

public interface IPyAnnoteClient : IDisposable
{
    /// <summary>Extrait un embedding vocal depuis des données WAV PCM 16kHz mono</summary>
    Task<EmbeddingResult> ExtractEmbeddingAsync(byte[] audioData, CancellationToken ct = default);

    /// <summary>Compare deux embeddings et retourne la similarité cosinus</summary>
    Task<ComparisonResult> CompareEmbeddingsAsync(float[] embedding1, float[] embedding2, CancellationToken ct = default);

    /// <summary>Vérifie que le serveur Python est actif</summary>
    Task<bool> PingAsync(CancellationToken ct = default);
}
