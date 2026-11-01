namespace Syno.TableView;

internal sealed class PositionIndex(IIdentity identity) : IIdentityKeys
{
    internal readonly record struct Counts(long Inserts, long Removals);
    private Func<Counts>? _counts;
    private Counts _built;
    internal IdentityMap<int> Map { get; } = identity.CreateMap<int>(keepsKeys: true);
    public IIdentityKeys CapturedFor(object item) => Map;
    internal IReadOnlyList<object> Rows { get; private set; } = Array.Empty<object>();
    public bool ContainsReference(object item) => Map.ContainsReference(item);

    internal void Build(IReadOnlyList<object> rows, IIdentityKeys keys, IIdentityKeys? held = null)
    {
        Map.Clear();
        for (int index = 0; index < rows.Count; index++)
        {
            object item = rows[index];
            IIdentityKeys captured = keys;
            if (!keys.ContainsReference(item) && held is PositionIndex previous)
            {
                captured = previous.Map;
            }
            Map.TryAdd(item, index, captured);
        }
        Rows = rows;
        _built = _counts?.Invoke() ?? default;
    }

    internal int IndexOf(object? item) => item is not null && Map.TryGetValue(item, out int index)
        ? Locate(Map.Resolve(item)!, index) : -1;

    internal int IndexOf(object item, IIdentityKeys keys) =>
        Map.TryGetValue(item, keys, out int index) ? Locate(Map.Resolve(item, keys)!, index) : -1;

    private int Locate(object item, int stored)
    {
        if (_counts is null)
        {
            return stored;
        }
        Counts now = _counts();
        long inserts = now.Inserts - _built.Inserts;
        long removals = now.Removals - _built.Removals;
        if (inserts + removals >= Math.Max(1, Math.Sqrt(Rows.Count)))
        {
            for (int index = 0; index < Rows.Count; index++)
            {
                Map.Set(Rows[index], index, Map);
            }
            _built = now;
            return Map.TryGetValue(item, Map, out int current) ? current : -1;
        }
        int first = (int)Math.Max(0, stored - removals);
        int last = (int)Math.Min(Rows.Count - 1, stored + inserts);
        for (int index = first; index <= last; index++)
        {
            if (ReferenceEquals(Rows[index], item))
            {
                return index;
            }
        }
        return -1;
    }

    internal void Insert(object item, int index, IIdentityKeys keys) => Map.Set(item, index, keys);
    internal void Remove(object item, IIdentityKeys keys) => Map.Remove(item, keys);

    internal int IndexOfKnown(object item, PositionIndex previous) =>
        IndexOf(item, Map.ContainsReference(item) ? Map : previous.Map);

    internal void Bind(IReadOnlyList<object> rows, Func<Counts>? counts = null)
    {
        Rows = rows;
        _counts = counts;
        _built = counts?.Invoke() ?? default;
    }

    internal void MoveTo(IReadOnlyList<object> rows) => Rows = rows;

    internal object? Resolve(object item)
    {
        int index = IndexOf(item);
        return index < 0 ? null : Rows[index];
    }
}
