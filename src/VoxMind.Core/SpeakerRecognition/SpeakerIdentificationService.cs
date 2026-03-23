using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using VoxMind.Core.Database;

namespace VoxMind.Core.SpeakerRecognition;

public class SpeakerIdentificationService : ISpeakerIdentificationService
{
    private readonly VoxMindDbContext _db;
    private readonly IPyAnnoteClient _pyAnnoteClient;
    private readonly ILogger<SpeakerIdentificationService> _logger;
    private readonly float _confidenceThreshold;
    private readonly string _embeddingsDir;

    // Cache mémoire : profileId → liste de vecteurs d'embedding
    private readonly Dictionary<Guid, List<float[]>> _embeddingCache = new();
    private readonly object _cacheLock = new();
    private bool _disposed;

    public SpeakerIdentificationService(
        VoxMindDbContext db,
        IPyAnnoteClient pyAnnoteClient,
        ILogger<SpeakerIdentificationService> logger,
        float confidenceThreshold = 0.7f,
        string embeddingsDir = "/home/pc/voice_data/embeddings")
    {
        _db = db;
        _pyAnnoteClient = pyAnnoteClient;
        _logger = logger;
        _confidenceThreshold = confidenceThreshold;
        _embeddingsDir = embeddingsDir;
        Directory.CreateDirectory(embeddingsDir);
    }

    /// <summary>Charge tous les embeddings actifs en mémoire au démarrage (chargement parallèle, max 4 lectures simultanées).</summary>
    public async Task InitializeAsync()
    {
        var embeddings = await _db.SpeakerEmbeddings
            .Include(e => e.Profile)
            .Where(e => e.Profile.IsActive)
            .ToListAsync();

        var semaphore = new SemaphoreSlim(4);
        var tasks = embeddings.Select(async emb =>
        {
            await semaphore.WaitAsync();
            try
            {
                if (!File.Exists(emb.FilePath)) return;
                var bytes = await File.ReadAllBytesAsync(emb.FilePath);
                var vector = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(bytes).ToArray();
                lock (_cacheLock)
                {
                    if (!_embeddingCache.TryGetValue(emb.ProfileId, out var list))
                    {
                        list = new List<float[]>();
                        _embeddingCache[emb.ProfileId] = list;
                    }
                    list.Add(vector);
                }
            }
            finally
            {
                semaphore.Release();
            }
        });

        await Task.WhenAll(tasks);
        _logger.LogInformation("Cache d'embeddings initialisé : {Count} profils.", _embeddingCache.Count);
    }

    public async Task<SpeakerIdentificationResult> IdentifyAsync(float[] embedding)
    {
        List<KeyValuePair<Guid, List<float[]>>> snapshot;
        lock (_cacheLock)
        {
            if (_embeddingCache.Count == 0)
                return SpeakerIdentificationResult.Unknown(_confidenceThreshold);
            snapshot = _embeddingCache.ToList();
        }

        float bestSimilarity = 0f;
        Guid? bestProfileId = null;

        foreach (var (profileId, vectors) in snapshot)
        {
            foreach (var storedVector in vectors)
            {
                var similarity = CosineSimilarity(embedding, storedVector);
                if (similarity > bestSimilarity)
                {
                    bestSimilarity = similarity;
                    bestProfileId = profileId;
                }
            }
        }

        if (bestSimilarity >= _confidenceThreshold && bestProfileId.HasValue)
        {
            var profile = await _db.SpeakerProfiles.FindAsync(bestProfileId.Value);
            return new SpeakerIdentificationResult(true, bestProfileId, profile?.Name, bestSimilarity, _confidenceThreshold);
        }

        return new SpeakerIdentificationResult(false, null, null, bestSimilarity, _confidenceThreshold);
    }

