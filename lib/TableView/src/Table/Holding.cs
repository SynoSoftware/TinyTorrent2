using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace Syno.TableView;

/// <summary>
/// The owner rulings of sections 5.3 and 9: nothing moves under the pointer that the person did
/// not move. While the rows are pointed at, a row that leaves the source is held in place and rows
/// that arrive wait; under a reordering sort, the pointed row keeps its place. Inside a sort's
/// settle interval the hold keeps membership the same way until the settle.
/// </summary>
/// <remarks>
/// A hierarchical table holds nothing. Expansion and collapse are the person's own commands and
/// arrive through the same refresh as any other change, and a row pinned in place could be
/// separated from its parent.
/// </remarks>
public sealed partial class Table
{
    /// <summary>Rows the view keeps showing although the source no longer has them.</summary>
    private IdentitySet _held = null!;
    private IdentitySet _candidateHeld = null!;

    /// <summary>
    /// Held rows that came back to the source while the rows are still pointed at. Each keeps the
    /// held row's index until the hold ends.
    /// </summary>
    private IdentitySet _returned = null!;
    private IdentitySet _pinned = null!;
    // The rows the last call to Pin before the current one pinned.
    private IdentitySet _lastPinned = null!;
    private readonly List<(int Index, object Row)> _pins = new();
    private readonly List<object> _shownRows = new();
    private readonly List<object> _releasedRows = new();
    private readonly List<object> _pinOrder = new();

    /// <summary>
    /// Rows named by <see cref="Release"/>, each with whether the source has it once the change the
    /// person caused arrives, and when it was named.
    /// </summary>
    private IdentityMap<(bool Present, DateTimeOffset Named)> _released = null!;

    /// <summary>
    /// How long a <see cref="Release"/> entry waits for its change: one settling interval, and
    /// never less than the default one, because the hold under the pointer waits for it too.
    /// </summary>
    private TimeSpan ReleaseLifetime => _sortInterval > DefaultSortInterval ? _sortInterval : DefaultSortInterval;

    private bool _pointerOver;

    /// <summary>The row under the pointer, or null over empty row surface.</summary>
    private object? _pointedRow;

    /// <summary>The row whose context menu is open.</summary>
    private object? _menuRow;

    /// <summary>The pointer is over the rows, or a row's context menu is open.</summary>
    private bool IsPointed => _pointerOver || _menuRow is not null;
    private bool KeepsMembership => _hierarchy is null && IsPointed && _view.Count > 0;
    private bool PinsOrder => _hierarchy is null && SortsLocally && !SortsOrder;

    /// <summary>
    /// Whether the view shows the source's rows: no row is held or returned, and none waits,
    /// because without a held or returned row a shorter view means rows wait.
    /// </summary>
    private bool ShowsSource => _hierarchy is not null
        || _held.Count == 0 && _returned.Count == 0 && _view.Count == _source.Snapshot.Count;

    /// <summary>What the hold makes of one source change.</summary>
    /// <param name="Rows">How the shown rows changed: the rows the order is made from.</param>
    /// <param name="Held">The held rows after the change.</param>
    /// <param name="Released">The <see cref="Release"/> rows the change carries.</param>
    /// <param name="Holds">Whether the rows are held back, while pointed at or until the settle.</param>
    /// <param name="Returning">
    /// Held rows back in the source while pointed at, as their current instances, including the
    /// ones that came back in an earlier change. Each keeps its held row's index until the rows
    /// are released, so nothing moves under the pointer.
    /// </param>
    private readonly record struct HoldStep(SortChange Rows, IdentitySet Held,
        IReadOnlyList<object> Released, bool Holds, IReadOnlyList<object> Returning);

    /// <summary>A row shown although it left the source. It refuses every action.</summary>
    internal bool IsHeld(object? item) => item is not null && _held.Count > 0 && _held.Contains(item);

    /// <summary>A held row shows the held look only while the pointer holds the rows.</summary>
    internal bool ShowsHeld(object? item) => KeepsMembership && IsHeld(item);

    /// <summary>
    /// Section 5's interaction predicate as the table applies it: the host's answer, and never a
    /// held row, which the host no longer has to act on.
    /// </summary>
    internal bool IsInteractive(object item) => !IsHeld(item) && CanInteract?.Invoke(item) != false;

