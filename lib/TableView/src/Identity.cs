using System.Collections;

namespace Syno.TableView;

internal interface IIdentity
{
    IdentityMap<T> CreateMap<T>();
    bool IsSame(object? left, object? right);
}

/// <summary>
/// A map keyed by row identity. Every lookup reads the row's key; the contract keeps keys stable
/// and the selector inexpensive, so no map stores a key of its own.
/// </summary>
internal abstract class IdentityMap<T> : IEnumerable<KeyValuePair<object, T>>
{
    internal abstract int Count { get; }
    internal abstract void Clear();
    internal abstract bool TryAdd(object item, T value);
    /// <summary>Store <paramref name="item"/> as the instance for its identity.</summary>
    internal abstract void Set(object item, T value);
    internal abstract bool TryGetValue(object item, out T value);
    internal abstract bool Remove(object item);
    /// <summary>The stored instance with <paramref name="item"/>'s identity, or null.</summary>
    internal abstract object? Resolve(object item);
    internal bool ContainsKey(object item) => TryGetValue(item, out _);
    internal T this[object item] => TryGetValue(item, out T value) ? value : throw new KeyNotFoundException();
    public abstract IEnumerator<KeyValuePair<object, T>> GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

internal sealed class Identity<TRow, TKey>(
    Func<TRow, TKey> key,
    IEqualityComparer<TKey>? comparer = null
) : IIdentity
    where TRow : class
    where TKey : notnull
{
    private readonly Func<TRow, TKey> _key = key;
    private readonly IEqualityComparer<TKey>? _comparer = comparer;

    internal static IIdentity Reference() =>
        new Identity<object, object>(item => item, ReferenceEqualityComparer.Instance);

    public IdentityMap<T> CreateMap<T>() => new Map<T>(this);

    private TKey ReadKey(object item)
    {
        TKey value = _key((TRow)item);
        if (value is null)
        {
            throw new InvalidOperationException("The schema key selector returned null.");
        }
        return value;
    }

    public bool IsSame(object? left, object? right) =>
        ReferenceEquals(left, right)
        || (left is not null && right is not null
            && (_comparer ?? EqualityComparer<TKey>.Default).Equals(ReadKey(left), ReadKey(right)));

    private sealed class Map<T>(Identity<TRow, TKey> identity) : IdentityMap<T>
    {
        private readonly Dictionary<TKey, Entry> _entries = new(identity._comparer);

        private readonly record struct Entry(object Item, T Value);

        internal override int Count => _entries.Count;

        internal override void Clear() => _entries.Clear();

        internal override bool TryAdd(object item, T value) => _entries.TryAdd(identity.ReadKey(item), new(item, value));

        internal override void Set(object item, T value) => _entries[identity.ReadKey(item)] = new(item, value);

        internal override bool TryGetValue(object item, out T value)
        {
            if (_entries.TryGetValue(identity.ReadKey(item), out Entry entry))
            {
                value = entry.Value;
                return true;
            }
            value = default!;
            return false;
        }

        internal override object? Resolve(object item) =>
            _entries.TryGetValue(identity.ReadKey(item), out Entry entry) ? entry.Item : null;

        internal override bool Remove(object item) => _entries.Remove(identity.ReadKey(item));

        // Every publish walks maps that are usually empty; an empty array's enumerator is shared,
        // where the iterator below is allocated for each walk.
        public override IEnumerator<KeyValuePair<object, T>> GetEnumerator() =>
            _entries.Count == 0 ? Enumerable.Empty<KeyValuePair<object, T>>().GetEnumerator() : Walk();

        private IEnumerator<KeyValuePair<object, T>> Walk()
        {
            foreach (Entry entry in _entries.Values)
            {
                yield return new(entry.Item, entry.Value);
            }
        }
    }
}

internal sealed class IdentitySet(IIdentity identity) : IReadOnlyCollection<object>
{
    internal IdentityMap<byte> Map { get; } = identity.CreateMap<byte>();
    public int Count => Map.Count;
    public bool Contains(object item) => Map.ContainsKey(item);
    internal bool Add(object item) => Map.TryAdd(item, 0);
    internal bool Remove(object item) => Map.Remove(item);
    internal void Clear() => Map.Clear();

    internal void UnionWith(IEnumerable<object> items)
    {
        foreach (object item in items)
        {
            Add(item);
        }
    }

    internal void SymmetricExceptWith(IEnumerable<object> items)
    {
        foreach (object item in items)
        {
            if (!Remove(item))
            {
                Add(item);
            }
        }
    }

    public IEnumerator<object> GetEnumerator()
    {
        foreach (var entry in Map)
        {
            yield return entry.Key;
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
