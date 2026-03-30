using System.Text.Json;
using VoxMind.Core.Configuration;
using Xunit;

namespace VoxMind.Tests.Unit.Configuration;

public class ConfigurationLoaderTests : IDisposable
{
    private readonly string _tmpDir;

    // Doit correspondre aux options utilisées dans ConfigurationLoader
    private static readonly JsonSerializerOptions _writeOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public ConfigurationLoaderTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(_tmpDir);
    }

    [Fact]
    public void Load_ValidFile_ReturnsConfiguration()
    {
        // Arrange
        var configPath = Path.Combine(_tmpDir, "config.json");
        var config = new AppConfiguration
        {
            Application = new ApplicationConfig { Name = "TestVox", Version = "2.0.0" },
            Audio = new AudioConfig { DefaultSampleRate = 22050 }
        };
        File.WriteAllText(configPath, JsonSerializer.Serialize(config, _writeOptions));

        // Act
        var loaded = ConfigurationLoader.Load(configPath);

        // Assert
        Assert.NotNull(loaded);
        Assert.Equal("TestVox", loaded.Application.Name);
        Assert.Equal(22050, loaded.Audio.DefaultSampleRate);
    }

    [Fact]
    public void Load_FileNotFound_ThrowsFileNotFoundException()
    {
        var path = Path.Combine(_tmpDir, "nonexistent.json");
        Assert.Throws<FileNotFoundException>(() => ConfigurationLoader.Load(path));
    }

    [Fact]
    public void Load_DefaultValues_AppliedWhenMissing()
    {
        // Arrange
        var configPath = Path.Combine(_tmpDir, "minimal.json");
        File.WriteAllText(configPath, "{}");

        // Act
        var loaded = ConfigurationLoader.Load(configPath);

        // Assert
        Assert.Equal(16000, loaded.Audio.DefaultSampleRate); // Valeur par défaut
        Assert.Equal(100, loaded.Audio.DefaultChunkDurationMs);
        Assert.Equal(0.7f, loaded.Ml.SpeakerRecognition.ConfidenceThreshold);
    }

    [Fact]
    public void Validate_InvalidConfidenceThreshold_ThrowsInvalidOperationException()
    {
        var configPath = Path.Combine(_tmpDir, "invalid.json");
        var config = new AppConfiguration();
        config.Ml.SpeakerRecognition.ConfidenceThreshold = 1.5f; // Invalide
        File.WriteAllText(configPath, JsonSerializer.Serialize(config, _writeOptions));

        Assert.Throws<InvalidOperationException>(() => ConfigurationLoader.Load(configPath));
    }

    public void Dispose()
    {
        Directory.Delete(_tmpDir, recursive: true);
    }
}