    /// <summary>
    /// Apply at once the change the person caused to whether the source has
    /// <paramref name="items"/>, even while the pointer is over the rows. The update that carries
    /// the change applies whole, so every held row goes with it and every waiting row arrives.
    /// </summary>
    /// <remarks>
    /// Call it before the change reaches the source, or after it while the table still holds it
    /// back. A row whose change has not arrived one settling interval after the call, and at
    /// least the default interval, is no longer named, so a command that changes no row's
    /// presence leaves nothing behind. A hierarchical table holds nothing and ignores the call.
    /// </remarks>
    public void Release(IEnumerable<object> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (_hierarchy is not null)
        {
            return;
        }

        bool heldBack = false;
        DateTimeOffset now = DateTimeOffset.UtcNow;
        foreach (object item in items)
        {
            _released.TryAdd(item, default);
            bool present = _source.Membership.ContainsKey(item);
            if (present == (_view.Positions.IndexOf(item) >= 0))
            {
                _released.Set(item, (!present, now));
                continue;
            }

            _released.Set(item, (present, now));
            heldBack = true;
        }

        if (heldBack)
        {
            RebuildView();
        }
    }

    /// <summary>The pointer is over the row surface, above <paramref name="row"/> or no row.</summary>
    private void PointAt(object? row)
    {
        bool pointed = IsPointed;
        _pointerOver = true;
        _pointedRow = row;
        ShowHeldLook(pointed);
    }

