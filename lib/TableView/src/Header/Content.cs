using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace Syno.TableView.Header;

/// <summary>XAML layout for a label whose optional sort glyph uses only spare space.</summary>
public sealed partial class Content : Panel
{
    protected override Size MeasureOverride(Size availableSize)
    {
        var label = Children[0];
        var glyph = Children[1];
        label.Measure(availableSize);
        glyph.Measure(new Size(double.PositiveInfinity, availableSize.Height));
        // Fits measure only the label. The glyph is decoration that uses
        // spare space when the final column is arranged.
        return new Size(label.DesiredSize.Width,
            Math.Max(label.DesiredSize.Height, glyph.DesiredSize.Height));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var label = Children[0];
        var glyph = Children[1];
        var room = label.DesiredSize.Width + glyph.DesiredSize.Width <= finalSize.Width;
        var reserved = room ? glyph.DesiredSize.Width : 0;
        glyph.Opacity = room ? 1 : 0;
        label.Arrange(new Rect(0, 0, finalSize.Width - reserved, finalSize.Height));
        glyph.Arrange(new Rect(finalSize.Width - reserved, 0, reserved, finalSize.Height));
        return finalSize;
    }
}
