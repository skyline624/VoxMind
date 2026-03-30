using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SherpaOnnx;
using VoxMind.Core.Configuration;
using VoxMind.Core.Database;
using Vad = VoxMind.Core.Vad;

namespace VoxMind.Core.SpeakerRecognition;

/// <summary>
/// Speaker identification service using sherpa-onnx for embedding extraction (100% local C#, no Python).
/// Drops the dependency on PyAnnote gRPC server.
///
/// Required model: EmbeddingModelPath from SpeakerRecognitionConfig.SherpaOnnx
/// (e.g. 3dspeaker_speech_eres2net_base_sv_zh-cn_3dspeaker_16k.onnx)
/// </summary>
public class SherpaOnnxSpeakerService : ISpeakerIdentificationService
{
    private readonly VoxMindDbContext _db;
    private readonly ILogger<SherpaOnnxSpeakerService> _logger;
    private readonly float _confidenceThreshold;
    private readonly float _clusteringThreshold;
    private readonly string _embeddingsDir;
    private readonly SpeakerEmbeddingExtractor? _extractor;

    private readonly Dictionary<Guid, List<float[]>> _embeddingCache = new();
    private readonly object _cacheLock = new();
    private bool _disposed;

    public SherpaOnnxSpeakerService(
        SpeakerRecognitionConfig config,
        VoxMindDbContext db,
        ILogger<SherpaOnnxSpeakerService> logger,
        string embeddingsDir = "/home/pc/voice_data/embeddings")
    {
        _db = db;
        _logger = logger;
        _confidenceThreshold = config.ConfidenceThreshold;
        _clusteringThreshold = config.SherpaOnnx.ClusteringThreshold;
        _embeddingsDir = embeddingsDir;
        Directory.CreateDirectory(embeddingsDir);

        var modelPath = config.SherpaOnnx.EmbeddingModelPath;
        if (File.Exists(modelPath))
        {
            try
            {
                var cfg = new SpeakerEmbeddingExtractorConfig
                {
                    Model = modelPath,
                    NumThreads = config.SherpaOnnx.NumThreads,
                    Debug = 0,
                    Provider = "cpu"
                };
                _extractor = new SpeakerEmbeddingExtractor(cfg);
                _logger.LogInformation("SherpaOnnx embedding extractor chargé depuis {Path}.", modelPath);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Impossible de charger SherpaOnnx embedding extractor depuis {Path}.", modelPath);
            }
        }
        else
        {
            _logger.LogWarning("Modèle d'embedding SherpaOnnx introuvable : {Path}. Speaker recognition désactivé.", modelPath);
        }
    }

