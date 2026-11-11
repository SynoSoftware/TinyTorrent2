namespace Syno.TableView.Body;

/// <summary>
/// Gives each row the view shows or the source has a slot, a small number the row keeps while it
/// is in either, and keeps by slot what one preparation hands the next: where each row stands,
/// which rows the source has, and each row's sort key. A refresh then does work in proportion to
/// the rows that changed instead of rebuilding every row's state.
/// </summary>
/// <remarks>
/// Two threads share it without a lock. The UI thread reads the committed state, which slot each
/// key has and where each slot stands in the view, at any time; <see cref="Commit"/> writes it on
/// the UI thread after the view applied a preparation's edits. The preparation worker reads the
/// committed state and writes only the rest: the numbering of the source, the order it prepares,
/// the stale keys and the sort keys. The table runs one preparation at a time and commits only
/// after it ended, so the two never write at once. A preparation the table discards leaves the
/// committed state as it was, and what it wrote of the rest is still true or rewritten by the next.
/// </remarks>
internal sealed class Slots
{
    private readonly ItemIdentity _identity;
    private readonly IReadOnlyList<object> _view;

    /// <summary>The slot of every row the committed view shows or the committed source has, by key.</summary>
    private Dictionary<object, int> _map;

    /// <summary>The map the next commit takes whole, built by <see cref="Remap"/>, or null.</summary>
    private Dictionary<object, int>? _remapped;

    /// <summary>Each slot's committed index in the view, or -1.</summary>
    private int[] _index = [];

    /// <summary>Each shown hierarchy row's committed place among its siblings, from 1.</summary>
    private int[] _sibling = [];

    /// <summary>The rows the numbered source added since the last commit, by key.</summary>
    private readonly Dictionary<object, int> _joined;

    /// <summary>Keys <see cref="Number"/> added to <see cref="_joined"/>, undone if it fails.</summary>
    private readonly List<object> _added = [];

    /// <summary>Committed rows the numbered source no longer has, until a commit leaves them out of the view.</summary>
    private readonly List<(int Slot, object Key)> _absent = [];

    private readonly Stack<int> _free = new();
    private int _capacity;

    private IReadOnlyList<object> _source = [];

    /// <summary>The source the committed view shows.</summary>
    private IReadOnlyList<object>? _committed;

    private int[] _bySource = [];

    /// <summary>Each slot's position in the numbered source, or -1.</summary>
    private int[] _sourceAt = [];

    private int[] _shown = [];
    private int _shownCount;
    private int[] _order = [];
    private int _orderCount;

    /// <summary>Each slot's index in the prepared order, or -1.</summary>
    private int[] _next = [];

    private int[] _nextSibling = [];

    /// <summary>The preparation wrote a new order, which <see cref="Commit"/> makes the view's.</summary>
    private bool _ordered;

    /// <summary>The prepared order is a sort, so every row it shows has a current key.</summary>
    private bool _sorted;

    /// <summary>Rows the prepared order pinned outside their sorted place.</summary>
    private readonly List<int> _pinned = [];

    /// <summary>Rows whose key must be read again before a sort can place them.</summary>
    private bool[] _stale = [];

    private readonly List<int> _staleList = [];

    /// <summary>The host refreshed without naming rows, so every key must be read again.</summary>
    private bool _everyStale;

    /// <summary>The view's order is no sort by the current keys, direction and source order.</summary>
    private bool _unsorted = true;

    private Func<SortKeys>? _keysFrom;
    private SortDirection _direction;

    internal Slots(ItemIdentity identity, IReadOnlyList<object> view)
    {
        _identity = identity;
        _view = view;
        IEqualityComparer<object> keys = identity.KeySelector is null
            ? ReferenceEqualityComparer.Instance
            : identity.KeyComparer;
        _map = new(keys);
        _joined = new(keys);
    }

    // ------------------------------------------------------------------ UI thread

    /// <summary>The index of <paramref name="item"/>'s row in the committed view, or -1.</summary>
    internal int IndexOf(object item) =>
        KeyOf(item) is { } key && _map.TryGetValue(key, out int slot) && slot < _index.Length
            ? _index[slot]
            : -1;

    /// <summary>The place of a shown hierarchy row among its siblings, from 1; 0 for a row not shown.</summary>
    internal int SiblingOf(object item) =>
        KeyOf(item) is { } key && _map.TryGetValue(key, out int slot) && InView(slot) && slot < _sibling.Length
            ? _sibling[slot]
            : 0;

