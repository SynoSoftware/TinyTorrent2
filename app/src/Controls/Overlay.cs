using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace Syno.TinyTorrent.Controls;

// Uses allocated bounds without letting layout-dependent content determine
// its own next allocation, as a progress indicator would during column fitting.
public sealed partial class Overlay : Panel
{
    protected override Size MeasureOverride(Size availableSize)
    {
        foreach (var child in Children)
            child.Measure(availableSize);
        return new Size();
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        foreach (var child in Children)
            child.Arrange(new Rect(new Point(), finalSize));
        return finalSize;
    }
}
