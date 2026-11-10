namespace Syno.TableView.Body;

/// <summary>
/// One column's sort key for every row, read once and kept by slot (<see cref="Slots"/>) until
/// the row changes, so a settle compares keys it already has instead of reading every row again.
/// </summary>
/// <remarks>
/// A sort or layout request reads the keys of the rows it sees on the UI thread with
/// <see cref="ReadAll"/>, so a failing selector rejects the request before the table changes. The
/// preparation then moves those values to their slots with <see cref="Place"/> rather than reading
/// them a second time. Every later read runs on the preparation worker.
/// </remarks>
internal abstract class SortKeys
{
    /// <summary>The rows <see cref="ReadAll"/> read, in the order it read them, until <see cref="Place"/>.</summary>
    internal IReadOnlyCollection<object>? Unplaced { get; private protected set; }

    internal abstract void ReadAll(IReadOnlyCollection<object> rows);

    /// <summary>
    /// Keep the keys <see cref="ReadAll"/> read, each at the slot of its row. A row with no slot
    /// (-1 in <paramref name="slots"/>) is left out.
    /// </summary>
    internal abstract void Place(int[] slots, int capacity);

    internal abstract void Read(int slot, object row);

    internal abstract int Compare(int left, int right);
}

internal sealed class SortKeys<TRow, TKey>(Func<TRow, TKey> key, IComparer<TKey> comparer) : SortKeys
{
    private TKey[] _values = [];
    private TKey[] _read = [];

    internal override void ReadAll(IReadOnlyCollection<object> rows)
    {
        var read = new TKey[rows.Count];
        int index = 0;
        foreach (object row in rows)
        {
            read[index++] = key((TRow)row);
        }

        _read = read;
        Unplaced = rows;
    }

    internal override void Place(int[] slots, int capacity)
    {
        _values = new TKey[capacity];
        for (int index = 0; index < _read.Length; index++)
        {
            if (slots[index] >= 0)
            {
                _values[slots[index]] = _read[index];
            }
        }

        _read = [];
        Unplaced = null;
    }

    internal override void Read(int slot, object row)
    {
        if (slot >= _values.Length)
        {
            Array.Resize(ref _values, Math.Max(slot + 1, _values.Length * 2));
        }

        _values[slot] = key((TRow)row);
    }

    internal override int Compare(int left, int right) => comparer.Compare(_values[left], _values[right]);
}
