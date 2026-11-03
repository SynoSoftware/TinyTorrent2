using System.Diagnostics;
using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;

namespace Syno.TableView;

/// <summary>
/// Section 19's continuity for rows a publish reorders. The list has no move for a row: the view
/// removes it and inserts it again, and the list fades it out and in. So the table turns the
/// list's transitions off for that layout episode and slides every row that was on screen from
/// where it was shown to its new place, as <see cref="Motion.SlideFrom"/> does for columns.
/// </summary>
/// <remarks>
/// A row that comes on screen from elsewhere in the view slides in from the edge on its old side.
/// A row new to the source appears at once, and a row that leaves the screen goes at once: the
/// rows that take its place slide over it.
/// </remarks>
public sealed partial class Table
{
    /// <summary>
    /// Where each row the reorder moves was shown, by instance, from the reorder until the end of
    /// its layout episode, when <see cref="StartSlides"/> slides the rows from there.
    /// </summary>
    private Dictionary<object, double>? _slideFrom;

    /// <summary>The slides started, by container: the distance each started from, and when.</summary>
    private readonly Dictionary<UIElement, (double Distance, long Started)> _slides = new();

    /// <summary>
    /// Whether the publish moves a row the view shows: a reorder, or a row the difference takes
    /// out and puts back elsewhere, as a sorted order states a settle.
    /// </summary>
    private bool Moves(IReadOnlyList<object> order, Body.View.Difference difference)
    {
        if (difference.Reorders)
        {
            return true;
        }
        foreach (int index in difference.Arriving)
        {
            if (_view.Positions.IndexOf(order[index]) >= 0)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Before a reorder reconciles, record where each row that will be realized after it is shown
    /// now. Returns false when the rows are not to slide: the publish moves no row, animations are
    /// off, the list is not laid out, or every position the list holds keeps its row.
    /// </summary>
    /// <remarks>
    /// Any publish before the frame keeps the positions the first reorder recorded, since the
    /// containers it inserted have not been laid out yet, and maps the new instances it brings.
    /// </remarks>
    private bool CaptureSlides(IReadOnlyList<object> order, Body.View.Difference difference,
        IReadOnlyList<int> realized)
    {
        if (_slideFrom is null && !Moves(order, difference) || !Motion.IsEnabled || !IsLoaded
            || _surface?.ItemsPanelRoot is not ItemsStackPanel panel || InnerScrollViewer() is not { } scroller)
        {
            return false;
        }
        int start = Math.Max(0, panel.FirstCacheIndex);
        int end = Math.Min(order.Count - 1, panel.LastCacheIndex);
        bool changes = _slideFrom is not null;
        for (int index = start; index <= end && !changes; index++)
        {
            changes = index >= _view.Count || !ReferenceEquals(order[index], _view[index]);
        }
        if (!changes)
        {
            return false;
        }
        Dictionary<object, double> from = _slideFrom ?? new(ReferenceEqualityComparer.Instance);
        double rowHeight = 0;
        if (_slideFrom is null)
        {
            double offset = scroller.VerticalOffset;
            foreach (int index in realized)
            {
                if (_surface.ContainerFromIndex(index) is UIElement container)
                {
                    from[_view[index]] = container.ActualOffset.Y - offset + Shown(container);
                    rowHeight = container.ActualSize.Y;
                }
            }
        }
        int first = panel.FirstVisibleIndex;
        int last = panel.LastVisibleIndex;
        for (int index = start; index <= end; index++)
        {
            object row = order[index];
            int was = _view.Positions.IndexOf(row);
            if (was < 0 || from.ContainsKey(row))
            {
                continue;
            }
            if (from.TryGetValue(_view[was], out double shown))
            {
                // A new instance of the row: the next frame finds it by this one.
                from[row] = shown;
            }
            else if (_slideFrom is null && (was < first || was > last))
            {
                from[row] = was < first ? -rowHeight : scroller.ViewportHeight;
            }
        }
        _slideFrom = from;
        return true;
    }

    /// <summary>
    /// After layout placed the reordered rows, show each where it was and slide it to its new
    /// place. A row off screen both before and after does not slide.
    /// </summary>
    private void StartSlides()
    {
        if (_slideFrom is not { } from)
        {
            return;
        }
        if (_surface?.ItemsPanelRoot is not Panel panel || InnerScrollViewer() is not { } scroller)
        {
            return;
        }
        double offset = scroller.VerticalOffset;
        double viewport = scroller.ViewportHeight;
        Vector3KeyFrameAnimation? slide = null;
        long now = Stopwatch.GetTimestamp();
        foreach (UIElement container in panel.Children)
        {
            if (RowItem(container) is not { } row || !from.TryGetValue(row, out double was))
            {
                continue;
            }
            double placed = container.ActualOffset.Y - offset;
            double height = container.ActualSize.Y;
            double distance = was - placed;
            if (Math.Abs(distance) < Motion.MinimumMoveDips
                || (was + height <= 0 || was >= viewport) && (placed + height <= 0 || placed >= viewport))
            {
                StopSlide(container);
                continue;
            }
            slide ??= Motion.CreateSlide(ElementCompositionPreview.GetElementVisual(this).Compositor);
            Motion.Slide(container, new Vector3(0, (float)distance, 0), slide);
            _slides[container] = (distance, now);
        }
    }

    /// <summary>Put a container whose slide no longer applies, because it shows another row, in its place.</summary>
    private void StopSlide(UIElement container)
    {
        if (_slides.Remove(container))
        {
            Motion.Stop(container);
        }
    }

    /// <summary>How far from its layout place a sliding container is shown now.</summary>
    private double Shown(UIElement container)
    {
        if (!_slides.TryGetValue(container, out var slide))
        {
            return 0;
        }
        double remaining = Motion.Remaining(slide.Distance, Stopwatch.GetElapsedTime(slide.Started));
        if (remaining == 0)
        {
            _slides.Remove(container);
        }
        return remaining;
    }
}
