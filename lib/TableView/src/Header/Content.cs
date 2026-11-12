using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace Syno.TableView.Header;

/// <summary>
/// XAML layout for a header label and its sort glyph. The sorted column's glyph always shows and
/// the label takes what is left, so a fit includes the glyph (section 9). The glyph is collapsed
/// on every other column and takes no space.
/// </summary>
public sealed partial class Content : Panel
{
    protected override Size MeasureOverride(Size availableSize)
    {
        var label = Children[0];
        var glyph = Children[1];
        glyph.Measure(new Size(double.PositiveInfinity, availableSize.Height));
        var reserved = glyph.DesiredSize.Width;
        label.Measure(new Size(Math.Max(availableSize.Width - reserved, 0), availableSize.Height));
        return new Size(label.DesiredSize.Width + reserved,
            Math.Max(label.DesiredSize.Height, glyph.DesiredSize.Height));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var label = Children[0];
        var glyph = Children[1];
        var reserved = Math.Min(glyph.DesiredSize.Width, finalSize.Width);
        label.Arrange(new Rect(0, 0, finalSize.Width - reserved, finalSize.Height));
        glyph.Arrange(new Rect(finalSize.Width - reserved, 0, reserved, finalSize.Height));
        return finalSize;
    }
}
