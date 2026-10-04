using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace Syno.TableView.Body;

/// <summary>
/// Section 14's rectangle: the overlay it drives, the rows it covers, and the auto-scroll at
/// the viewport edges. It decides coverage only; what coverage means for the selection is the
/// table's, because only the table knows the modifier and the mode.
/// </summary>
/// <remarks>
/// Coverage is reported as view indices, not items. Containers come and go under a gesture that
/// auto-scrolls, so what is measurable changes; an index does not, and a row that has scrolled out
/// keeps the membership it had. An index also survives a source update, which is the reason the
/// table no longer cancels the gesture for one: the rectangle covers a band of the viewport, and
/// which rows stand in that band afterwards is a question the table re-asks of the new view.
/// </remarks>
internal sealed class Marquee
{
    /// <summary>How close to an edge the pointer must be for auto-scroll to run.</summary>
    private const double AutoScrollBandDips = 24;

    /// <summary>Vertical dips per tick at the very edge, scaled down across the band.</summary>
    private const double AutoScrollStepDips = 24;

    private readonly SortedSet<int> _covered = new();
    private readonly DispatcherTimer _autoScroll = new();

    /// <summary>Moves the rectangle without a layout pass; its size is the layout part.</summary>
    private readonly TranslateTransform _overlayOffset = new();

    private Action? _coverageChanged;
    private ListView? _rows;
    private ScrollViewer? _scroller;
    private FrameworkElement? _overlay;

    /// <summary>
    /// The fixed corner, held in content coordinates. In viewport coordinates it would slide up the
    /// list as auto-scroll runs, and rows the user had already swept would fall back out.
    /// </summary>
    private double _originContentY;

    private double _originX;
    private Point _pointer;

    internal Marquee()
    {
        _autoScroll.Interval = TimeSpan.FromMilliseconds(50);
        _autoScroll.Tick += OnAutoScrollTick;
    }

    internal bool IsActive { get; private set; }

    /// <summary>The covered rows' view indices, ascending, so they are already in visual order.</summary>
    internal IReadOnlyCollection<int> CoveredIndices => _covered;

    internal int LowestCovered => _covered.Min;

    internal int HighestCovered => _covered.Max;

    /// <param name="origin">The press position, relative to <paramref name="rows"/>.</param>
    internal void Begin(
        ListView rows,
        ScrollViewer? scroller,
        FrameworkElement? overlay,
        Point origin,
        Action coverageChanged)
    {
        _rows = rows;
        _scroller = scroller;
        _overlay = overlay;
        _coverageChanged = coverageChanged;
        _covered.Clear();
        _originX = origin.X;
        _originContentY = origin.Y + VerticalOffset();
        _pointer = origin;
        IsActive = true;

        if (_overlay is not null)
        {
            _overlay.RenderTransform = _overlayOffset;
        }

        Advance(alwaysReport: true);
    }

    /// <param name="pointer">The pointer position, relative to the hosted list.</param>
    internal void Track(Point pointer)
    {
        if (!IsActive)
        {
            return;
        }

        _pointer = pointer;
        Advance(alwaysReport: false);
        UpdateAutoScroll();
    }

    internal void End()
    {
        if (!IsActive)
        {
            return;
        }

        IsActive = false;
        _autoScroll.Stop();
        _covered.Clear();

        if (_overlay is not null)
        {
            _overlay.Visibility = Visibility.Collapsed;
        }

        _coverageChanged = null;
        _overlay = null;
        _rows = null;
        _scroller = null;
    }

    internal void Refresh()
    {
        if (_rows is null)
        {
            return;
        }

        _covered.RemoveWhere(index => index >= _rows.Items.Count);
        UpdateCoverage(CurrentRect());
    }

    // ------------------------------------------------------------------ geometry

    private void Advance(bool alwaysReport)
    {
        Rect rect = CurrentRect();
        PositionOverlay(rect);

        if (UpdateCoverage(rect) || alwaysReport)
        {
            _coverageChanged?.Invoke();
        }
    }

    private Rect CurrentRect()
    {
        double originY = _originContentY - VerticalOffset();

        return new Rect(
            Math.Min(_originX, _pointer.X),
            Math.Min(originY, _pointer.Y),
            Math.Abs(_pointer.X - _originX),
            Math.Abs(_pointer.Y - originY));
    }

