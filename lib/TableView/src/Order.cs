namespace Syno.TableView;

internal sealed class Order(
    Column column,
    Func<object, int?> position,
    Func<IEnumerable<object>> items
)
{
    internal Column Column { get; } = column;

    private long _publishVersion = -1;
    private bool _hasMovableRows;
    private IdentitySet? _seen;
    private readonly HashSet<int> _positions = new();

    internal bool HasMovableRows(long publishVersion)
    {
        if (_publishVersion != publishVersion)
        {
            _hasMovableRows = items().Where(Contains).Skip(1).Any();
            _publishVersion = publishVersion;
        }
        return _hasMovableRows;
    }

    internal bool Contains(object item) => position(item).HasValue;

    internal Snapshot Capture(IIdentity identity)
    {
        var entries = new List<Entry>();
        IdentityMap<Entry> captured = identity.CreateMap<Entry>();
        _seen = new(identity);
        _positions.Clear();
        try
        {
            foreach (object item in items())
            {
                if (position(item) is not int value)
                {
                    continue;
                }
                Entry entry = new(item, value);
                if (!captured.TryAdd(item, entry) || !_positions.Add(value))
                {
                    throw InvalidOrder();
                }
                entries.Add(entry);
            }
        }
        finally
        {
            _positions.Clear();
        }
        entries.Sort((left, right) => left.Position.CompareTo(right.Position));
        return new(entries.ToArray(), captured);
    }

    internal bool Matches(Snapshot snapshot)
    {
        IdentityMap<Entry> captured = snapshot.Map;
        IdentitySet seen = _seen!;
        seen.Clear();
        _positions.Clear();
        bool matches = true;
        try
        {
            foreach (object item in items())
            {
                if (position(item) is not int value)
                {
                    continue;
                }
                if (!seen.Add(item) || !_positions.Add(value))
                {
                    throw InvalidOrder();
                }
                matches &= captured.TryGetValue(item, out Entry before)
                    && ReferenceEquals(before.Item, item) && before.Position == value;
            }
            return matches && seen.Count == captured.Count;
        }
        finally
        {
            seen.Clear();
            _positions.Clear();
        }
    }

    private static InvalidOperationException InvalidOrder() =>
        new("An order must have unique items and positions.");

    internal sealed record Snapshot(Entry[] Entries, IdentityMap<Entry> Map);

    internal readonly record struct Entry(object Item, int Position);
}
