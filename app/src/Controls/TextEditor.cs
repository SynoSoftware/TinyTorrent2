using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Syno.TinyTorrent.Controls;

public static class TextEditor
{
    // While an input method composes text in a box, Enter, Escape and leaving
    // the box belong to the input method, as the localisation contract rules,
    // so a handler of those asks IsComposing first. The state lives on the box.
    private static readonly DependencyProperty FollowsProperty = DependencyProperty.RegisterAttached(
        "Follows",
        typeof(bool),
        typeof(TextEditor),
        new PropertyMetadata(false, OnFollows)
    );

    private static readonly DependencyProperty ComposingProperty = DependencyProperty.RegisterAttached(
        "Composing",
        typeof(bool),
        typeof(TextEditor),
        new PropertyMetadata(false)
    );

    /// <summary>Follows input-method composition in the box; a later call does nothing.</summary>
    internal static void FollowComposition(TextBox editor) => editor.SetValue(FollowsProperty, true);

    internal static bool IsComposing(TextBox editor) => (bool)editor.GetValue(ComposingProperty);

    private static void OnFollows(DependencyObject element, DependencyPropertyChangedEventArgs args)
    {
        var editor = (TextBox)element;
        editor.TextCompositionStarted += OnStarted;
        editor.TextCompositionEnded += OnEnded;
    }

    private static void OnStarted(TextBox sender, TextCompositionStartedEventArgs args) =>
        sender.SetValue(ComposingProperty, true);

    private static void OnEnded(TextBox sender, TextCompositionEndedEventArgs args) =>
        sender.SetValue(ComposingProperty, false);

    internal static void RevealTip(PasswordBox control, string text)
    {
        if (RevealButton(control) is not { } button)
            return;
        IconButton.Name(button, text);
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
