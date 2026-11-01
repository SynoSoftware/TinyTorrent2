namespace Syno.TableView;

internal sealed class Order(
    Column column,
    Func<object, int?> position,
    Func<IEnumerable<object>> items
)
{
    internal Column Column { get; } = column;

    private long _publishVersion = -1;
    private bool _canReorder;

    internal bool AllowsReorder(long publishVersion)
    {
        if (_publishVersion != publishVersion)
        {
            _canReorder = items().Where(Contains).Skip(1).Any();
            _publishVersion = publishVersion;
        }
        return _canReorder;
    }

    internal bool Contains(object item) => position(item).HasValue;

    internal Entry[] Capture(IIdentity identity)
    {
        var entries = new List<Entry>();
        var seen = new IdentitySet(identity);
        var positions = new HashSet<int>();
        foreach (var item in items())
        {
            if (position(item) is not int value)
                continue;
            if (!seen.Add(item) || !positions.Add(value))
                throw new InvalidOperationException(
                    "An order must have unique items and positions."
                );
            entries.Add(new(item, value));
        }
        entries.Sort((left, right) => left.Position.CompareTo(right.Position));
        return entries.ToArray();
    }

    internal readonly record struct Entry(object Item, int Position);
}