    /// <summary>
    /// Section 14 measures realized geometry only, so this visits the realized containers and
    /// nothing else. A row that is not realized keeps whatever membership it already had.
    /// </summary>
    private bool UpdateCoverage(Rect rect)
    {
        // The panel's cache range, not its Children. A container left over from before a scroll
        // stays in Children, stays Visible, and still round-trips through IndexFromContainer and
        // ContainerFromIndex for the row it last showed — at bounds from where that row used to be.
        // Measured: after scrolling 60 rows the panel's range was 51..73 while Children still held
        // a container claiming row 0, whose stale bounds took row 0 back out of the rectangle.
        if (_rows?.ItemsPanelRoot is not ItemsStackPanel panel)
        {
            return false;
        }

        int first = panel.FirstCacheIndex;
        int last = Math.Min(panel.LastCacheIndex, _rows.Items.Count - 1);
        if (first < 0 || last < first)
        {
            return false;
        }

        bool changed = false;
        for (int index = first; index <= last; index++)
        {
            if (_rows.ContainerFromIndex(index) is not FrameworkElement container)
            {
                continue;
            }

            changed |= Intersects(rect, BoundsInRows(container))
                ? _covered.Add(index)
                : _covered.Remove(index);
        }

        return changed;
    }

    /// <summary>
    /// The row's band across the whole row surface, rather than the container's own width. A
    /// container is only as wide as the columns, and the space beside them is where the gesture
    /// starts, so a rectangle drawn entirely in that space would meet no container at all. What a
    /// rectangle spans vertically is what it covers: a row is a full-width band, and how far the
    /// rectangle reaches across one says nothing about whether it is in it.
    /// </summary>
    private Rect BoundsInRows(FrameworkElement container)
    {
        Rect bounds = container
            .TransformToVisual(_rows)
            .TransformBounds(new Rect(0, 0, container.ActualWidth, container.ActualHeight));

        return new Rect(0, bounds.Top, _rows?.ActualWidth ?? bounds.Width, bounds.Height);
    }

    /// <summary>
    /// Inclusive on every edge. A straight vertical drag makes a zero-width rectangle, and an
    /// exclusive test would report it as touching nothing.
    /// </summary>
    private static bool Intersects(Rect a, Rect b) =>
        a.Left <= b.Right && b.Left <= a.Right && a.Top <= b.Bottom && b.Top <= a.Bottom;

    // ------------------------------------------------------------------ auto-scroll

    private void UpdateAutoScroll()
    {
        if (AutoScrollStep() == 0)
        {
            _autoScroll.Stop();
        }
        else
        {
            _autoScroll.Start();
        }
    }

    private void OnAutoScrollTick(object? sender, object e)
    {
        double step = AutoScrollStep();
        if (step == 0 || _scroller is null)
        {
            _autoScroll.Stop();
            return;
        }

        _scroller.ChangeView(null, _scroller.VerticalOffset + step, null, disableAnimation: true);

        // The rows realized by that scroll must exist before the rectangle is measured against them.
        _rows?.UpdateLayout();
        Advance(alwaysReport: false);
    }

    private double AutoScrollStep()
    {
        if (_rows is null || _scroller is null)
        {
            return 0;
        }

        double aboveTop = AutoScrollBandDips - _pointer.Y;
        if (aboveTop > 0)
        {
            return -StepFor(aboveTop);
        }

        double belowBottom = _pointer.Y - (_rows.ActualHeight - AutoScrollBandDips);
        return belowBottom > 0 ? StepFor(belowBottom) : 0;
    }

    private static double StepFor(double depthIntoBand) =>
        Math.Min(depthIntoBand, AutoScrollBandDips) / AutoScrollBandDips * AutoScrollStepDips;

    // ------------------------------------------------------------------ overlay

    private void PositionOverlay(Rect rect)
    {
        if (_overlay is null)
        {
            return;
        }

        _overlayOffset.X = rect.X;
        _overlayOffset.Y = rect.Y;
        _overlay.Width = rect.Width;
        _overlay.Height = rect.Height;
        _overlay.Visibility = Visibility.Visible;
    }

    private double VerticalOffset() => _scroller?.VerticalOffset ?? 0;
}
