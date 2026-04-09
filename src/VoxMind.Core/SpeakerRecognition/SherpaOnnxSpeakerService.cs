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
    private readonly IDbContextFactory<VoxMindDbContext> _dbFactory;
    private readonly ILogger<SherpaOnnxSpeakerService> _logger;
    private readonly float _confidenceThreshold;
    private readonly float _clusteringThreshold;
    private readonly string _embeddingsDir;
    private readonly SpeakerEmbeddingExtractor? _extractor;
    private readonly OfflineSpeakerDiarization? _diarizer;
    private readonly object _diarizerLock = new();

    private readonly Dictionary<Guid, List<float[]>> _embeddingCache = new();
    private readonly object _cacheLock = new();
    private bool _disposed;

    public SherpaOnnxSpeakerService(
        SpeakerRecognitionConfig config,
        IDbContextFactory<VoxMindDbContext> dbFactory,
        ILogger<SherpaOnnxSpeakerService> logger,
        string embeddingsDir = "/home/pc/voice_data/embeddings")
    {
        _dbFactory = dbFactory;
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

        // OfflineSpeakerDiarization (pyannote-3.0 + 3D-Speaker) — qualité PyAnnote en C# pur.
        // Requiert les DEUX modèles présents : segmentation + embedding.
        var segPath = config.SherpaOnnx.SegmentationModelPath;
        if (_extractor is not null && File.Exists(segPath))
        {
            try
            {
                var diarConfig = new OfflineSpeakerDiarizationConfig();
                diarConfig.Segmentation.Pyannote.Model = segPath;
                diarConfig.Segmentation.NumThreads = config.SherpaOnnx.NumThreads;
                diarConfig.Segmentation.Debug = 0;
                diarConfig.Segmentation.Provider = "cpu";
                diarConfig.Embedding.Model = modelPath;
                diarConfig.Embedding.NumThreads = config.SherpaOnnx.NumThreads;
                diarConfig.Embedding.Debug = 0;
                diarConfig.Embedding.Provider = "cpu";
                diarConfig.Clustering.NumClusters = -1; // -1 = use threshold mode by default
                diarConfig.Clustering.Threshold = config.SherpaOnnx.ClusteringThreshold;
                diarConfig.MinDurationOn = config.SherpaOnnx.MinDurationOn;
                diarConfig.MinDurationOff = config.SherpaOnnx.MinDurationOff;

                _diarizer = new OfflineSpeakerDiarization(diarConfig);
                _logger.LogInformation(
                    "OfflineSpeakerDiarization initialisé (segmentation: {Seg}, embedding: {Emb}).",
                    segPath, modelPath);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Impossible d'initialiser OfflineSpeakerDiarization. Diarisation : fallback legacy.");
            }
        }
        else if (_extractor is not null)
        {
            _logger.LogWarning(
                "Modèle de segmentation pyannote introuvable : {Path}. Diarisation : fallback legacy (clustering greedy).",
                segPath);
        }
    }

    /// <summary>Charge tous les embeddings actifs en mémoire (parallèle, max 4 lectures simultanées).</summary>
    public async Task InitializeAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var embeddings = await db.SpeakerEmbeddings
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

        float[] samples = ConvertWavToFloat(audioData);
        var embedding = ExtractEmbeddingFromSamples(samples);
        if (embedding is null)
            return Task.FromResult(SpeakerIdentificationResult.Unknown(_confidenceThreshold));

        return IdentifyAsync(embedding);
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
            await using var db = await _dbFactory.CreateDbContextAsync();
            var profile = await db.SpeakerProfiles.FindAsync(bestProfileId.Value);
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

        await using (var db = await _dbFactory.CreateDbContextAsync())
        {
            db.SpeakerProfiles.Add(new SpeakerProfileEntity { Id = profileId, Name = name, CreatedAt = DateTime.UtcNow, IsActive = true });
            db.SpeakerEmbeddings.Add(new SpeakerEmbeddingEntity { Id = embeddingId, ProfileId = profileId, FilePath = filePath, CapturedAt = DateTime.UtcNow, InitialConfidence = initialConfidence, AudioDurationSeconds = audioDurationSeconds });
            await db.SaveChangesAsync();
        }

        AddToCache(profileId, embedding);
        _logger.LogInformation("Locuteur '{Name}' (ID={Id}) enregistré.", name, profileId);

        return new SpeakerProfile { Id = profileId, Name = name, CreatedAt = DateTime.UtcNow, IsActive = true };
    }

    public async Task AddEmbeddingToProfileAsync(Guid profileId, float[] embedding, float confidence)
    {
        var embeddingId = Guid.NewGuid();
        var filePath = Path.Combine(_embeddingsDir, $"{profileId}_emb_{embeddingId}.bin");

        var bytes = System.Runtime.InteropServices.MemoryMarshal.AsBytes(embedding.AsSpan()).ToArray();
        await File.WriteAllBytesAsync(filePath, bytes);

        await using (var db = await _dbFactory.CreateDbContextAsync())
        {
            db.SpeakerEmbeddings.Add(new SpeakerEmbeddingEntity { Id = embeddingId, ProfileId = profileId, FilePath = filePath, CapturedAt = DateTime.UtcNow, InitialConfidence = confidence });
            await db.SaveChangesAsync();
        }

        AddToCache(profileId, embedding);
    }

    public async Task<SpeakerProfile?> GetProfileAsync(Guid profileId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var e = await db.SpeakerProfiles.Include(p => p.Embeddings).FirstOrDefaultAsync(p => p.Id == profileId);
        return e is null ? null : MapToProfile(e);
    }

    public async Task<IReadOnlyList<SpeakerProfile>> GetAllProfilesAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var entities = await db.SpeakerProfiles.Include(p => p.Embeddings).Where(p => p.IsActive).ToListAsync();
        return entities.Select(MapToProfile).ToList();
    }

    public async Task MergeProfilesAsync(Guid targetProfileId, Guid sourceProfileId)
    {
        await using (var db = await _dbFactory.CreateDbContextAsync())
        {
            var sourceEmbs = await db.SpeakerEmbeddings.Where(e => e.ProfileId == sourceProfileId).ToListAsync();
            foreach (var emb in sourceEmbs) emb.ProfileId = targetProfileId;

            var source = await db.SpeakerProfiles.FindAsync(sourceProfileId);
            if (source is not null) db.SpeakerProfiles.Remove(source);
            await db.SaveChangesAsync();
        }

        lock (_cacheLock)
        {
            if (_embeddingCache.TryGetValue(sourceProfileId, out var vecs))
            {
                if (!_embeddingCache.TryGetValue(targetProfileId, out var target))
                {
                    target = new List<float[]>();
                    _embeddingCache[targetProfileId] = target;
                }
                target.AddRange(vecs);
                _embeddingCache.Remove(sourceProfileId);
            }
        }
    }

    public async Task RenameProfileAsync(Guid profileId, string newName)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var profile = await db.SpeakerProfiles.FindAsync(profileId)
            ?? throw new KeyNotFoundException($"Profil {profileId} introuvable.");
        profile.Name = newName;
        await db.SaveChangesAsync();
    }

    public async Task DeleteProfileAsync(Guid profileId)
    {
        await using (var db = await _dbFactory.CreateDbContextAsync())
        {
            var profile = await db.SpeakerProfiles.Include(p => p.Embeddings).FirstOrDefaultAsync(p => p.Id == profileId);
            if (profile is null) return;
            foreach (var emb in profile.Embeddings)
                if (File.Exists(emb.FilePath)) File.Delete(emb.FilePath);
            db.SpeakerProfiles.Remove(profile);
            await db.SaveChangesAsync();
        }
        lock (_cacheLock) { _embeddingCache.Remove(profileId); }
    }

    public async Task LinkSpeakersAsync(Guid knownProfileId, Guid unknownProfileId)
    {
        await using (var db = await _dbFactory.CreateDbContextAsync())
        {
            var known = await db.SpeakerProfiles.FindAsync(knownProfileId)
                ?? throw new KeyNotFoundException($"Profil connu {knownProfileId} introuvable.");
            var unknown = await db.SpeakerProfiles.Include(p => p.Embeddings).FirstOrDefaultAsync(p => p.Id == unknownProfileId)
                ?? throw new KeyNotFoundException($"Profil inconnu {unknownProfileId} introuvable.");

            var aliases = string.IsNullOrEmpty(known.AliasesJson)
                ? new List<string>()
                : JsonSerializer.Deserialize<List<string>>(known.AliasesJson) ?? new();
            if (!aliases.Contains(unknown.Name)) { aliases.Add(unknown.Name); known.AliasesJson = JsonSerializer.Serialize(aliases); }

            var unknownEmbs = await db.SpeakerEmbeddings.Where(e => e.ProfileId == unknownProfileId).ToListAsync();
            foreach (var emb in unknownEmbs) emb.ProfileId = knownProfileId;
            db.SpeakerProfiles.Remove(unknown);
            await db.SaveChangesAsync();
        }

        lock (_cacheLock)
        {
            if (_embeddingCache.TryGetValue(unknownProfileId, out var vecs))
            {
                if (!_embeddingCache.TryGetValue(knownProfileId, out var target))
                {
                    target = new List<float[]>();
                    _embeddingCache[knownProfileId] = target;
                }
                target.AddRange(vecs);
                _embeddingCache.Remove(unknownProfileId);
            }
        }
    }

    public async Task UpdateLastSeenAsync(Guid profileId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var profile = await db.SpeakerProfiles.FindAsync(profileId);
        if (profile is null) return;
        profile.LastSeenAt = DateTime.UtcNow;
        profile.DetectionCount++;
        await db.SaveChangesAsync();
    }

    /// <summary>Helper interne : ajoute un embedding au cache thread-safe (DRY pour Enroll/Add/Merge/Link).</summary>
    private void AddToCache(Guid profileId, float[] embedding)
    {
        lock (_cacheLock)
        {
            if (!_embeddingCache.TryGetValue(profileId, out var list))
            {
                list = new List<float[]>();
                _embeddingCache[profileId] = list;
            }
            list.Add(embedding);
        }
    }

    public Task<float[]?> ExtractEmbeddingAsync(byte[] audioData, CancellationToken ct = default)
    {
        if (_extractor is null) return Task.FromResult<float[]?>(null);
        float[] samples = ConvertWavToFloat(audioData);
        return Task.FromResult(ExtractEmbeddingFromSamples(samples));
    }

    public Task<bool> CheckHealthAsync() => Task.FromResult(_extractor is not null);

    /// <inheritdoc/>
    public async Task<IReadOnlyDictionary<int, SpeakerLabel>> DiarizeAudioAsync(
        float[] audioSamples,
        IReadOnlyList<Vad.VadSegment> vadSegments,
        CancellationToken ct = default,
        int? numSpeakers = null)
    {
        if (_extractor is null || vadSegments.Count == 0 || audioSamples.Length == 0)
            return new Dictionary<int, SpeakerLabel>();

        // Pas de modèle de segmentation pyannote → fallback sur le clustering greedy legacy
        if (_diarizer is null)
            return await DiarizeFallback(vadSegments, ct, numSpeakers);

        // ── 1. Configurer dynamiquement le nombre de clusters si fourni ─────────
        // sherpa-onnx accepte SetConfig à chaud — pas besoin de recharger le modèle.
        lock (_diarizerLock)
        {
            var liveCfg = new OfflineSpeakerDiarizationConfig();
            liveCfg.Clustering.NumClusters = numSpeakers ?? -1;
            liveCfg.Clustering.Threshold = _clusteringThreshold;
            _diarizer.SetConfig(liveCfg);
        }

        // ── 2. Lancer le pipeline pyannote-3.0 (sync — wrappé en Task.Run) ──────
        OfflineSpeakerDiarizationSegment[] diarSegments;
        try
        {
            diarSegments = await Task.Run(() => _diarizer.Process(audioSamples), ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "OfflineSpeakerDiarization a échoué — fallback legacy.");
            return await DiarizeFallback(vadSegments, ct, numSpeakers);
        }

        if (diarSegments.Length == 0)
            return new Dictionary<int, SpeakerLabel>();

        _logger.LogInformation(
            "Diarisation pyannote : {Segments} segments, {Speakers} locuteur(s) bruts.",
            diarSegments.Length,
            diarSegments.Select(s => s.Speaker).Distinct().Count());

        // ── 3. Pour chaque cluster int → extraire un embedding représentatif ────
        // et matcher contre les profils enrôlés (réutilise IdentifyAsync existant).
        int autoCount;
        await using (var db = await _dbFactory.CreateDbContextAsync(ct))
            autoCount = await db.SpeakerProfiles.CountAsync(p => p.IsActive, ct);

        var clusterToLabel = new Dictionary<int, SpeakerLabel>();
        foreach (var clusterId in diarSegments.Select(s => s.Speaker).Distinct())
        {
            ct.ThrowIfCancellationRequested();

            // Plus long segment du cluster comme représentant (meilleur SNR)
            var rep = diarSegments
                .Where(s => s.Speaker == clusterId)
                .OrderByDescending(s => s.End - s.Start)
                .First();

            int sliceStart = Math.Max(0, (int)(rep.Start * 16000));
            int sliceEnd = Math.Min(audioSamples.Length, (int)(rep.End * 16000));
            if (sliceEnd <= sliceStart) continue;

            var slice = audioSamples[sliceStart..sliceEnd];
            float[]? emb = ExtractEmbeddingFromSamples(slice);
            if (emb is null) continue;

            var ident = await IdentifyAsync(emb);
            if (ident.IsIdentified && ident.ProfileId.HasValue)
            {
                clusterToLabel[clusterId] = new SpeakerLabel(ident.ProfileId, ident.SpeakerName ?? $"Locuteur {clusterId + 1}");
                await UpdateLastSeenAsync(ident.ProfileId.Value);
            }
            else
            {
                autoCount++;
                var name = $"Locuteur {autoCount}";
                int totalSec = (int)diarSegments.Where(s => s.Speaker == clusterId).Sum(s => s.End - s.Start);
                var profile = await EnrollSpeakerAsync(name, emb, ident.Confidence, audioDurationSeconds: totalSec);
                clusterToLabel[clusterId] = new SpeakerLabel(profile.Id, name);
                _logger.LogInformation("Diarisation : nouveau profil auto '{Name}' créé.", name);
            }
        }

        // ── 4. Mapper chaque VadSegment au cluster qui le recouvre le plus ──────
        var result = new Dictionary<int, SpeakerLabel>();
        for (int i = 0; i < vadSegments.Count; i++)
        {
            var v = vadSegments[i];
            float bestOverlap = 0f;
            int bestSpeaker = -1;
            foreach (var ds in diarSegments)
            {
                float overlap = Math.Max(0f, Math.Min(ds.End, v.EndSeconds) - Math.Max(ds.Start, v.StartSeconds));
                if (overlap > bestOverlap) { bestOverlap = overlap; bestSpeaker = ds.Speaker; }
            }
            if (bestSpeaker >= 0 && clusterToLabel.TryGetValue(bestSpeaker, out var lbl))
                result[i] = lbl;
        }

        _logger.LogInformation("Diarisation : {Count} locuteur(s) finaux assignés.",
            result.Values.DistinctBy(l => l.ProfileId).Count());

        return result;
    }

    /// <summary>
    /// Centralise l'extraction d'embedding depuis un buffer PCM float32 16 kHz mono.
    /// Retourne null si l'extracteur n'est pas chargé ou en cas d'erreur.
    /// </summary>
    private float[]? ExtractEmbeddingFromSamples(float[] samples)
    {
        if (_extractor is null || samples.Length == 0) return null;
        try
        {
            var stream = _extractor.CreateStream();
            stream.AcceptWaveform(16000, samples);
            stream.InputFinished();
            return _extractor.Compute(stream);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "ExtractEmbeddingFromSamples a échoué.");
            return null;
        }
    }

    /// <summary>
    /// Implémentation legacy : clustering greedy + fusion agglomérative + propagation.
    /// Conservée comme fallback quand le modèle de segmentation pyannote est absent.
    /// À supprimer une fois la migration validée en production.
    /// </summary>
    private async Task<IReadOnlyDictionary<int, SpeakerLabel>> DiarizeFallback(
        IReadOnlyList<Vad.VadSegment> segments,
        CancellationToken ct,
        int? numSpeakers)
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
        int autoCount;
        await using (var db = await _dbFactory.CreateDbContextAsync(ct))
        {
            autoCount = await db.SpeakerProfiles.CountAsync(p => p.IsActive, ct);
        }

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
        _diarizer?.Dispose();
        _extractor?.Dispose();
    }
}
