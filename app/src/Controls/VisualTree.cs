using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Syno.TinyTorrent.Controls;

internal static class VisualTree
{
    /// <summary>
    /// Whether <paramref name="element"/> is <paramref name="container"/> or
    /// lies inside it in the visual tree; false for a null element.
    /// </summary>
    internal static bool Contains(DependencyObject container, DependencyObject? element)
    {
        for (var current = element; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (ReferenceEquals(current, container))
            {
                return true;
            }
        }
        return false;
    }
}
