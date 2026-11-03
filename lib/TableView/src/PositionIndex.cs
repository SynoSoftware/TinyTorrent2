namespace Syno.TableView;

/// <summary>
/// Row to index over one ordered sequence. An index stored before later inserts and removals is
/// found by a scan of the window those changes can have moved it through, and the whole index is
/// rebuilt after about √n changes.
/// </summary>
/// <param name="counts">The sequence's running counts of inserts and removals, or null when the
/// owner builds the index again after every change.</param>
internal sealed class PositionIndex(IIdentity identity, Func<PositionIndex.Counts>? counts = null)
{
    internal readonly record struct Counts(long Inserts, long Removals);
    private readonly IdentityMap<int> _map = identity.CreateMap<int>();
    private Counts _built;
    internal IReadOnlyList<object> Rows { get; private set; } = Array.Empty<object>();

    /// <summary>Index <paramref name="rows"/> as they stand.</summary>
    internal void Build(IReadOnlyList<object> rows)
    {
        _map.Clear();
        for (int index = 0; index < rows.Count; index++)
        {
            _map.TryAdd(rows[index], index);
        }
        Bind(rows);
    }

    /// <summary>
    /// <paramref name="rows"/> now stand at the stored indices exactly, so the window starts
    /// again from the current counts.
    /// </summary>
    internal void Bind(IReadOnlyList<object> rows)
    {
        Rows = rows;
        _built = counts?.Invoke() ?? default;
    }

    /// <summary>
    /// <paramref name="rows"/> replace the sequence; every change since the window started was
    /// counted and recorded with <see cref="Insert"/> and <see cref="Remove"/>.
    /// </summary>
    internal void Follow(IReadOnlyList<object> rows) => Rows = rows;

    internal int IndexOf(object? item)
    {
        if (item is null || !_map.TryGetValue(item, out int stored))
        {
            return -1;
        }
        object current = _map.Resolve(item)!;
        if (counts is null || stored < Rows.Count && ReferenceEquals(Rows[stored], current))
        {
            return stored;
        }
        Counts now = counts();
        long inserts = now.Inserts - _built.Inserts;
        long removals = now.Removals - _built.Removals;
        if (inserts + removals >= Math.Max(1, Math.Sqrt(Rows.Count)))
        {
            for (int index = 0; index < Rows.Count; index++)
            {
                _map.Set(Rows[index], index);
            }
            _built = now;
            return _map.TryGetValue(current, out int rebuilt) ? rebuilt : -1;
        }
        int first = (int)Math.Max(0, stored - removals);
        int last = (int)Math.Min(Rows.Count - 1, stored + inserts);
        for (int index = first; index <= last; index++)
        {
            if (ReferenceEquals(Rows[index], current))
            {
                return index;
            }
        }
        return -1;
    }

    /// <summary>The instance standing in the sequence with <paramref name="item"/>'s identity.</summary>
    internal object? Resolve(object item)
    {
        int index = IndexOf(item);
        return index < 0 ? null : Rows[index];
    }

    internal void Insert(object item, int index) => _map.Set(item, index);
    internal void Remove(object item) => _map.Remove(item);
}
