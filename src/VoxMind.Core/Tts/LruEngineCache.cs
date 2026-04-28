namespace VoxMind.Core.Tts;

/// <summary>
/// Cache LRU thread-safe pour des moteurs F5-TTS résidents en RAM.
///
/// On charge un moteur F5 par langue à la demande (cold-load ~2-4 s pour
/// les 3 ONNX). Une fois chargé, on garde le moteur résident pour les appels
/// suivants. Quand le cache déborde (capacité = 2 par défaut, FR + EN), on
/// évince la langue la moins récemment utilisée et on libère ses sessions ONNX.
/// </summary>
public sealed class LruEngineCache<TEngine> : IDisposable where TEngine : IDisposable
{
    private readonly int _capacity;
    private readonly LinkedList<(string Key, TEngine Engine)> _order = new();
    private readonly Dictionary<string, LinkedListNode<(string Key, TEngine Engine)>> _index = new();
    private readonly object _lock = new();
    private bool _disposed;

    public LruEngineCache(int capacity)
    {
        if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
    }

    public int Count
    {
        get { lock (_lock) return _index.Count; }
    }

    public IReadOnlyList<string> ResidentKeys
    {
        get { lock (_lock) return _index.Keys.ToList(); }
    }

    public bool TryGet(string key, out TEngine engine)
    {
        lock (_lock)
        {
            if (_index.TryGetValue(key, out var node))
            {
                _order.Remove(node);
                _order.AddFirst(node);
                engine = node.Value.Engine;
                return true;
            }
            engine = default!;
            return false;
        }
    }

    /// <summary>
    /// Récupère ou charge un moteur. Le <paramref name="factory"/> est appelé hors verrou
    /// pour éviter de bloquer pendant le chargement (~2-4 s).
    /// </summary>
    public TEngine GetOrLoad(string key, Func<TEngine> factory)
    {
        if (TryGet(key, out var existing)) return existing;

        // Chargement hors verrou — peut être lent
        var newEngine = factory();

        lock (_lock)
        {
            // Course possible : un autre thread peut avoir chargé entre temps
            if (_index.TryGetValue(key, out var existingNode))
            {
                newEngine.Dispose();
                _order.Remove(existingNode);
                _order.AddFirst(existingNode);
                return existingNode.Value.Engine;
            }

            var node = new LinkedListNode<(string, TEngine)>((key, newEngine));
            _order.AddFirst(node);
            _index[key] = node;

            while (_index.Count > _capacity && _order.Last is { } evict)
            {
                _order.RemoveLast();
                _index.Remove(evict.Value.Key);
                evict.Value.Engine.Dispose();
            }

            return newEngine;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (_lock)
        {
            foreach (var entry in _order)
                entry.Engine.Dispose();
            _order.Clear();
            _index.Clear();
        }
    }
}
