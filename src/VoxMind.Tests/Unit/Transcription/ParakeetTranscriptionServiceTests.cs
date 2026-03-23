using Microsoft.Extensions.Logging.Abstractions;
using VoxMind.Core.Transcription;
using Xunit;

namespace VoxMind.Tests.Unit.Transcription;

public class ParakeetTranscriptionServiceTests : IDisposable
{
    // Port intentionnellement fermé pour tester la gestion d'erreur
    private const string UnreachableEndpoint = "localhost:59998";

    private readonly ParakeetTranscriptionService _service;

    public ParakeetTranscriptionServiceTests()
    {
        _service = new ParakeetTranscriptionService(
            UnreachableEndpoint,
            NullLogger<ParakeetTranscriptionService>.Instance);
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
        Assert.Equal("parakeet-ctc-1.1b", _service.Info.ModelName);
    }

    [Fact]
    public async Task TranscribeChunk_WhenGrpcUnavailable_ReturnsEmptyResult()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var result = await _service.TranscribeChunkAsync(new byte[100], cts.Token);

        Assert.NotNull(result);
        Assert.Equal(string.Empty, result.Text);
    }

    public void Dispose() => _service.Dispose();
}
