using VoxMind.Core.Transcription;
using VoxMind.Core.Tts;
using Xunit;

namespace VoxMind.Tests.Unit.Tts;

public class TtsEngineRegistryTests
{
    private sealed class StubTtsService : ITtsService
    {
        public TtsModelInfo Info { get; }
        public StubTtsService(string name, bool loaded)
        {
            Info = new TtsModelInfo
            {
                EngineName = name,
                Backend = ComputeBackend.CPU,
                IsLoaded = loaded,
                AvailableLanguages = loaded ? new[] { "fr", "en" } : Array.Empty<string>(),
            };
        }
        public Task<TtsResult> SynthesizeAsync(
            string text, string? language = null, byte[]? referenceWav = null,
            string? referenceText = null, CancellationToken ct = default)
            => throw new NotImplementedException();
        public void Dispose() { }
    }

    [Fact]
    public void Get_NoNameSpecified_ReturnsDefaultEngine()
    {
        var f5 = new StubTtsService("f5", loaded: true);
        var xtts = new StubTtsService("xtts", loaded: false);
        var registry = new TtsEngineRegistry(
            new Dictionary<string, ITtsService> { ["f5"] = f5, ["xtts"] = xtts },
            defaultEngine: "f5");

        var got = registry.Get();

        Assert.Same(f5, got);
    }

    [Fact]
    public void Get_KnownName_ReturnsRequestedEngine()
    {
        var f5 = new StubTtsService("f5", loaded: true);
        var xtts = new StubTtsService("xtts", loaded: false);
        var registry = new TtsEngineRegistry(
            new Dictionary<string, ITtsService> { ["f5"] = f5, ["xtts"] = xtts },
            defaultEngine: "f5");

        var got = registry.Get("xtts");

        Assert.Same(xtts, got);
    }

    [Fact]
    public void Get_UnknownName_FallsBackToDefault()
    {
        var f5 = new StubTtsService("f5", loaded: true);
        var registry = new TtsEngineRegistry(
            new Dictionary<string, ITtsService> { ["f5"] = f5 },
            defaultEngine: "f5");

        var got = registry.Get("does-not-exist");

        Assert.Same(f5, got);
    }

    [Fact]
    public void Constructor_DefaultEngineMissing_Throws()
    {
        var f5 = new StubTtsService("f5", loaded: true);
        Assert.Throws<ArgumentException>(() =>
            new TtsEngineRegistry(
                new Dictionary<string, ITtsService> { ["f5"] = f5 },
                defaultEngine: "missing"));
    }

    [Fact]
    public void Constructor_EmptyDictionary_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            new TtsEngineRegistry(new Dictionary<string, ITtsService>(), defaultEngine: "f5"));
    }

    [Fact]
    public void ListAll_ReturnsAllRegisteredEngines()
    {
        var f5 = new StubTtsService("f5", loaded: true);
        var xtts = new StubTtsService("xtts", loaded: false);
        var registry = new TtsEngineRegistry(
            new Dictionary<string, ITtsService> { ["f5"] = f5, ["xtts"] = xtts },
            defaultEngine: "f5");

        var all = registry.ListAll();

        Assert.Equal(2, all.Count);
        Assert.True(all["f5"].IsLoaded);
        Assert.False(all["xtts"].IsLoaded);
    }
}
