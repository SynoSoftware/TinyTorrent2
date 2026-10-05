using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Syno.TinyTorrent;

internal static class TextEditor
{
    internal static TextBox? Find(DependencyObject element)
    {
        if (element is TextBox editor) return editor;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(element); index++)
            if (Find(VisualTreeHelper.GetChild(element, index)) is { } child) return child;
        return null;
    }
}