    public async Task<SpeakerProfile> EnrollSpeakerAsync(string name, float[] embedding, float initialConfidence, int audioDurationSeconds = 0)
    {
        var profileId = Guid.NewGuid();
        var embeddingId = Guid.NewGuid();
        var filePath = Path.Combine(_embeddingsDir, $"{profileId}_emb_{embeddingId}.bin");

        // Sauvegarder l'embedding sur disque
        var bytes = System.Runtime.InteropServices.MemoryMarshal.AsBytes(embedding.AsSpan()).ToArray();
        await File.WriteAllBytesAsync(filePath, bytes);

        // Insérer en base
        var profileEntity = new SpeakerProfileEntity
        {
            Id = profileId,
            Name = name,
            CreatedAt = DateTime.UtcNow,
            IsActive = true
        };
        var embeddingEntity = new SpeakerEmbeddingEntity
        {
            Id = embeddingId,
            ProfileId = profileId,
            FilePath = filePath,
            CapturedAt = DateTime.UtcNow,
            InitialConfidence = initialConfidence,
            AudioDurationSeconds = audioDurationSeconds
        };
        _db.SpeakerProfiles.Add(profileEntity);
        _db.SpeakerEmbeddings.Add(embeddingEntity);
        await _db.SaveChangesAsync();

        // Mettre à jour le cache
        _embeddingCache[profileId] = new List<float[]> { embedding };
        _logger.LogInformation("Locuteur '{Name}' (ID={Id}) enregistré.", name, profileId);

        return new SpeakerProfile
        {
            Id = profileId,
            Name = name,
            CreatedAt = DateTime.UtcNow,
            IsActive = true,
            Embeddings = new List<SpeakerEmbedding>
            {
                new() { Id = embeddingId, ProfileId = profileId, FilePath = filePath, CapturedAt = DateTime.UtcNow, InitialConfidence = initialConfidence }
            }
        };
    }

    public async Task AddEmbeddingToProfileAsync(Guid profileId, float[] embedding, float confidence)
    {
        var embeddingId = Guid.NewGuid();
        var filePath = Path.Combine(_embeddingsDir, $"{profileId}_emb_{embeddingId}.bin");

        var bytes = System.Runtime.InteropServices.MemoryMarshal.AsBytes(embedding.AsSpan()).ToArray();
        await File.WriteAllBytesAsync(filePath, bytes);

        _db.SpeakerEmbeddings.Add(new SpeakerEmbeddingEntity
        {
            Id = embeddingId, ProfileId = profileId, FilePath = filePath,
            CapturedAt = DateTime.UtcNow, InitialConfidence = confidence
        });
        await _db.SaveChangesAsync();

        lock (_cacheLock)
        {
            if (!_embeddingCache.ContainsKey(profileId))
                _embeddingCache[profileId] = new List<float[]>();
            _embeddingCache[profileId].Add(embedding);
        }
    }

    public async Task<SpeakerProfile?> GetProfileAsync(Guid profileId)
    {
        var entity = await _db.SpeakerProfiles.Include(p => p.Embeddings).FirstOrDefaultAsync(p => p.Id == profileId);
        return entity is null ? null : MapToProfile(entity);
    }

    public async Task<IReadOnlyList<SpeakerProfile>> GetAllProfilesAsync()
    {
        var entities = await _db.SpeakerProfiles.Include(p => p.Embeddings).Where(p => p.IsActive).ToListAsync();
        return entities.Select(MapToProfile).ToList();
    }

    public async Task MergeProfilesAsync(Guid targetProfileId, Guid sourceProfileId)
    {
        var sourceEmbeddings = await _db.SpeakerEmbeddings
            .Where(e => e.ProfileId == sourceProfileId).ToListAsync();

        foreach (var emb in sourceEmbeddings)
            emb.ProfileId = targetProfileId;

        var source = await _db.SpeakerProfiles.FindAsync(sourceProfileId);
        if (source is not null)
            _db.SpeakerProfiles.Remove(source);

        await _db.SaveChangesAsync();

        // Mettre à jour le cache
        if (_embeddingCache.TryGetValue(sourceProfileId, out var sourceVectors))
        {
            if (!_embeddingCache.ContainsKey(targetProfileId))
                _embeddingCache[targetProfileId] = new List<float[]>();
            _embeddingCache[targetProfileId].AddRange(sourceVectors);
            _embeddingCache.Remove(sourceProfileId);
        }
    }

