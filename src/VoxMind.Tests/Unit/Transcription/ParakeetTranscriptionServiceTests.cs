using Microsoft.Extensions.Logging.Abstractions;
using VoxMind.Core.Transcription;
using VoxMind.Core.Vad;
using Xunit;

namespace VoxMind.Tests.Unit.Transcription;

public class ParakeetTranscriptionServiceTests : IDisposable
{
    private readonly ParakeetOnnxTranscriptionService _service;

    public ParakeetTranscriptionServiceTests()
    {
        _service = new ParakeetOnnxTranscriptionService(
            "/nonexistent/model/path",
            new DisabledVadService(),
            NullLogger<ParakeetOnnxTranscriptionService>.Instance);
    }

    [Fact]
    public async Task DetectLanguage_AlwaysReturnsEn()
    {
        var result = await _service.DetectLanguageAsync(Array.Empty<byte>());

        Assert.Equal("en", result);
    }

    [Fact]
    public void Info_InitialState_IsNotLoaded()
    {
        Assert.False(_service.Info.IsLoaded);
        Assert.Equal("parakeet-tdt-0.6b-v3-int8", _service.Info.ModelName);
    }

    [Fact]
    public async Task TranscribeChunk_WhenModelNotLoaded_ReturnsEmptyResult()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var result = await _service.TranscribeChunkAsync(new byte[100], cts.Token);

        Assert.NotNull(result);
        Assert.Equal(string.Empty, result.Text);
    }

    public void Dispose() => _service.Dispose();
}
