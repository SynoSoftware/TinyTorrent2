using System.Collections;

namespace Syno.TableView;

internal interface IIdentity
{
    IdentityMap<T> CreateMap<T>(bool keepsKeys = false);
    bool IsSame(object? left, object? right);
}

internal interface IIdentityKeys
{
    IIdentityKeys CapturedFor(object item);
    bool ContainsReference(object item);
}

internal abstract class IdentityMap<T> : IIdentityKeys, IEnumerable<KeyValuePair<object, T>>
{
    internal abstract int Count { get; }
    internal abstract void Clear();
    internal abstract bool TryAdd(object item, T value);
    internal abstract bool TryAdd(object item, T value, IIdentityKeys keys);
    internal abstract void Set(object item, T value);
    internal abstract void Set(object item, T value, IIdentityKeys keys);
    internal abstract bool TryGetValue(object item, out T value);
    internal abstract bool TryGetValue(object item, IIdentityKeys keys, out T value);
    internal abstract bool Remove(object item);
    internal abstract bool Remove(object item, IIdentityKeys keys);
    internal abstract object? Resolve(object item);
    internal abstract object? Resolve(object item, IIdentityKeys keys);
    public abstract bool ContainsReference(object item);
    public IIdentityKeys CapturedFor(object item) => this;
    internal bool ContainsKey(object item) => TryGetValue(item, out _);
    internal bool ContainsKey(object item, IIdentityKeys keys) => TryGetValue(item, keys, out _);
    internal T? GetValueOrDefault(object item) => TryGetValue(item, out T value) ? value : default;
    internal T this[object item] => TryGetValue(item, out T value) ? value : throw new KeyNotFoundException();
    public abstract IEnumerator<KeyValuePair<object, T>> GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

internal sealed class Identity<TRow, TKey>(
    Func<TRow, TKey> key,
    IEqualityComparer<TKey>? comparer = null,
    bool reference = false
) : IIdentity
    where TRow : class
    where TKey : notnull
{
    private readonly Func<TRow, TKey> _key = key;
    private readonly IEqualityComparer<TKey>? _comparer = comparer;
    private readonly bool _reference = reference;

    internal static IIdentity Reference() =>
        new Identity<object, object>(item => item, ReferenceEqualityComparer.Instance, true);

    public IdentityMap<T> CreateMap<T>(bool keepsKeys = false) => new Map<T>(this, keepsKeys);

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

    private interface IKeys
    {
        TKey GetKey(object item);
    }

    private sealed class Map<T> : IdentityMap<T>, IKeys
    {
        private readonly Identity<TRow, TKey> _identity;
        private readonly Dictionary<TKey, Entry> _entries;
        private readonly Dictionary<object, TKey>? _keys;

        private readonly record struct Entry(object Item, T Value);

        internal Map(Identity<TRow, TKey> identity, bool keepsKeys)
        {
            _identity = identity;
            _entries = new(identity._comparer);
            if (keepsKeys && !identity._reference)
            {
                _keys = new(ReferenceEqualityComparer.Instance);
            }
        }

        internal override int Count => _entries.Count;

        public override bool ContainsReference(object item) =>
            _keys is not null ? _keys.ContainsKey(item)
                : _identity._reference && _entries.ContainsKey((TKey)item);

        internal override void Clear()
        {
            _entries.Clear();
            _keys?.Clear();
        }

        private TKey Key(object item) =>
            _keys is not null && _keys.TryGetValue(item, out TKey? captured)
                ? captured
                : _identity.ReadKey(item);

        TKey IKeys.GetKey(object item) =>
            _identity._reference ? (TKey)item : _keys![item];

        private static TKey Key(object item, IIdentityKeys keys) => ((IKeys)keys.CapturedFor(item)).GetKey(item);

        internal override bool TryAdd(object item, T value) => Add(item, value, _identity.ReadKey(item));

        internal override bool TryAdd(object item, T value, IIdentityKeys keys) =>
            Add(item, value, Key(item, keys));

        private bool Add(object item, T value, TKey captured)
        {
            if (!_entries.TryAdd(captured, new(item, value)))
            {
                return false;
            }
            _keys?.Add(item, captured);
            return true;
        }

        internal override void Set(object item, T value) => Put(item, value, Key(item));

        internal override void Set(object item, T value, IIdentityKeys keys) =>
            Put(item, value, Key(item, keys));

        private void Put(object item, T value, TKey captured)
        {
            if (_keys is not null && _entries.TryGetValue(captured, out Entry previous))
            {
                _keys.Remove(previous.Item);
            }
            _entries[captured] = new(item, value);
            if (_keys is not null)
            {
                _keys[item] = captured;
            }
        }

        internal override bool TryGetValue(object item, out T value) => Find(Key(item), out value);

        internal override bool TryGetValue(object item, IIdentityKeys keys, out T value) =>
            Find(Key(item, keys), out value);

        private bool Find(TKey captured, out T value)
        {
            if (_entries.TryGetValue(captured, out Entry entry))
            {
                value = entry.Value;
                return true;
            }
            value = default!;
            return false;
        }

        internal override object? Resolve(object item) =>
            _entries.TryGetValue(Key(item), out Entry entry) ? entry.Item : null;

        internal override object? Resolve(object item, IIdentityKeys keys) =>
            _entries.TryGetValue(Key(item, keys), out Entry entry) ? entry.Item : null;

        internal override bool Remove(object item) => Delete(Key(item));

        internal override bool Remove(object item, IIdentityKeys keys) => Delete(Key(item, keys));

        private bool Delete(TKey captured)
        {
            if (!_entries.Remove(captured, out Entry entry))
            {
                return false;
            }
            _keys?.Remove(entry.Item);
            return true;
        }

        public override IEnumerator<KeyValuePair<object, T>> GetEnumerator()
        {
            foreach (Entry entry in _entries.Values)
            {
                yield return new(entry.Item, entry.Value);
            }
        }
    }
}

internal sealed class IdentitySet(IIdentity identity, bool keepsKeys = false) : IReadOnlyCollection<object>, IIdentityKeys
{
    internal IdentityMap<byte> Map { get; } = identity.CreateMap<byte>(keepsKeys);
    public IIdentityKeys CapturedFor(object item) => Map;
    public int Count => Map.Count;
    public bool ContainsReference(object item) => Map.ContainsReference(item);
    public bool Contains(object item) => Map.ContainsKey(item);
    internal bool Contains(object item, IIdentityKeys keys) => Map.ContainsKey(item, keys);
    internal bool Add(object item) => Map.TryAdd(item, 0);
    internal bool Add(object item, IIdentityKeys keys) => Map.TryAdd(item, 0, keys);
    internal bool Remove(object item) => Map.Remove(item);
    internal bool Remove(object item, IIdentityKeys keys) => Map.Remove(item, keys);
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
