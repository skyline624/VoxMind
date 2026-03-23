using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using VoxMind.Core.Configuration;
using VoxMind.Core.Database;
using VoxMind.Core.SpeakerRecognition;
using Xunit;

namespace VoxMind.Tests.Unit.SpeakerRecognition;

public class SpeakerMergeTests : IDisposable
{
    private readonly VoxMindDbContext _db;
    private readonly SherpaOnnxSpeakerService _service;
    private readonly string _tmpDir;

    public SpeakerMergeTests()
    {
        var options = new DbContextOptionsBuilder<VoxMindDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _db = new VoxMindDbContext(options);

        _tmpDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(_tmpDir);

        var config = new SpeakerRecognitionConfig
        {
            SherpaOnnx = new SherpaOnnxConfig { EmbeddingModelPath = "nonexistent.onnx" }
        };
        _service = new SherpaOnnxSpeakerService(
            config, _db,
            NullLogger<SherpaOnnxSpeakerService>.Instance,
            embeddingsDir: _tmpDir
        );
    }

    [Fact]
    public async Task MergeProfiles_TransfersAllEmbeddingsAndDeletesSource()
    {
        // Arrange
        var emb1 = GenerateEmbedding();
        var emb2 = GenerateEmbedding();
        var profile1 = await _service.EnrollSpeakerAsync("Profile1", emb1, 0.9f);
        var profile2 = await _service.EnrollSpeakerAsync("Profile2", emb2, 0.85f);

        // Act
        await _service.MergeProfilesAsync(profile1.Id, profile2.Id);

        // Assert
        var merged = await _service.GetProfileAsync(profile1.Id);
        Assert.NotNull(merged);
        Assert.Equal(2, merged!.Embeddings.Count); // Les 2 embeddings dans profile1

        var deleted = await _service.GetProfileAsync(profile2.Id);
        Assert.Null(deleted); // profile2 supprimé
    }

    [Fact]
    public async Task LinkSpeakers_UnknownToKnown_TransfersEmbeddingsAndAddsAlias()
    {
        // Arrange
        var known = await _service.EnrollSpeakerAsync("Marjorie", GenerateEmbedding(), 0.95f);
        var unknown = await _service.EnrollSpeakerAsync("Inconnu #1", GenerateEmbedding(), 0.7f);

        // Act
        await _service.LinkSpeakersAsync(known.Id, unknown.Id);

        // Assert
        var updatedKnown = await _service.GetProfileAsync(known.Id);
        Assert.NotNull(updatedKnown);
        Assert.Contains("Inconnu #1", updatedKnown!.Aliases);

        var unknownDeleted = await _service.GetProfileAsync(unknown.Id);
        Assert.Null(unknownDeleted);
    }

    [Fact]
    public async Task IdentifyAsync_KnownSpeaker_ReturnsCorrectProfile()
    {
        // Arrange
        var embedding = GenerateEmbedding();
        var enrolled = await _service.EnrollSpeakerAsync("Alex", embedding, 0.9f);

        // Act — même embedding, devrait être identifié
        var result = await _service.IdentifyAsync(embedding);

        // Assert
        Assert.True(result.IsIdentified);
        Assert.Equal(enrolled.Id, result.ProfileId);
        Assert.Equal("Alex", result.SpeakerName);
    }

    [Fact]
    public async Task IdentifyAsync_UnknownSpeaker_ReturnsNotIdentified()
    {
        // Arrange — enroller un locuteur avec un embedding fixe
        var knownEmbedding = new float[512];
        // Remplir avec des 1.0 normalisés
        for (int i = 0; i < 512; i++) knownEmbedding[i] = 1.0f / MathF.Sqrt(512);
        await _service.EnrollSpeakerAsync("Known", knownEmbedding, 0.9f);

        // Un embedding très différent (orthogonal)
        var unknownEmbedding = new float[512];
        unknownEmbedding[0] = 1.0f; // Vecteur unitaire dans direction [0]

        // Act
        var result = await _service.IdentifyAsync(unknownEmbedding);

        // Assert — similarité cosinus très faible, ne devrait pas être identifié
        Assert.False(result.IsIdentified);
    }

    [Fact]
    public async Task EmbeddingCache_ConcurrentReads_DoesNotThrow()
    {
        // Arrange — populer le cache séquentiellement (EF Core DbContext non thread-safe)
        var profile = await _service.EnrollSpeakerAsync("Concurrent", GenerateEmbedding(), 0.9f);
        for (int i = 0; i < 5; i++)
            await _service.AddEmbeddingToProfileAsync(profile.Id, GenerateEmbeddingRandom(), 0.8f);

        // Act — 30 lectures simultanées sur le cache (scénario réel : pipeline audio continu)
        var tasks = Enumerable.Range(0, 30)
            .Select(_ => _service.IdentifyAsync(GenerateEmbeddingRandom()));

        var ex = await Record.ExceptionAsync(() => Task.WhenAll(tasks));
        Assert.Null(ex);
    }

    private static float[] GenerateEmbedding()
    {
        var rng = new Random(42);
        var emb = new float[512];
        float norm = 0;
        for (int i = 0; i < 512; i++) { emb[i] = (float)rng.NextDouble(); norm += emb[i] * emb[i]; }
        norm = MathF.Sqrt(norm);
        for (int i = 0; i < 512; i++) emb[i] /= norm;
        return emb;
    }

    private static float[] GenerateEmbeddingRandom()
    {
        var emb = new float[512];
        float norm = 0;
        for (int i = 0; i < 512; i++) { emb[i] = (float)Random.Shared.NextDouble(); norm += emb[i] * emb[i]; }
        norm = MathF.Sqrt(norm);
        for (int i = 0; i < 512; i++) emb[i] /= norm;
        return emb;
    }

    public void Dispose()
    {
        _service.Dispose();
        _db.Dispose();
        Directory.Delete(_tmpDir, recursive: true);
    }
}