    public async Task RenameProfileAsync(Guid profileId, string newName)
    {
        var profile = await _db.SpeakerProfiles.FindAsync(profileId)
            ?? throw new KeyNotFoundException($"Profil {profileId} introuvable.");
        profile.Name = newName;
        await _db.SaveChangesAsync();
    }

    public async Task DeleteProfileAsync(Guid profileId)
    {
        var profile = await _db.SpeakerProfiles.Include(p => p.Embeddings).FirstOrDefaultAsync(p => p.Id == profileId);
        if (profile is null) return;

        // Supprimer les fichiers d'embedding
        foreach (var emb in profile.Embeddings)
            if (File.Exists(emb.FilePath)) File.Delete(emb.FilePath);

        _db.SpeakerProfiles.Remove(profile);
        await _db.SaveChangesAsync();
        _embeddingCache.Remove(profileId);
    }

    public async Task LinkSpeakersAsync(Guid knownProfileId, Guid unknownProfileId)
    {
        var known = await _db.SpeakerProfiles.FindAsync(knownProfileId)
            ?? throw new KeyNotFoundException($"Profil connu {knownProfileId} introuvable.");
        var unknown = await _db.SpeakerProfiles.Include(p => p.Embeddings).FirstOrDefaultAsync(p => p.Id == unknownProfileId)
            ?? throw new KeyNotFoundException($"Profil inconnu {unknownProfileId} introuvable.");

        // Ajouter le nom de l'inconnu comme alias du profil connu
        var aliases = string.IsNullOrEmpty(known.AliasesJson)
            ? new List<string>()
            : JsonSerializer.Deserialize<List<string>>(known.AliasesJson) ?? new();
        if (!aliases.Contains(unknown.Name))
        {
            aliases.Add(unknown.Name);
            known.AliasesJson = JsonSerializer.Serialize(aliases);
        }

        // Transférer les embeddings
        var unknownEmbeddings = await _db.SpeakerEmbeddings.Where(e => e.ProfileId == unknownProfileId).ToListAsync();
        foreach (var emb in unknownEmbeddings)
            emb.ProfileId = knownProfileId;

        // Supprimer l'inconnu
        _db.SpeakerProfiles.Remove(unknown);
        await _db.SaveChangesAsync();

        // Mettre à jour le cache
        if (_embeddingCache.TryGetValue(unknownProfileId, out var vecs))
        {
            if (!_embeddingCache.ContainsKey(knownProfileId)) _embeddingCache[knownProfileId] = new();
            _embeddingCache[knownProfileId].AddRange(vecs);
            _embeddingCache.Remove(unknownProfileId);
        }
    }

    public async Task UpdateLastSeenAsync(Guid profileId)
    {
        var profile = await _db.SpeakerProfiles.FindAsync(profileId);
        if (profile is null) return;
        profile.LastSeenAt = DateTime.UtcNow;
        profile.DetectionCount++;
        await _db.SaveChangesAsync();
    }

    public async Task<bool> CheckHealthAsync() => await _pyAnnoteClient.PingAsync();

    private static SpeakerProfile MapToProfile(SpeakerProfileEntity e) => new()
    {
        Id = e.Id,
        Name = e.Name,
        Aliases = string.IsNullOrEmpty(e.AliasesJson)
            ? new()
            : JsonSerializer.Deserialize<List<string>>(e.AliasesJson) ?? new(),
        CreatedAt = e.CreatedAt,
        LastSeenAt = e.LastSeenAt,
        DetectionCount = e.DetectionCount,
        IsActive = e.IsActive,
        Notes = e.Notes,
        Embeddings = e.Embeddings.Select(em => new SpeakerEmbedding
        {
            Id = em.Id,
            ProfileId = em.ProfileId,
            FilePath = em.FilePath,
            CapturedAt = em.CapturedAt,
            InitialConfidence = em.InitialConfidence,
            AudioDurationSeconds = em.AudioDurationSeconds
        }).ToList()
    };

    private static float CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length != b.Length) return 0f;
        float dot = 0f, normA = 0f, normB = 0f;
        for (int i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }
        return (normA == 0 || normB == 0) ? 0f : dot / (MathF.Sqrt(normA) * MathF.Sqrt(normB));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _pyAnnoteClient.Dispose();
    }
}