    /// <summary>
    /// The source has <paramref name="item"/>'s row and the view holds it back. Reads the
    /// worker's numbering, so it is asked only while no preparation runs.
    /// </summary>
    internal bool IsWaiting(object item) =>
        KeyOf(item) is { } key && _map.TryGetValue(key, out int slot) && _sourceAt[slot] >= 0 && !InView(slot);

    /// <summary>
    /// Make the preparation's state the committed one. The table calls it on the UI thread right
    /// after the view applied the preparation's edits, so the committed indices describe the view.
    /// </summary>
    internal void Commit()
    {
        if (_ordered)
        {
            (_shown, _order) = (_order, _shown);
            _shownCount = _orderCount;
            (_index, _next) = (_next, _index);
            (_sibling, _nextSibling) = (_nextSibling, _sibling);
        }

        bool remapped = _remapped is not null;
        if (_remapped is { } map)
        {
            _map = map;
            _remapped = null;
        }
        else
        {
            _map.EnsureCapacity(_map.Count + _joined.Count);
            foreach ((object key, int slot) in _joined)
            {
                _map.Add(key, slot);
            }
        }

        _joined.Clear();

        int kept = 0;
        for (int i = 0; i < _absent.Count; i++)
        {
            (int slot, object key) = _absent[i];
            if (_sourceAt[slot] >= 0)
            {
                continue;
            }

            if (InView(slot))
            {
                _absent[kept++] = _absent[i];
            }
            else if (remapped || _map.Remove(key))
            {
                // A remapped map never had the key.
                _free.Push(slot);
            }
        }

        _absent.RemoveRange(kept, _absent.Count - kept);

        if (_sorted)
        {
            ClearStale();
            _everyStale = false;
            _unsorted = false;
        }

        foreach (int slot in _pinned)
        {
            MarkStale(slot);
        }

        _committed = _source;
    }

    // ------------------------------------------------------------------ preparation worker

    internal SortKeys? Keys { get; private set; }

    /// <summary>The rows the view shows, in its order; read by the worker while the UI only reads it.</summary>
    internal IReadOnlyList<object> View => _view;

    /// <summary>The numbered source as slots, in source order.</summary>
    internal ReadOnlySpan<int> Source => _bySource.AsSpan(0, _source.Count);

    /// <summary>The committed view as slots, in view order.</summary>
    internal ArraySegment<int> Shown => new(_shown, 0, _shownCount);

    /// <summary>The prepared order as slots.</summary>
    internal ArraySegment<int> Order => new(_order, 0, _orderCount);

    /// <summary>Each slot's index in the prepared order, or -1.</summary>
    internal int[] Targets => _next;

    /// <summary>The view's order cannot serve as the sorted base, so the next sort sorts every row.</summary>
    internal bool Unsorted => _unsorted || _everyStale;

    internal bool InSource(int slot) => _sourceAt[slot] >= 0;

    internal bool InView(int slot) => slot < _index.Length && _index[slot] >= 0;

    internal bool IsStale(int slot) => _everyStale || _stale[slot];

    internal int SourceAt(int slot) => _sourceAt[slot];

    /// <summary>The current row of a slot in the numbered source or the committed view.</summary>
    internal object Row(int slot) =>
        _sourceAt[slot] >= 0 ? _source[_sourceAt[slot]] : _view[_index[slot]];

    /// <summary>The committed view shows <paramref name="source"/>, the same rows in the same instances.</summary>
    internal bool Shows(IReadOnlyList<object> source) => ReferenceEquals(_committed, source);

    internal int SlotOf(object? row) => row is not null && KeyOf(row) is { } key ? Find(key) : -1;

    /// <summary>Start a preparation, forgetting what a discarded one prepared.</summary>
    internal void Begin()
    {
        _ordered = false;
        _sorted = false;
        _pinned.Clear();
        _remapped = null;
    }

