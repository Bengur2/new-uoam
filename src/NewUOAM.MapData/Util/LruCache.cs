namespace NewUOAM.MapData.Util;

/// <summary>
/// Small O(1) get/set LRU cache. Node-based: eviction order is tracked with a
/// <see cref="LinkedList{T}"/> of nodes the dictionary points straight at, so touching an entry
/// is <c>LinkedList.Remove(node)</c> (O(1)) instead of <c>LinkedList.Remove(value)</c> (an O(n)
/// scan for the matching node) - that exact mistake, in two hand-rolled caches this type
/// replaced, made preloading a whole facet effectively quadratic (minutes instead of seconds
/// for a ~25M-tile map) once the cache was full and every lookup had to scan it.
/// </summary>
public sealed class LruCache<TKey, TValue> where TKey : notnull
{
    private readonly int _capacity;
    private readonly Dictionary<TKey, LinkedListNode<(TKey Key, TValue Value)>> _map;
    private readonly LinkedList<(TKey Key, TValue Value)> _order = new();
    private readonly object _lock = new();

    public LruCache(int capacity)
    {
        _capacity = Math.Max(1, capacity);
        _map = new Dictionary<TKey, LinkedListNode<(TKey, TValue)>>(_capacity);
    }

    public bool TryGet(TKey key, out TValue value)
    {
        lock (_lock)
        {
            if (_map.TryGetValue(key, out var node))
            {
                _order.Remove(node);
                _order.AddLast(node);
                value = node.Value.Value;
                return true;
            }
        }
        value = default!;
        return false;
    }

    public void Set(TKey key, TValue value)
    {
        lock (_lock)
        {
            if (_map.TryGetValue(key, out var existing))
                _order.Remove(existing);

            var node = _order.AddLast((key, value));
            _map[key] = node;

            while (_map.Count > _capacity)
            {
                var oldest = _order.First!;
                _order.RemoveFirst();
                _map.Remove(oldest.Value.Key);
            }
        }
    }

    public TValue GetOrAdd(TKey key, Func<TKey, TValue> factory)
    {
        if (TryGet(key, out var existing)) return existing;
        var created = factory(key);
        Set(key, created);
        return created;
    }
}
