using System.Diagnostics;
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
/// settling interval the hold keeps membership the same way until the settle.
/// </summary>
/// <remarks>
/// A hierarchical table holds nothing. Expansion and collapse are the person's own commands and
/// arrive through the same refresh as any other change, and a row pinned in place could be
/// separated from its parent.
/// </remarks>
public sealed partial class Table
{
    /// <summary>Rows the view keeps showing although the source no longer has them.</summary>
    private HashSet<object> _held;

    /// <summary>
    /// Rows named by <see cref="Release"/>, each with whether the source has it once the change the
    /// person caused arrives, and the <see cref="Stopwatch"/> timestamp of when it was named.
    /// </summary>
    private IReadOnlyDictionary<object, (bool Present, long Named)> _released;
    private readonly List<Body.Projection.Release> _releases = [];

    /// <summary>
    /// How long a <see cref="Release"/> entry waits for its change: one settling interval, and
    /// never less than the default one, because the hold under the pointer waits for it too.
    /// </summary>
    private TimeSpan ReleaseLifetime =>
        _sortInterval > DefaultSortInterval ? _sortInterval : DefaultSortInterval;

    private bool _pointerOver;

    /// <summary>The row under the pointer, or null over empty row surface.</summary>
    private object? _pointedRow;

    /// <summary>The row whose context menu is open.</summary>
    private object? _menuRow;

    /// <summary>The pointer is over the rows, or a row's context menu is open.</summary>
    private bool IsPointed => _pointerOver || _menuRow is not null;

    /// <summary>A row shown although it left the source.</summary>
    private bool IsHeld(object? item) => item is not null && _held.Contains(KeyOf(item));

    /// <summary>
    /// A held row shows the held look only while the pointer holds the rows. A row that waits for
    /// the settle with the pointer away looks as any row does until it leaves.
    /// </summary>
    internal bool ShowsHeld(object? item) => IsPointed && IsHeld(item);

    /// <summary>
    /// Section 5's interaction predicate as the table applies it: the host's answer, and never a
    /// held row, because the source no longer has it, however long its removal waits.
    /// </summary>
    internal bool IsInteractive(object item) =>
        !IsHeld(item) && CanInteract?.Invoke(item) != false;

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

        QueueRelease(items, waitingOnly: false);
    }

    private void QueueRelease(IEnumerable<object> items, bool waitingOnly)
    {
        _releases.Add(new(
            items.Select(item => (item, IndexInView(item) >= 0)).ToArray(),
            _source.Snapshot,
            Stopwatch.GetTimestamp())
        {
            WaitingOnly = waitingOnly,
        });
        RebuildView();
    }

    /// <summary>The pointer is over the row surface, above <paramref name="row"/> or no row.</summary>
    private void PointAt(object? row)
    {
        if (_preparingView && (!_pointerOver || !ReferenceEquals(_pointedRow, row)))
            Supersede();
        bool pointed = IsPointed;
        _pointerOver = true;
        _pointedRow = row;
        ShowHeldLook(pointed);
    }

    /// <summary>
    /// Rows that wait for the settle take the held look (<see cref="ShowsHeld"/>) as soon as the
    /// pointer holds the rows, not at the next publish.
    /// </summary>
    private void ShowHeldLook(bool wasPointed)
    {
        if (!wasPointed && _held.Count > 0)
        {
            RowVisualsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// The pointer has left the rows; what the hold kept back applies unless a menu still holds it,
    /// and inside a sort's settling interval it applies at the settle.
    /// </summary>
    private void StopPointing()
    {
        if (_preparingView && IsPointed)
            Supersede();
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
        if (_preparingView)
            Supersede();
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

            if (_preparingView)
                Supersede();
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
        // Under a sort that reorders, the pointed row kept its index, so it takes its sorted place
        // now, or at the settle inside the interval, rather than at the next update, which a quiet
        // source may never send.
        if (_held.Count == 0 && View.Count == _source.Snapshot.Count && !Reorders)
        {
            return;
        }

        RebuildView();
    }

}