    /// <summary>Charge tous les embeddings actifs en mémoire (parallèle, max 4 lectures simultanées).</summary>
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
            finally { semaphore.Release(); }
        });

        await Task.WhenAll(tasks);
        _logger.LogInformation("Cache embeddings initialisé : {Count} profils.", _embeddingCache.Count);
    }

    public Task<SpeakerIdentificationResult> IdentifyFromAudioAsync(byte[] audioData, CancellationToken ct = default)
    {
        if (_extractor is null)
            return Task.FromResult(SpeakerIdentificationResult.Unknown(_confidenceThreshold));

        try
        {
            float[] samples = ConvertWavToFloat(audioData);
            if (samples.Length == 0)
                return Task.FromResult(SpeakerIdentificationResult.Unknown(_confidenceThreshold));

            var stream = _extractor.CreateStream();
            stream.AcceptWaveform(16000, samples);
            stream.InputFinished();
            float[] embedding = _extractor.Compute(stream);

            return IdentifyAsync(embedding);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Erreur lors de l'extraction d'embedding sherpa-onnx.");
            return Task.FromResult(SpeakerIdentificationResult.Unknown(_confidenceThreshold));
        }
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
            // Comparer contre le meilleur embedding individuel ET le centroïde moyen
            // Cela permet de reconnaître un locuteur même avec des conditions d'enregistrement différentes
            float bestForProfile = 0f;

            // Comparaison individuelle
            foreach (var stored in vectors)
            {
                float sim = CosineSimilarity(embedding, stored);
                if (sim > bestForProfile) bestForProfile = sim;
            }

            // Comparaison contre le centroïde (moyenne des embeddings du profil)
            if (vectors.Count > 1)
            {
                var centroid = new float[embedding.Length];
                for (int i = 0; i < centroid.Length; i++)
                {
                    float sum = 0f;
                    foreach (var v in vectors) sum += v[i];
                    centroid[i] = sum / vectors.Count;
                }
                float centroidSim = CosineSimilarity(embedding, centroid);
                if (centroidSim > bestForProfile) bestForProfile = centroidSim;
            }

            if (bestForProfile > bestSimilarity)
            {
                bestSimilarity = bestForProfile;
                bestProfileId = profileId;
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

        var bytes = System.Runtime.InteropServices.MemoryMarshal.AsBytes(embedding.AsSpan()).ToArray();
        await File.WriteAllBytesAsync(filePath, bytes);

        _db.SpeakerProfiles.Add(new SpeakerProfileEntity { Id = profileId, Name = name, CreatedAt = DateTime.UtcNow, IsActive = true });
        _db.SpeakerEmbeddings.Add(new SpeakerEmbeddingEntity { Id = embeddingId, ProfileId = profileId, FilePath = filePath, CapturedAt = DateTime.UtcNow, InitialConfidence = initialConfidence, AudioDurationSeconds = audioDurationSeconds });
        await _db.SaveChangesAsync();

        lock (_cacheLock) { _embeddingCache[profileId] = new List<float[]> { embedding }; }
        _logger.LogInformation("Locuteur '{Name}' (ID={Id}) enregistré.", name, profileId);

        return new SpeakerProfile { Id = profileId, Name = name, CreatedAt = DateTime.UtcNow, IsActive = true };
    }

    public async Task AddEmbeddingToProfileAsync(Guid profileId, float[] embedding, float confidence)
    {
        var embeddingId = Guid.NewGuid();
        var filePath = Path.Combine(_embeddingsDir, $"{profileId}_emb_{embeddingId}.bin");

        var bytes = System.Runtime.InteropServices.MemoryMarshal.AsBytes(embedding.AsSpan()).ToArray();
        await File.WriteAllBytesAsync(filePath, bytes);

        _db.SpeakerEmbeddings.Add(new SpeakerEmbeddingEntity { Id = embeddingId, ProfileId = profileId, FilePath = filePath, CapturedAt = DateTime.UtcNow, InitialConfidence = confidence });
        await _db.SaveChangesAsync();

        lock (_cacheLock)
        {
            if (!_embeddingCache.ContainsKey(profileId)) _embeddingCache[profileId] = new List<float[]>();
            _embeddingCache[profileId].Add(embedding);
        }
    }

    public async Task<SpeakerProfile?> GetProfileAsync(Guid profileId)
    {
        var e = await _db.SpeakerProfiles.Include(p => p.Embeddings).FirstOrDefaultAsync(p => p.Id == profileId);
        return e is null ? null : MapToProfile(e);
    }

    public async Task<IReadOnlyList<SpeakerProfile>> GetAllProfilesAsync()
    {
        var entities = await _db.SpeakerProfiles.Include(p => p.Embeddings).Where(p => p.IsActive).ToListAsync();
        return entities.Select(MapToProfile).ToList();
    }

    public async Task MergeProfilesAsync(Guid targetProfileId, Guid sourceProfileId)
    {
        var sourceEmbs = await _db.SpeakerEmbeddings.Where(e => e.ProfileId == sourceProfileId).ToListAsync();
        foreach (var emb in sourceEmbs) emb.ProfileId = targetProfileId;

        var source = await _db.SpeakerProfiles.FindAsync(sourceProfileId);
        if (source is not null) _db.SpeakerProfiles.Remove(source);
        await _db.SaveChangesAsync();

        lock (_cacheLock)
        {
            if (_embeddingCache.TryGetValue(sourceProfileId, out var vecs))
            {
                if (!_embeddingCache.ContainsKey(targetProfileId)) _embeddingCache[targetProfileId] = new List<float[]>();
                _embeddingCache[targetProfileId].AddRange(vecs);
                _embeddingCache.Remove(sourceProfileId);
            }
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
        foreach (var emb in profile.Embeddings)
            if (File.Exists(emb.FilePath)) File.Delete(emb.FilePath);
        _db.SpeakerProfiles.Remove(profile);
        await _db.SaveChangesAsync();
        lock (_cacheLock) { _embeddingCache.Remove(profileId); }
    }

    public async Task LinkSpeakersAsync(Guid knownProfileId, Guid unknownProfileId)
    {
        var known = await _db.SpeakerProfiles.FindAsync(knownProfileId)
            ?? throw new KeyNotFoundException($"Profil connu {knownProfileId} introuvable.");
        var unknown = await _db.SpeakerProfiles.Include(p => p.Embeddings).FirstOrDefaultAsync(p => p.Id == unknownProfileId)
            ?? throw new KeyNotFoundException($"Profil inconnu {unknownProfileId} introuvable.");

        var aliases = string.IsNullOrEmpty(known.AliasesJson)
            ? new List<string>()
            : JsonSerializer.Deserialize<List<string>>(known.AliasesJson) ?? new();
        if (!aliases.Contains(unknown.Name)) { aliases.Add(unknown.Name); known.AliasesJson = JsonSerializer.Serialize(aliases); }

        var unknownEmbs = await _db.SpeakerEmbeddings.Where(e => e.ProfileId == unknownProfileId).ToListAsync();
        foreach (var emb in unknownEmbs) emb.ProfileId = knownProfileId;
        _db.SpeakerProfiles.Remove(unknown);
        await _db.SaveChangesAsync();

        lock (_cacheLock)
        {
            if (_embeddingCache.TryGetValue(unknownProfileId, out var vecs))
            {
                if (!_embeddingCache.ContainsKey(knownProfileId)) _embeddingCache[knownProfileId] = new();
                _embeddingCache[knownProfileId].AddRange(vecs);
                _embeddingCache.Remove(unknownProfileId);
            }
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

    public Task<float[]?> ExtractEmbeddingAsync(byte[] audioData, CancellationToken ct = default)
    {
        if (_extractor is null) return Task.FromResult<float[]?>(null);
        try
        {
            float[] samples = ConvertWavToFloat(audioData);
            if (samples.Length == 0) return Task.FromResult<float[]?>(null);
            var stream = _extractor.CreateStream();
            stream.AcceptWaveform(16000, samples);
            stream.InputFinished();
            return Task.FromResult<float[]?>(_extractor.Compute(stream));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Erreur extraction embedding pour enrollment.");
            return Task.FromResult<float[]?>(null);
        }
    }

    public Task<bool> CheckHealthAsync() => Task.FromResult(_extractor is not null);

    /// <inheritdoc/>
    public async Task<IReadOnlyDictionary<int, SpeakerLabel>> DiarizeSegmentsAsync(
        IReadOnlyList<Vad.VadSegment> segments,
        CancellationToken ct = default,
        int? numSpeakers = null)
    {
        var result = new Dictionary<int, SpeakerLabel>();

        if (_extractor is null || segments.Count == 0)
            return result;

        // ── 1. Extraire l'embedding de chaque segment ──────────────────────────
        // Les segments < 0.75 s (12 000 samples @ 16 kHz) sont extraits sans embedding :
        // leur locuteur sera propagé depuis le voisin temporel le plus proche (étape 4).
        const int MinSamples = 12_000;

        var embeddings = new (int Index, float[] Embedding)[segments.Count];
        for (int i = 0; i < segments.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            if (segments[i].Samples.Length < MinSamples)
            {
                embeddings[i] = (i, Array.Empty<float>());
                continue;
            }
            try
            {
                var stream = _extractor.CreateStream();
                stream.AcceptWaveform(16000, segments[i].Samples);
                stream.InputFinished();
                embeddings[i] = (i, _extractor.Compute(stream));
            }
            catch
            {
                embeddings[i] = (i, Array.Empty<float>());
            }
        }

        // ── 2. Clustering greedy par similarité cosinus ─────────────────────────
        var clusters = new List<(List<int> Indices, float[] Centroid)>();

        foreach (var (idx, emb) in embeddings)
        {
            if (emb.Length == 0) continue;

            float bestSim = 0f;
            int bestCluster = -1;
            for (int c = 0; c < clusters.Count; c++)
            {
                float sim = CosineSimilarity(emb, clusters[c].Centroid);
                if (sim > bestSim) { bestSim = sim; bestCluster = c; }
            }

            if (bestSim >= _clusteringThreshold && bestCluster >= 0)
            {
                clusters[bestCluster].Indices.Add(idx);
                var prev = clusters[bestCluster].Centroid;
                int count = clusters[bestCluster].Indices.Count;
                var newCentroid = new float[prev.Length];
                for (int k = 0; k < prev.Length; k++)
                    newCentroid[k] = (prev[k] * (count - 1) + emb[k]) / count;
                clusters[bestCluster] = (clusters[bestCluster].Indices, newCentroid);
            }
            else
            {
                clusters.Add((new List<int> { idx }, emb));
            }
        }

        // ── 3. Fusion agglomérative si numSpeakers est fourni ───────────────────
        // Tant qu'on a plus de clusters que souhaité, on fusionne les deux plus similaires.
        if (numSpeakers.HasValue && clusters.Count > numSpeakers.Value)
        {
            _logger.LogInformation(
                "Diarisation : fusion agglomérative de {From} → {To} clusters.",
                clusters.Count, numSpeakers.Value);

            while (clusters.Count > numSpeakers.Value)
            {
                float bestSim = -1f;
                int bestI = 0, bestJ = 1;

                for (int i = 0; i < clusters.Count - 1; i++)
                    for (int j = i + 1; j < clusters.Count; j++)
                    {
                        float sim = CosineSimilarity(clusters[i].Centroid, clusters[j].Centroid);
                        if (sim > bestSim) { bestSim = sim; bestI = i; bestJ = j; }
                    }

                // Fusionner j dans i (moyenne pondérée par taille)
                int sizeI = clusters[bestI].Indices.Count;
                int sizeJ = clusters[bestJ].Indices.Count;
                int total = sizeI + sizeJ;
                var mergedCentroid = new float[clusters[bestI].Centroid.Length];
                for (int k = 0; k < mergedCentroid.Length; k++)
                    mergedCentroid[k] = (clusters[bestI].Centroid[k] * sizeI + clusters[bestJ].Centroid[k] * sizeJ) / total;

                var mergedIndices = clusters[bestI].Indices;
                mergedIndices.AddRange(clusters[bestJ].Indices);
                clusters[bestI] = (mergedIndices, mergedCentroid);
                clusters.RemoveAt(bestJ);
            }
        }

        // ── 4. Matcher chaque cluster contre les profils connus ou créer un nouveau ──
        int autoCount = await _db.SpeakerProfiles.CountAsync(p => p.IsActive, ct);

        for (int c = 0; c < clusters.Count; c++)
        {
            ct.ThrowIfCancellationRequested();

            var identification = await IdentifyAsync(clusters[c].Centroid);

            SpeakerLabel label;
            if (identification.IsIdentified && identification.ProfileId.HasValue)
            {
                label = new SpeakerLabel(identification.ProfileId, identification.SpeakerName ?? $"Locuteur {c + 1}");
                await UpdateLastSeenAsync(identification.ProfileId.Value);
            }
            else
            {
                autoCount++;
                var name = $"Locuteur {autoCount}";
                var profile = await EnrollSpeakerAsync(name, clusters[c].Centroid, identification.Confidence,
                    audioDurationSeconds: (int)clusters[c].Indices
                        .Sum(i => segments[i].EndSeconds - segments[i].StartSeconds));
                label = new SpeakerLabel(profile.Id, name);
                _logger.LogInformation("Diarisation : nouveau profil auto '{Name}' créé ({Count} segments).",
                    name, clusters[c].Indices.Count);
            }

            foreach (var idx in clusters[c].Indices)
                result[idx] = label;
        }

        // ── 5. Propager le locuteur aux segments courts (sans embedding) ─────────
        // Chaque segment court hérite du locuteur du voisin temporellement le plus proche.
        for (int i = 0; i < segments.Count; i++)
        {
            if (result.ContainsKey(i)) continue;

            float midpoint = (segments[i].StartSeconds + segments[i].EndSeconds) / 2f;
            int bestNeighbor = -1;
            float bestDist = float.MaxValue;

            foreach (var (assignedIdx, _) in result)
            {
                float neighborMid = (segments[assignedIdx].StartSeconds + segments[assignedIdx].EndSeconds) / 2f;
                float dist = MathF.Abs(neighborMid - midpoint);
                if (dist < bestDist) { bestDist = dist; bestNeighbor = assignedIdx; }
            }

            if (bestNeighbor >= 0)
                result[i] = result[bestNeighbor];
        }

        _logger.LogInformation("Diarisation : {Count} locuteur(s) détecté(s).",
            result.Values.DistinctBy(l => l.ProfileId).Count());

        return result;
    }

    private static SpeakerProfile MapToProfile(SpeakerProfileEntity e) => new()
    {
        Id = e.Id,
        Name = e.Name,
        Aliases = string.IsNullOrEmpty(e.AliasesJson) ? new() : JsonSerializer.Deserialize<List<string>>(e.AliasesJson) ?? new(),
        CreatedAt = e.CreatedAt,
        LastSeenAt = e.LastSeenAt,
        DetectionCount = e.DetectionCount,
        IsActive = e.IsActive,
        Notes = e.Notes,
        Embeddings = e.Embeddings.Select(em => new SpeakerEmbedding { Id = em.Id, ProfileId = em.ProfileId, FilePath = em.FilePath, CapturedAt = em.CapturedAt, InitialConfidence = em.InitialConfidence, AudioDurationSeconds = em.AudioDurationSeconds }).ToList()
    };

    private static float CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length != b.Length) return 0f;
        float dot = 0f, normA = 0f, normB = 0f;
        for (int i = 0; i < a.Length; i++) { dot += a[i] * b[i]; normA += a[i] * a[i]; normB += b[i] * b[i]; }
        return (normA == 0 || normB == 0) ? 0f : dot / (MathF.Sqrt(normA) * MathF.Sqrt(normB));
    }

    private static float[] ConvertWavToFloat(byte[] wavData)
    {
        if (wavData.Length < 8) return Array.Empty<float>();
        int dataOffset = 44;
        for (int i = 0; i < Math.Min(wavData.Length - 8, 512); i++)
        {
            if (wavData[i] == 'd' && wavData[i + 1] == 'a' && wavData[i + 2] == 't' && wavData[i + 3] == 'a')
            { dataOffset = i + 8; break; }
        }
        int n = (wavData.Length - dataOffset) / 2;
        if (n <= 0) return Array.Empty<float>();
        var samples = new float[n];
        for (int i = 0; i < n; i++) samples[i] = BitConverter.ToInt16(wavData, dataOffset + i * 2) / 32768.0f;
        return samples;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _extractor?.Dispose();
    }
}