    /// <summary>
    /// Held rows take the held look (<see cref="ShowsHeld"/>) as soon as the pointer holds the
    /// rows, not at the next publish.
    /// </summary>
    private void ShowHeldLook(bool wasPointed)
    {
        if (!wasPointed && _held.Count > 0)
        {
            RowVisualsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// The pointer has left the rows; what the hold kept back applies unless a menu still holds it.
    /// </summary>
    private void StopPointing()
    {
        _pointerOver = false;
        _pointedRow = null;
        ReleaseHold();
    }

    private void OnRowsPointerEntered(object sender, PointerRoutedEventArgs e) =>
        PointAt(RowFrom(e.OriginalSource as DependencyObject));

    /// <summary>
    /// Exits bubble up from every cell the pointer leaves, so only a pointer outside the row surface,
    /// or one gone out of range, as a lifted finger or pen is, stops pointing at the rows.
    /// </summary>
    private void OnRowsPointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (_surface is null)
        {
            return;
        }

        Point position = e.GetCurrentPoint(_surface).Position;
        Rect bounds = new(0, 0, _surface.ActualWidth, _surface.ActualHeight);
        if (!e.Pointer.IsInRange || !bounds.Contains(position))
        {
            StopPointing();
        }
    }

    /// <summary>The row whose container holds <paramref name="source"/>, whatever cell element it is.</summary>
    private object? RowFrom(DependencyObject? source)
    {
        for (
            DependencyObject? node = source;
            node is not null && !ReferenceEquals(node, _surface);
            node = VisualTreeHelper.GetParent(node)
        )
        {
            if (node is ListViewItem container)
            {
                return RowItem(container);
            }
        }

        return null;
    }

    /// <summary>
    /// Section 15's host shows its menu while the context request is raised, so a popup that opened
    /// during the request is that menu, and its row stays put until the menu closes.
    /// </summary>
    /// <remarks>
    /// When the menu closes the pointer is treated as having left the rows, because a pointer that
    /// left them over the menu raised no exit the rows could see. The next move over the rows
    /// points at them again.
    /// </remarks>
    private void HoldForMenu(object row, IReadOnlyList<Popup> before)
    {
        if (OpenPopups().FirstOrDefault(popup => !before.Contains(popup)) is not Popup menu)
        {
            return;
        }

        bool pointed = IsPointed;
        _menuRow = row;
        ShowHeldLook(pointed);
        menu.Closed += OnMenuClosed;

        void OnMenuClosed(object? sender, object e)
        {
            menu.Closed -= OnMenuClosed;

            // A later request has opened another menu, which owns the hold now.
            if (!ReferenceEquals(_menuRow, row))
            {
                return;
            }

            _menuRow = null;
            StopPointing();
        }
    }

    private IReadOnlyList<Popup> OpenPopups() =>
        XamlRoot is null
            ? Array.Empty<Popup>()
            : VisualTreeHelper.GetOpenPopupsForXamlRoot(XamlRoot);


    /// <summary>Apply what the hold kept back, once nothing points at the rows.</summary>
    private void ReleaseHold()
    {
        if (IsPointed || _detached || !_schemaCaptured || _hierarchy is not null || ShowsSource)
        {
            return;
        }

        RebuildView();
    }

    /// <summary>
    /// The snapshot rows to show. While the rows are pointed at, or while the change
    /// <paramref name="waits"/> for the settle, that is only the rows already shown, and every
    /// shown row the snapshot no longer has becomes a held row. A change that empties the source,
    /// and a whole change to a source that shares no row with the view, which is a different
    /// list, do not wait.
    /// </summary>
    /// <remarks>
    /// One rule per affected row: a shown row that leaves is held, a row that arrives waits, and
    /// a row that arrives with an identity the view shows returns in place of the held row. A
    /// change that names its rows passes them on. Every row is read only for a whole change, a
    /// change that carries rows named by <see cref="Release"/>, and a view that did not show the
    /// whole source before. An empty view takes its rows at once: nothing is on screen to be moved.
    /// A returned row keeps the held row's index on every change until the hold ends.
    /// </remarks>
    private HoldStep Hold(Body.Source.Preparation source, bool sortChanged = false, bool waits = false)
    {
        IReadOnlyList<object> snapshot = source.Capture.Rows;
        IdentitySet held = _candidateHeld;
        held.Clear();
        // A Clear that starts a different list cannot wait, so a change that empties the source
        // applies at once; the Adds that follow find an empty view, which takes rows at once.
        bool holds = !sortChanged && (KeepsMembership || waits && snapshot.Count > 0);
        List<object> released = _releasedRows;
        released.Clear();
        // An entry whose change did not come within its lifetime ends here, so it cannot let a
        // later background change through the hold.
        DateTimeOffset expired = DateTimeOffset.UtcNow - ReleaseLifetime;
        List<object>? ended = null;
        foreach (var entry in _released)
        {
            if (entry.Value.Named < expired)
            {
                (ended ??= new()).Add(entry.Key);
            }
            else if ((source.Resolve(entry.Key) is not null) == entry.Value.Present)
            {
                released.Add(entry.Key);
            }
        }
        foreach (object row in ended ?? [])
        {
            _released.Remove(row);
        }
        if (!holds || released.Count > 0)
        {
            return Applies();
        }
        if (!source.IsWhole)
        {
            List<Body.Source.Arrival> returning = new();
            foreach (Body.Source.Arrival arrival in source.Arrivals)
            {
                if (_view.Positions.IndexOf(arrival.Item) >= 0)
                {
                    returning.Add(arrival);
                }
            }
            List<object> returned = new(_returned.Count + returning.Count);
            foreach (object row in _returned)
            {
                if (source.Resolve(row) is { } current)
                {
                    returned.Add(current);
                }
            }
            bool leaves = false;
            foreach (object row in source.Leavers)
            {
                leaves |= _view.Positions.IndexOf(row) >= 0;
            }
            if (!leaves && returning.Count == 0)
            {
                // Unchanged held membership keeps the accepted set, because copying it costs
                // every held row on every notification.
                return new(Named(returning), _held, released, true, returned);
            }
            foreach (object row in _held)
            {
                held.Add(row);
            }
            foreach (object row in source.Leavers)
            {
                if (_view.Positions.IndexOf(row) >= 0)
                {
                    held.Add(row);
                }
            }
            foreach (Body.Source.Arrival arrival in returning)
            {
                held.Remove(arrival.Item);
                returned.Add(arrival.Item);
            }
            return new(Named(returning), held, released, true, returned);
        }
        List<object> staying = _shownRows;
        staying.Clear();
        List<object> back = new();
        foreach (object item in snapshot)
        {
            if (_view.Positions.IndexOf(item) >= 0)
            {
                staying.Add(item);
                if (_held.Count > 0 && _held.Contains(item) || _returned.Count > 0 && _returned.Contains(item))
                {
                    back.Add(item);
                }
            }
        }
        if (staying.Count == 0 && !KeepsMembership)
        {
            return Applies();
        }
        foreach (object row in _view)
        {
            if (source.Resolve(row) is null)
            {
                held.Add(row);
            }
        }
        return new(SortChange.Whole(staying, snapshot), held, released, true, back);

        HoldStep Applies()
        {
            bool showedSource = _held.Count == 0
                && _view.Count == snapshot.Count - source.Arrivals.Count + source.Leavers.Count;
            return new(source.IsWhole || released.Count > 0 || !showedSource
                ? SortChange.Whole(snapshot, snapshot) : Named(source.Arrivals), held,
                released, false,
                Array.Empty<object>());
        }

        // While the rows are held, a shown row that leaves and arrives again returns in its
        // place, so only a change the hold passes on can move a row.
        SortChange Named(IReadOnlyList<Body.Source.Arrival> arrivals) =>
            new(null, snapshot, source.Leavers, arrivals, source.Reread, source.Shift);
    }

    private void AcceptHold(HoldStep hold)
    {
        if (!ReferenceEquals(_held, hold.Held))
        {
            (_held, _candidateHeld) = (hold.Held, _held);
            _candidateHeld.Clear();
        }
        foreach (object row in hold.Released)
        {
            _released.Remove(row);
        }
        _returned.Clear();
        foreach (object row in hold.Returning)
        {
            _returned.Add(row);
        }
    }

    /// <summary>
    /// Put every held row back at the index it has, every returning row at its held row's index,
    /// and, under a sort that reorders, the pointed row
    /// and the row whose menu is open at theirs; every other row takes the next free place in
    /// <paramref name="order"/>. Also reports those rows, so the reconcile keeps their containers.
    /// </summary>
    /// <remarks>
    /// While the rows are pointed at, the hold keeps membership unchanged, so the result has exactly
    /// the view's count and every pinned index is a place in it. When only the pointed rows are
    /// pinned and each already stands at its index, the result is <paramref name="order"/> itself.
    /// When the last publish pinned the same rows and <paramref name="order"/> differs from the
    /// order it pinned only by new instances, the result is the view with those instances, stated
    /// against the view. Either way a publish inside the settle interval costs only its own rows.
    /// </remarks>
    /// <returns>
    /// The order, the pinned rows, and the order's origin when the result is made from the view;
    /// otherwise null.
    /// </returns>
    private (IReadOnlyList<object> Order, IdentitySet Pinned, Body.View.Origin? FromView) Pin(
        IReadOnlyList<object> order,
        HoldStep hold,
        Body.Source.Preparation source
    )
    {
        IdentitySet held = hold.Held;
        bool reorders = PinsOrder;
        object? menu = reorders ? _menuRow : null;
        object? pointed = reorders ? _pointedRow : null;
        (_pinned, _lastPinned) = (_lastPinned, _pinned);
        IdentitySet pinned = _pinned;
        pinned.Clear();
        if (held.Count == 0 && hold.Returning.Count == 0 && StandsInPlace(menu) && StandsInPlace(pointed))
        {
            return (order, pinned, null);
        }

        pinned.Clear();
        int count = order.Count + held.Count;
        List<(int Index, object Row)> pins = _pins;
        pins.Clear();
        foreach (object row in held)
        {
            Keep(_view.Positions.IndexOf(row), row);
        }
        foreach (object row in hold.Returning)
        {
            Keep(_view.Positions.IndexOf(row), row);
        }
        KeepPointed(menu);
        KeepPointed(pointed);

        if (pinned.Count == 0)
        {
            return (order, pinned, null);
        }
        if (SamePins() && _sortedOrder?.Origin.Difference is
            { Leaving.Count: 0, Arriving.Count: 0, Moves: RowMoves.None } stated)
        {
            // The view is the last order pinned the same way, and this order moves no row of it.
            var (kept, origin) = ViewWith(stated.Replacing.Select(replaced => order[replaced.After]));
            return (kept, pinned, origin);
        }

        List<object> result = _pinOrder;
        result.Clear();
        for (int index = 0; index < count; index++)
        {
            result.Add(null!);
        }
        foreach (var (index, row) in pins)
        {
            result[index] = row;
        }
        int next = 0;
        for (int index = 0; index < result.Count; index++)
        {
            if (result[index] is not null)
            {
                continue;
            }

            while (pinned.Contains(order[next]))
            {
                next++;
            }

            result[index] = order[next++];
        }

        return (result, pinned, null);

        // A row's index is its identity's view index, so each identity keeps one place.
        void Keep(int index, object row)
        {
            if (index >= 0 && index < count && pinned.Add(row))
            {
                pins.Add((index, row));
            }
        }

        void KeepPointed(object? row)
        {
            if (row is not null && source.Resolve(row) is { } current)
            {
                Keep(_view.Positions.IndexOf(row), current);
            }
        }

        bool SamePins()
        {
            if (pinned.Count != _lastPinned.Count)
            {
                return false;
            }
            foreach (var (_, row) in pins)
            {
                if (!_lastPinned.Contains(row))
                {
                    return false;
                }
            }
            return true;
        }

        // Whether a pointed row needs no place kept: it is not shown or not in the source, or the
        // order already has it at its view index, where it is pinned.
        bool StandsInPlace(object? row)
        {
            if (row is null)
            {
                return true;
            }
            int index = _view.Positions.IndexOf(row);
            if (index < 0 || source.Resolve(row) is not { } current)
            {
                return true;
            }
            if (index >= order.Count || !ReferenceEquals(order[index], current))
            {
                return false;
            }
            pinned.Add(current);
            return true;
        }
    }
}
