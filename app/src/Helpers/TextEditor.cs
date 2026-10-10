using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Syno.TinyTorrent.Helpers;

internal static class TextEditor
{
    internal static void RevealTip(PasswordBox control, string text)
    {
        if (RevealButton(control) is not { } button)
            return;
        ToolTipService.SetToolTip(button, text);
        AutomationProperties.SetName(button, text);
    }

    private static FrameworkElement? RevealButton(DependencyObject element)
    {
        if (element is FrameworkElement { Name: "RevealButton" } button)
            return button;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(element); index++)
            if (RevealButton(VisualTreeHelper.GetChild(element, index)) is { } child)
                return child;
        return null;
    }

    internal static TextBox? Find(DependencyObject element, string? name = null)
    {
        if (element is TextBox editor && (name is null || editor.Name == name))
            return editor;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(element); index++)
            if (Find(VisualTreeHelper.GetChild(element, index), name) is { } child)
                return child;
        return null;
    }
}
