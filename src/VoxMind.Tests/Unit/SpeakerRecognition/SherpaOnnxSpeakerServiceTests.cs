using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using VoxMind.Core.Configuration;
using VoxMind.Core.Database;
using VoxMind.Core.SpeakerRecognition;
using Xunit;

namespace VoxMind.Tests.Unit.SpeakerRecognition;

/// <summary>
/// Tests de base pour SherpaOnnxSpeakerService (sans modèle ONNX — extractor = null).
/// Les tests d'identification réelle nécessitent le modèle sherpa-onnx téléchargé.
/// </summary>
public class SherpaOnnxSpeakerServiceTests : IDisposable
{
    private readonly VoxMindDbContext _db;
    private readonly SherpaOnnxSpeakerService _service;
    private readonly string _tmpDir;

    public SherpaOnnxSpeakerServiceTests()
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
    public async Task CheckHealth_WithoutModel_ReturnsFalse()
    {
        var result = await _service.CheckHealthAsync();
        Assert.False(result);
    }

    [Fact]
    public async Task IdentifyFromAudio_WithoutModel_ReturnsUnknown()
    {
        var result = await _service.IdentifyFromAudioAsync(new byte[1024]);
        Assert.False(result.IsIdentified);
    }

    [Fact]
    public async Task ExtractEmbedding_WithoutModel_ReturnsNull()
    {
        var result = await _service.ExtractEmbeddingAsync(new byte[1024]);
        Assert.Null(result);
    }

    [Fact]
    public async Task EnrollAndIdentify_WithManualEmbedding_Works()
    {
        var embedding = new float[512];
        for (int i = 0; i < 512; i++) embedding[i] = 1.0f / MathF.Sqrt(512);

        var profile = await _service.EnrollSpeakerAsync("Test", embedding, 1.0f);
        Assert.NotNull(profile);
        Assert.Equal("Test", profile.Name);

        var result = await _service.IdentifyAsync(embedding);
        Assert.True(result.IsIdentified);
        Assert.Equal(profile.Id, result.ProfileId);
    }

    [Fact]
    public void Dispose_DoesNotThrow()
    {
        var ex = Record.Exception(() => _service.Dispose());
        Assert.Null(ex);
    }

    public void Dispose()
    {
        _service.Dispose();
        _db.Dispose();
        if (Directory.Exists(_tmpDir))
            Directory.Delete(_tmpDir, recursive: true);
    }
}
