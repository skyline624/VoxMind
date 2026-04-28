using VoxMind.Core.Tts;
using Xunit;

namespace VoxMind.Tests.Unit.Tts;

public class LruEngineCacheTests
{
    private sealed class FakeEngine : IDisposable
    {
        public string Key { get; }
        public bool IsDisposed { get; private set; }
        public FakeEngine(string key) => Key = key;
        public void Dispose() => IsDisposed = true;
    }

    [Fact]
    public void GetOrLoad_FirstCall_LoadsAndStores()
    {
        using var cache = new LruEngineCache<FakeEngine>(capacity: 2);

        var fr = cache.GetOrLoad("fr", () => new FakeEngine("fr"));

        Assert.Equal("fr", fr.Key);
        Assert.Single(cache.ResidentKeys);
    }

    [Fact]
    public void GetOrLoad_SameKeyTwice_ReusesInstance()
    {
        using var cache = new LruEngineCache<FakeEngine>(capacity: 2);
        int factoryCalls = 0;

        var first = cache.GetOrLoad("fr", () =>
        {
            factoryCalls++;
            return new FakeEngine("fr");
        });
        var second = cache.GetOrLoad("fr", () =>
        {
            factoryCalls++;
            return new FakeEngine("fr");
        });

        Assert.Same(first, second);
        Assert.Equal(1, factoryCalls);
    }

    [Fact]
    public void GetOrLoad_BeyondCapacity_EvictsLeastRecent()
    {
        using var cache = new LruEngineCache<FakeEngine>(capacity: 2);
        var fr = cache.GetOrLoad("fr", () => new FakeEngine("fr"));
        var en = cache.GetOrLoad("en", () => new FakeEngine("en"));

        // Touche 'fr' pour qu'il soit MRU
        cache.GetOrLoad("fr", () => throw new Exception("not called"));

        // Charger 'de' évince le LRU = 'en'
        var de = cache.GetOrLoad("de", () => new FakeEngine("de"));

        Assert.True(en.IsDisposed);
        Assert.False(fr.IsDisposed);
        Assert.False(de.IsDisposed);
        Assert.Equal(2, cache.Count);
        Assert.Contains("fr", cache.ResidentKeys);
        Assert.Contains("de", cache.ResidentKeys);
    }

    [Fact]
    public void Dispose_DisposesAllResidentEngines()
    {
        var cache = new LruEngineCache<FakeEngine>(capacity: 4);
        var fr = cache.GetOrLoad("fr", () => new FakeEngine("fr"));
        var en = cache.GetOrLoad("en", () => new FakeEngine("en"));

        cache.Dispose();

        Assert.True(fr.IsDisposed);
        Assert.True(en.IsDisposed);
    }

    [Fact]
    public void TryGet_OnMissingKey_ReturnsFalse()
    {
        using var cache = new LruEngineCache<FakeEngine>(capacity: 2);
        var hit = cache.TryGet("zh", out _);
        Assert.False(hit);
    }
}
