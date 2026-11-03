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
/// that arrive wait; under a reordering sort, the pointed row keeps its place.
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
    private readonly List<object> _shownRows = new();
    private readonly List<object> _releasedRows = new();
    private readonly List<object> _pinOrder = new();

    /// <summary>
    /// Rows named by <see cref="Release"/>, each with whether the source has it once the change the
    /// person caused arrives.
    /// </summary>
    private IdentityMap<bool> _released = null!;

    private bool _pointerOver;

    /// <summary>The row under the pointer, or null over empty row surface.</summary>
    private object? _pointedRow;

    /// <summary>The row whose context menu is open.</summary>
    private object? _menuRow;

    /// <summary>The pointer is over the rows, or a row's context menu is open.</summary>
    private bool IsPointed => _pointerOver || _menuRow is not null;
    private bool KeepsMembership => _hierarchy is null && IsPointed && _view.Count > 0;
    private bool PinsOrder => _hierarchy is null && SortsLocally && !SortsOrder;

    /// <summary>What the hold makes of one source change.</summary>
    /// <param name="Shown">How the shown rows changed: the rows the order is made from.</param>
    /// <param name="Held">The held rows after the change.</param>
    /// <param name="Released">The <see cref="Release"/> rows the change carries.</param>
    /// <param name="Holds">Whether the rows are held back while pointed at.</param>
    /// <param name="Returning">
    /// Held rows back in the source while pointed at, as their current instances, including the
    /// ones that came back in an earlier change. Each keeps its held row's index until the rows
    /// are released, so nothing moves under the pointer.
    /// </param>
    private readonly record struct HoldStep(SortChange Shown, IdentitySet Held,
        IReadOnlyList<object> Released, bool Holds, IReadOnlyList<object> Returning);

    /// <summary>A row shown although it left the source. It is dimmed and refuses every action.</summary>
    internal bool IsHeld(object? item) => item is not null && _held.Count > 0 && _held.Contains(item);

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
    /// back. A named row that the view already shows as the source has it is expected to change
    /// next, so a call made after the view applied the change would release a later background
    /// change instead. A hierarchical table holds nothing and ignores the call.
    /// </remarks>
    public void Release(IEnumerable<object> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (_hierarchy is not null)
        {
            return;
        }

        bool heldBack = false;
        foreach (object item in items)
        {
            _released.TryAdd(item, false);
            bool present = _source.Membership.ContainsKey(item);
            if (present == (_view.Positions.IndexOf(item) >= 0))
            {
                _released.Set(item, !present);
                continue;
            }

            _released.Set(item, present);
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
        _pointerOver = true;
        _pointedRow = row;
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

        _menuRow = row;
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
        if (IsPointed || _detached || !_schemaCaptured || _hierarchy is not null)
        {
            return;
        }

        // Without a held or returned row the view is the source's rows in their places, so a shorter
        // view means rows wait.
        if (_held.Count == 0 && _returned.Count == 0 && _view.Count == _source.Snapshot.Count)
        {
            return;
        }

        RebuildView();
    }

    /// <summary>
    /// The snapshot rows to show. While the rows are pointed at, that is only the rows already
    /// shown, and every shown row the snapshot no longer has becomes a held row.
    /// </summary>
    /// <remarks>
    /// One rule per affected row: a shown row that leaves is held, a row that arrives waits, and
    /// a row that arrives with an identity the view shows returns in place of the held row. A
    /// change that names its rows passes them on. Every row is read only for a whole change, a
    /// change that carries rows named by <see cref="Release"/>, and a view that did not show the
    /// whole source before. An empty view takes its rows at once: nothing is on screen to be moved.
    /// A returned row keeps the held row's index on every change until the hold ends.
    /// </remarks>
    private HoldStep Hold(Body.Source.Preparation source, bool sortChanged = false)
    {
        IReadOnlyList<object> snapshot = source.Capture.Rows;
        IdentitySet held = _candidateHeld;
        held.Clear();
        bool holds = !sortChanged && KeepsMembership;
        List<object> released = _releasedRows;
        released.Clear();
        foreach (var entry in _released)
        {
            if ((source.Resolve(entry.Key) is not null) == entry.Value)
            {
                released.Add(entry.Key);
            }
        }
        if (!holds || released.Count > 0)
        {
            bool showedSource = _held.Count == 0
                && _view.Count == snapshot.Count - source.Arrivals.Count + source.Leavers.Count;
            return new(source.IsWhole || released.Count > 0 || !showedSource
                ? SortChange.Whole(snapshot, snapshot) : Named(source.Arrivals), held, released, false,
                Array.Empty<object>());
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
        foreach (object row in _view)
        {
            if (source.Resolve(row) is null)
            {
                held.Add(row);
            }
        }
        return new(SortChange.Whole(staying, snapshot), held, released, true, back);

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
            _released.Remove(row);
        _returned.Clear();
        foreach (object row in hold.Returning)
            _returned.Add(row);
    }

    /// <summary>
    /// Put every held row back at the index it has, every returning row at its held row's index,
    /// and, under a sort that reorders, the pointed row
    /// and the row whose menu is open at theirs; every other row takes the next free place in
    /// <paramref name="order"/>. Also reports those rows, so the reconcile keeps their containers.
    /// </summary>
    /// <remarks>
    /// While the rows are pointed at, the hold keeps membership unchanged, so the result has exactly
    /// the view's count and every pinned index is a place in it.
    /// </remarks>
    private (IReadOnlyList<object> Order, IdentitySet Pinned) Pin(
        IReadOnlyList<object> order,
        HoldStep hold,
        Body.Source.Preparation source
    )
    {
        IdentitySet held = hold.Held;
        bool reorders = PinsOrder;
        object? menu = reorders ? _menuRow : null;
        object? pointed = reorders ? _pointedRow : null;
        if (held.Count == 0 && hold.Returning.Count == 0 && menu is null && pointed is null)
        {
            _pinned.Clear();
            return (order, held);
        }

        IdentitySet pinned = _pinned;
        pinned.Clear();
        List<object> result = _pinOrder;
        result.Clear();
        for (int index = 0; index < order.Count + held.Count; index++)
        {
            result.Add(null!);
        }
        foreach (object row in held)
        {
            int index = _view.Positions.IndexOf(row);
            if (index >= 0 && index < result.Count)
            {
                Keep(index, row);
            }
        }
        foreach (object row in hold.Returning)
        {
            int index = _view.Positions.IndexOf(row);
            if (index >= 0 && index < result.Count && result[index] is null)
            {
                Keep(index, row);
            }
        }
        KeepPointed(menu);
        KeepPointed(pointed);

        if (pinned.Count == 0)
        {
            return (order, pinned);
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

        return (result, pinned);

        void Keep(int index, object row)
        {
            result[index] = row;
            pinned.Add(row);
        }

        void KeepPointed(object? row)
        {
            if (row is null)
            {
                return;
            }
            int index = _view.Positions.IndexOf(row);
            if (index >= 0 && index < result.Count && source.Resolve(row) is { } current)
            {
                Keep(index, current);
            }
        }
    }
}
