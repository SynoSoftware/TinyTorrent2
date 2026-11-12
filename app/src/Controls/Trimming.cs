using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Syno.TinyTorrent.Controls;

/// <summary>
/// Shows a text block's whole text in a tooltip only while the block trims it,
/// because a tooltip that repeats readable text tells the person nothing. Set on
/// a <see cref="Field"/>, it applies to the field's label.
/// </summary>
public static class Trimming
{
    public static readonly DependencyProperty ShowsToolTipProperty = DependencyProperty.RegisterAttached(
        "ShowsToolTip",
        typeof(bool),
        typeof(Trimming),
        new PropertyMetadata(false, OnToolTipChanged)
    );

    // The one tooltip a text block shows each time it trims, so trimming again
    // makes no new objects.
    private static readonly DependencyProperty TipProperty = DependencyProperty.RegisterAttached(
        "Tip",
        typeof(ToolTip),
        typeof(Trimming),
        new PropertyMetadata(null)
    );

    public static bool GetShowsToolTip(DependencyObject element) => (bool)element.GetValue(ShowsToolTipProperty);

    public static void SetShowsToolTip(DependencyObject element, bool value) =>
        element.SetValue(ShowsToolTipProperty, value);

    private static void OnToolTipChanged(DependencyObject element, DependencyPropertyChangedEventArgs args)
    {
        if (element is not TextBlock text)
        {
            return;
        }
        text.IsTextTrimmedChanged -= OnTrimmed;
        if ((bool)args.NewValue)
        {
            text.IsTextTrimmedChanged += OnTrimmed;
        }
        Apply(text);
    }

    private static void OnTrimmed(TextBlock sender, IsTextTrimmedChangedEventArgs args) => Apply(sender);

    private static void Apply(TextBlock text)
    {
        if (!text.IsTextTrimmed || !GetShowsToolTip(text))
        {
            ToolTipService.SetToolTip(text, null);
            return;
        }
        if (text.GetValue(TipProperty) is not ToolTip tip)
        {
            tip = new ToolTip();
            // The text is read as the tooltip opens, so a text that changes while
            // trimmed costs nothing until the person points at it.
            tip.Loading += (_, _) => tip.Content = text.Text;
            text.SetValue(TipProperty, tip);
        }
        ToolTipService.SetToolTip(text, tip);
    }
}
