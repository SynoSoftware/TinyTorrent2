using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Syno.TableView.Body;

/// <summary>
/// The table-drawn drag cue and the cursor that says a row can be dragged. The
/// row template's root, wrapping the cells panel.
/// </summary>
public sealed partial class Row : ContentControl
{
    /// <summary>
    /// Shown over a row the pointer could drag. A row is only as wide as its columns and the space
    /// beside them starts section 14's marquee instead, and nothing else marks that line: the row's
    /// fill shows only while it is selected and hover is off. Measured once on the original host,
    /// 56% of every row band was that space, read as a drag that had stopped working; that host's
    /// diagnostics harness has been deleted, so section 14 records the figure and nothing here can
    /// reproduce it. The reference shows its grab cursor for the same reason. Move rather than
    /// Hand, because Hand promises a click.
    /// </summary>
    private static readonly InputSystemCursor MoveCursor = InputSystemCursor.Create(
        InputSystemCursorShape.SizeAll
    );

    private Table? _owner;

    public Row()
    {
        DefaultStyleKey = typeof(Row);
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        DataContextChanged += OnRowItemChanged;
    }

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        // The cells panel is this control's content, so it already exists when the template is
        // applied, during the first measure. Given its owner here it realizes its cells in this
        // layout pass, instead of after Loaded in a second one.
        if (Content is CellsPanel cells && (_owner ?? FindOwner(this)) is Table owner)
        {
            cells.Attach(owner);
        }

        UpdateStates(useTransitions: false);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_owner is not null)
        {
            return;
        }

        _owner = FindOwner(this);
        if (_owner is not null)
        {
            _owner.RowVisualsChanged += OnRowVisualsChanged;
        }

        UpdateStates(useTransitions: false);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        // A late Unloaded for a row still in the tree; see Table's OnUnloaded.
        if (IsLoaded)
        {
            return;
        }

        if (_owner is null)
        {
            return;
        }

        _owner.RowVisualsChanged -= OnRowVisualsChanged;
        _owner = null;
    }

    /// <summary>A recycled container gets a new row item and must repaint before it is shown.</summary>
    private void OnRowItemChanged(FrameworkElement sender, DataContextChangedEventArgs args) =>
        UpdateStates(useTransitions: false);

    private void OnRowVisualsChanged(object? sender, EventArgs e) =>
        UpdateStates(useTransitions: true);

    /// <summary>
    /// This control paints the drag cue and the dimming of a held row; selection and keyboard focus
    /// stay the native container's visuals. The cursor is the one other cue: the move cursor while
    /// the table would drag this row, which is also how a sorted table, where the table
    /// withholds the drag, says so before the press. Without the move cursor a
    /// drag from the row is section 14's sweep.
    /// </summary>
    private void UpdateStates(bool useTransitions)
    {
        object? item = DataContext;
        bool dragging = _owner is not null && _owner.IsRowDragging(item);
        bool draggable = _owner is not null && item is not null && _owner.CanBeginRowDrag(item);
        bool held = _owner is not null && _owner.IsHeld(item);

        VisualStateManager.GoToState(this, dragging ? "Dragging" : "NotDragging", useTransitions);
        VisualStateManager.GoToState(this, held ? "Held" : "NotHeld", useTransitions);
        ProtectedCursor = draggable ? MoveCursor : null;
    }

    internal static Table? FindOwner(DependencyObject node)
    {
        DependencyObject? current = VisualTreeHelper.GetParent(node);
        while (current is not null)
        {
            if (current is Table table)
            {
                return table;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }
}
