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
    private IdentitySet _held;
    private IdentitySet _candidateHeld;
    private IdentitySet _pinned;
    private readonly List<object> _shownRows = new();
    private readonly List<object> _releasedRows = new();
    private readonly List<object> _pinOrder = new();

    /// <summary>
    /// Rows named by <see cref="Release"/>, each with whether the source has it once the change the
    /// person caused arrives.
    /// </summary>
    private IdentityMap<bool> _released;

    private bool _pointerOver;

    /// <summary>The row under the pointer, or null over empty row surface.</summary>
    private object? _pointedRow;

    /// <summary>The row whose context menu is open.</summary>
    private object? _menuRow;

    /// <summary>The pointer is over the rows, or a row's context menu is open.</summary>
    private bool IsPointed => _pointerOver || _menuRow is not null;

    /// <summary>A row shown although it left the source. It is dimmed and refuses every action.</summary>
    internal bool IsHeld(object? item) => item is not null && (_view.KeysFor(item) is { } keys
        ? _held.Contains(item, keys) : _held.Contains(item));

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
            bool present = _source.Membership.ContainsKey(item, _released);
            if (present == (_view.Positions.IndexOf(item, _released) >= 0))
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
                return _surface?.ItemFromContainer(container);
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

        // Without a held row the view is a subset of the source, so a shorter view means rows wait.
        if (_held.Count == 0 && _view.Count == _source.Snapshot.Count)
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
    /// An empty view takes its rows at once: nothing is on screen to be moved. So does a snapshot
    /// that carries a change named by <see cref="Release"/>.
    /// </remarks>
    private (
        IReadOnlyList<object> Shown,
        IdentitySet Held,
        IReadOnlyList<object> Released
    ) Hold(IReadOnlyList<object> snapshot, IdentityMap<object> live, bool sortChanged = false)
    {
        IdentitySet held = _candidateHeld;
        held.Clear();
        bool holds = !sortChanged && _hierarchy is null && IsPointed && _view.Count > 0;
        if (!holds && _released.Count == 0)
        {
            return (snapshot, held, Array.Empty<object>());
        }

        List<object> released = _releasedRows;
        released.Clear();
        foreach (var entry in _released)
        {
            if (live.ContainsKey(entry.Key, _released) == entry.Value)
                released.Add(entry.Key);
        }
        if (released.Count > 0 || !holds)
        {
            return (snapshot, held, released);
        }

        List<object> staying = _shownRows;
        staying.Clear();
        foreach (object item in snapshot)
        {
            if (_view.Positions.IndexOf(item, live) >= 0)
                staying.Add(item);
        }

        foreach (object row in _view)
        {
            if (!live.ContainsKey(row, _view.Positions.Map))
            {
                held.Add(row, _view.Positions.Map);
            }
        }

        return (staying, held, released);
    }

    private void AcceptHold(IdentitySet held, IReadOnlyList<object> released)
    {
        if (!ReferenceEquals(_held, held))
            (_held, _candidateHeld) = (held, _held);
        foreach (object row in released)
            _released.Remove(row);
    }

    /// <summary>
    /// Put every held row back at the index it has, and, under a sort that reorders, the pointed row
    /// and the row whose menu is open at theirs; every other row takes the next free place in
    /// <paramref name="order"/>. Also reports those rows, so the reconcile keeps their containers.
    /// </summary>
    /// <remarks>
    /// While the rows are pointed at, the hold keeps membership unchanged, so the result has exactly
    /// the view's count and every pinned index is a place in it.
    /// </remarks>
    private (IReadOnlyList<object> Order, IdentitySet Pinned) Pin(
        IReadOnlyList<object> order,
        IdentitySet held,
        IdentityMap<object> live
    )
    {
        bool reorders = _hierarchy is null && SortsLocally && !SortsOrder;
        object? menu = reorders ? _menuRow : null;
        object? pointed = reorders ? _pointedRow : null;
        if (held.Count == 0 && menu is null && pointed is null)
        {
            return (order, held);
        }

        IdentitySet pinned = _pinned;
        pinned.Clear();
        List<object> result = _pinOrder;
        result.Clear();
        for (int index = 0; index < order.Count + held.Count; index++)
            result.Add(null!);
        foreach (object row in held)
        {
            int index = _view.Positions.IndexOf(row, held);
            if (index >= 0 && index < result.Count)
                Keep(index, row, held.Map);
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
                continue;

            while (pinned.Contains(order[next], live))
            {
                next++;
            }

            result[index] = order[next++];
        }

        return (result, pinned);

        void Keep(int index, object row, IIdentityKeys keys)
        {
            result[index] = row;
            pinned.Add(row, keys);
        }

        void KeepPointed(object? row)
        {
            if (row is null)
                return;
            int index = _view.Positions.IndexOf(row);
            if (index < 0 || index >= result.Count)
                return;
            if (live.TryGetValue(row, _view.Positions.Map, out object current))
                Keep(index, current, live);
        }
    }
}
