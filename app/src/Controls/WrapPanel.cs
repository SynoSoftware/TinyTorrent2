using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace Syno.TinyTorrent.Controls;

// Places children left to right at their own width and starts a new line when
// the next child does not fit. WinUI's wrapping panels give every child the
// same cell, which leaves uneven gaps after short children.
public sealed partial class WrapPanel : Panel
{
    // Read during layout only; set them in markup.
    public double ColumnSpacing { get; set; }
    public double RowSpacing { get; set; }

    protected override Size MeasureOverride(Size availableSize)
    {
        var size = new Size();
        foreach (var child in Children)
        {
            child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        }
        foreach (var place in Place(availableSize.Width))
        {
            size.Width = Math.Max(size.Width, place.Right);
            size.Height = Math.Max(size.Height, place.Bottom);
        }
        return size;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var places = Place(finalSize.Width);
        for (var index = 0; index < Children.Count; index++)
        {
            Children[index].Arrange(places[index]);
        }
        return finalSize;
    }

    private Rect[] Place(double width)
    {
        var places = new Rect[Children.Count];
        var x = 0.0;
        var y = 0.0;
        var lineHeight = 0.0;
        for (var index = 0; index < Children.Count; index++)
        {
            var child = Children[index];
            if (child.Visibility == Visibility.Collapsed)
            {
                continue;
            }
            var size = child.DesiredSize;
            if (x > 0 && x + size.Width > width)
            {
                x = 0;
                y += lineHeight + RowSpacing;
                lineHeight = 0;
            }
            places[index] = new Rect(x, y, size.Width, size.Height);
            x += size.Width + ColumnSpacing;
            lineHeight = Math.Max(lineHeight, size.Height);
        }
        return places;
    }
}