    /// <summary>
    /// Build the map the next commit takes whole, when that is less work than the commit adding
    /// every row that joined and removing every row that left: after a switch to a list that
    /// shares few or no rows with the view, such as another torrent's peers. The UI thread then
    /// swaps the map instead of editing it once per row.
    /// </summary>
    /// <remarks>
    /// The next map takes the rows that joined, and keeps each committed row that the numbered
    /// source has or the next view still shows. It reads no row's key again. The UI thread only
    /// reads the committed map while a preparation runs, so the worker can read it too.
    /// </remarks>
    internal void Remap()
    {
        if (_joined.Count + _absent.Count <= _source.Count)
        {
            return;
        }

        Dictionary<object, int> map = new(_source.Count, _map.Comparer);
        foreach ((object key, int slot) in _joined)
        {
            map.Add(key, slot);
        }

        foreach ((object key, int slot) in _map)
        {
            bool shown = _ordered ? _next[slot] >= 0 : InView(slot);
            if (_sourceAt[slot] >= 0 || shown)
            {
                map.Add(key, slot);
            }
        }

        _remapped = map;
    }

    /// <summary>
    /// Number <paramref name="source"/>: a row the table already numbered keeps its slot and a
    /// new row takes a free one. It also checks every row, because only a new snapshot can bring
    /// a wrong one. A snapshot already numbered costs nothing.
    /// </summary>
    /// <remarks>
    /// A row whose instance changed, or which comes back to the source, has a stale key. Rows that
    /// stay but trade source places make the view's order unusable as a sorted base, because a
    /// sort breaks ties by source position.
    /// </remarks>
    internal void Number(IReadOnlyList<object> source, Type? rowType, CancellationToken cancellation)
    {
        if (ReferenceEquals(source, _source))
        {
            return;
        }

        IReadOnlyList<object> previous = _source;
        ClearNext();
        int[] numbered = OrderBuffer(source.Count);
        _added.Clear();
        try
        {
            int last = -1;
            for (int position = 0; position < source.Count; position++)
            {
                if ((position & 4095) == 0)
                {
                    cancellation.ThrowIfCancellationRequested();
                }

                object row = source[position];
                if (rowType is not null && !rowType.IsInstanceOfType(row))
                    throw new InvalidOperationException($"The source contains a row outside {rowType}.");
                object key = KeyOf(row)
                    ?? throw new InvalidOperationException("The schema key selector returned null.");
                int slot = Find(key);
                if (slot < 0)
                {
                    slot = Take();
                    _joined.Add(key, slot);
                    _added.Add(key);
                }
                else if (_next[slot] >= 0)
                {
                    throw new InvalidOperationException("The source contains duplicate row identities.");
                }
                else
                {
                    int before = _sourceAt[slot];
                    if (before < 0 || !ReferenceEquals(previous[before], row))
                        MarkStale(slot);
                    if (before >= 0 && before < last)
                        _unsorted = true;
                    last = Math.Max(last, before);
                }

                _next[slot] = position;
                numbered[position] = slot;
            }
        }
        catch
        {
            foreach (object key in _added)
            {
                _free.Push(_joined[key]);
                _joined.Remove(key);
            }

            throw;
        }

        (_bySource, _order) = (numbered, _bySource);
        for (int slot = 0; slot < _capacity; slot++)
        {
            int before = _sourceAt[slot];
            if (before >= 0 && _next[slot] < 0)
            {
                object key = KeyOf(previous[before])!;
                if (_joined.Remove(key))
                {
                    // Numbered by a preparation the table discarded, so never committed.
                    _free.Push(slot);
                }
                else
                {
                    _absent.Add((slot, key));
                }
            }

            _sourceAt[slot] = _next[slot];
        }

        _source = source;
    }

    /// <summary>Keep the sort keys of the column the table sorts by, or none.</summary>
    /// <param name="keys">The column's key store, or null when the table does not sort locally.</param>
    /// <param name="read">Keys a sort or layout request read on the UI thread, kept instead of read again.</param>
    internal void SortBy(Func<SortKeys>? keys, SortKeys? read, SortDirection direction)
    {
        if (keys is null)
        {
            Keys = null;
            _keysFrom = null;
            ClearStale();
            return;
        }

        if (read is { Unplaced: { } rows } && !ReferenceEquals(read, Keys))
        {
            Keys = read;
            _keysFrom = keys;
            ClearStale();
            _everyStale = false;
            _unsorted = true;
            read.Place(Places(rows), _sourceAt.Length);
        }
        else if (_keysFrom != keys)
        {
            Keys = keys();
            _keysFrom = keys;
            ClearStale();
            _everyStale = true;
            _unsorted = true;
        }

        if (direction != _direction)
        {
            _direction = direction;
            _unsorted = true;
        }
    }

