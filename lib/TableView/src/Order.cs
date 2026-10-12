namespace Syno.TableView;

internal sealed class Order(
    Column column,
    Func<object, int?> position,
    Func<IEnumerable<object>> items
)
{
    internal Column Column { get; } = column;

    internal Entry[] Capture(IEqualityComparer<object> identity)
    {
        var entries = new List<Entry>();
        var seen = new HashSet<object>(identity);
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
