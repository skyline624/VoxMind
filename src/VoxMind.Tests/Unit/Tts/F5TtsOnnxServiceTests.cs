using Microsoft.Extensions.Logging.Abstractions;
using VoxMind.Core.Configuration;
using VoxMind.Core.Tts;
using Xunit;

namespace VoxMind.Tests.Unit.Tts;

/// <summary>
/// Tests de graceful degradation pour F5TtsOnnxService :
/// pas de checkpoint sur disque ⇒ Info.IsLoaded == false et SynthesizeAsync jette NotSupportedException.
///
/// Les tests d'inférence réelle sont dans <c>RequiresModel</c> et désactivés en CI par défaut.
/// </summary>
public class F5TtsOnnxServiceTests : IDisposable
{
    private readonly F5TtsOnnxService _service;

    public F5TtsOnnxServiceTests()
    {
        var config = new TtsConfig
        {
            Enabled = true,
            DefaultEngine = "f5",
            DefaultLanguage = "fr",
            CacheCapacity = 2,
            FlowMatchingSteps = 32,
            Languages = new Dictionary<string, F5LanguageCheckpoint>
            {
                ["fr"] = new F5LanguageCheckpoint
                {
                    Language = "fr",
                    PreprocessModelPath = "/nonexistent/fr_preprocess.onnx",
                    TransformerModelPath = "/nonexistent/fr_transformer.onnx",
                    DecodeModelPath = "/nonexistent/fr_decode.onnx",
                    TokensPath = "/nonexistent/fr_tokens.txt",
                    DefaultReferenceWav = "/nonexistent/fr_ref.wav",
                    DefaultReferenceText = "Bonjour.",
                },
            },
        };

        _service = new F5TtsOnnxService(config, NullLogger<F5TtsOnnxService>.Instance);
    }

    [Fact]
    public void Info_WithoutCheckpoints_IsNotLoaded()
    {
        Assert.False(_service.Info.IsLoaded);
        Assert.Empty(_service.Info.AvailableLanguages);
        Assert.Equal("f5-tts-onnx", _service.Info.EngineName);
    }

    [Fact]
    public async Task Synthesize_OnEmptyText_ThrowsArgumentException()
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _service.SynthesizeAsync("", language: "fr"));
    }

    [Fact]
    public async Task Synthesize_OnUnknownLanguage_ThrowsNotSupported()
    {
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            _service.SynthesizeAsync("Bonjour", language: "zh"));
    }

    [Fact]
    public async Task Synthesize_WhenCheckpointMissing_ThrowsNotSupported()
    {
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            _service.SynthesizeAsync("Bonjour", language: "fr"));
    }

    [Fact]
    public void Info_ResidentLanguages_StartsEmpty()
    {
        Assert.Empty(_service.Info.ResidentLanguages);
    }

    public void Dispose() => _service.Dispose();
}