    /// <summary>The host named <paramref name="rows"/> as changed: their keys are read again before a sort places them.</summary>
    internal void Name(IReadOnlyList<object> rows)
    {
        if (Keys is null)
        {
            return;
        }

        foreach (object row in rows)
        {
            int slot = SlotOf(row);
            if (slot >= 0)
            {
                MarkStale(slot);
            }
        }
    }

    /// <summary>The host refreshed without naming rows: every key is read again at the next sort.</summary>
    internal void NameEvery()
    {
        if (Keys is not null)
        {
            _everyStale = true;
        }
    }

    /// <summary>The buffer the preparation writes its order into, at least <paramref name="count"/> long.</summary>
    internal int[] OrderBuffer(int count)
    {
        if (_order.Length < count)
        {
            _order = new int[Math.Max(count, _order.Length * 2)];
        }

        return _order;
    }

    /// <summary>The first <paramref name="count"/> slots of <see cref="OrderBuffer"/> are the order to show.</summary>
    /// <param name="sorted">The order is a sort, so every row in it has a current key.</param>
    internal void Arrange(int count, bool sorted)
    {
        ClearNext();
        for (int index = 0; index < count; index++)
        {
            _next[_order[index]] = index;
        }

        _orderCount = count;
        _ordered = true;
        _sorted = sorted;
    }

    internal void SetSibling(int slot, int place)
    {
        if (_nextSibling.Length < _sourceAt.Length)
        {
            Array.Resize(ref _nextSibling, _sourceAt.Length);
        }

        _nextSibling[slot] = place;
    }

    /// <summary>
    /// The prepared order keeps <paramref name="slot"/>'s row at its index rather than its sorted
    /// place, so the view's order is no sorted base for it: its key counts as stale once committed.
    /// </summary>
    internal void Pin(int slot) => _pinned.Add(slot);

    /// <summary>
    /// Every entry of <see cref="_next"/> reads as unplaced, past the taken slots too: a commit
    /// makes it the view's index, and <see cref="Take"/> later hands those slots out without
    /// writing that index.
    /// </summary>
    private void ClearNext()
    {
        if (_next.Length < _sourceAt.Length)
        {
            _next = new int[_sourceAt.Length];
        }

        Array.Fill(_next, -1);
    }

    private object? KeyOf(object row) => _identity.KeySelector is { } key ? key(row) : row;

    private int Find(object key) =>
        _map.TryGetValue(key, out int slot) || _joined.TryGetValue(key, out slot) ? slot : -1;

    private int Take()
    {
        if (_free.Count > 0)
        {
            return _free.Pop();
        }

        if (_capacity == _sourceAt.Length)
        {
            int length = Math.Max(16, _capacity * 2);
            Array.Resize(ref _sourceAt, length);
            Array.Fill(_sourceAt, -1, _capacity, length - _capacity);
            Array.Resize(ref _next, length);
            Array.Fill(_next, -1, _capacity, length - _capacity);
            Array.Resize(ref _stale, length);
        }

        return _capacity++;
    }

    /// <summary>
    /// The slot of each row <paramref name="rows"/> holds, for keys a request read. Rows it does
    /// not hold, which joined after the request, and rows the source replaced since are stale.
    /// </summary>
    private int[] Places(IReadOnlyCollection<object> rows)
    {
        if (ReferenceEquals(rows, _source))
        {
            return _bySource;
        }

        foreach (int slot in Source)
        {
            MarkStale(slot);
        }

        int[] places = new int[rows.Count];
        int index = 0;
        foreach (object row in rows)
        {
            int slot = SlotOf(row);
            if (slot >= 0 && _sourceAt[slot] >= 0 && ReferenceEquals(_source[_sourceAt[slot]], row))
            {
                _stale[slot] = false;
            }

            places[index++] = slot;
        }

        return places;
    }

    private void MarkStale(int slot)
    {
        if (Keys is null || _stale[slot])
        {
            return;
        }

        _stale[slot] = true;
        _staleList.Add(slot);
    }

    private void ClearStale()
    {
        foreach (int slot in _staleList)
        {
            _stale[slot] = false;
        }

        _staleList.Clear();
    }
}
