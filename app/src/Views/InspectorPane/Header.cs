using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Syno.TinyTorrent.Controls;

namespace Syno.TinyTorrent.Views;

public sealed partial class InspectorPane
{
    private bool ActionsOverflow => Surface.ActualWidth < FieldColumns.WideMinimum;

    private static Button CreateAction(string glyph, string name)
    {
        var resources = Application.Current.Resources;
        var button = new Button
        {
            Style = (Style)resources["TinyTorrentSubtleButtonStyle"],
            Content = new FontIcon
            {
                FontFamily = Lucide.Font,
                Glyph = glyph,
                FontSize = (double)resources["TinyTorrentButtonIconSize"],
            },
        };
        AutomationProperties.SetAutomationId(button, name);
        return button;
    }

    private void ShowActions(Button? more, params Button[] primary)
    {
        if (
            ReferenceEquals(MoreAction.Child, more)
            && PrimaryActions.Children.SequenceEqual(primary)
        )
            return;
        foreach (var button in PrimaryActions.Children.OfType<Button>())
            button.Flyout?.Hide();
        (MoreAction.Child as Button)?.Flyout?.Hide();
        PrimaryActions.Children.Clear();
        foreach (var button in primary)
            PrimaryActions.Children.Add(button);
        MoreAction.Child = more;
        MoreAction.Visibility = more is null ? Visibility.Collapsed : Visibility.Visible;
        var visible = more is not null || primary.Length > 0;
        TabActions.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        ArrangeActions();
    }

    private void OnHeaderSize(object sender, SizeChangedEventArgs args) => ArrangeActions();

    private void ArrangeActions()
    {
        if (PrimaryActions is null)
            return;
        var visibility =
            !ActionsOverflow && PrimaryActions.Children.Count > 0
                ? Visibility.Visible
                : Visibility.Collapsed;
        if (PrimaryActions.Visibility == visibility)
            return;
        var focused = false;
        if (visibility == Visibility.Collapsed)
        {
            foreach (var button in PrimaryActions.Children.OfType<Button>())
            {
                focused |=
                    button.FocusState != FocusState.Unfocused || button.Flyout?.IsOpen == true;
                button.Flyout?.Hide();
            }
        }
        PrimaryActions.Visibility = visibility;
        if (focused)
            (MoreAction.Child as Button)?.Focus(FocusState.Keyboard);
    }
}
