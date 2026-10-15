using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Syno.TinyTorrent.Controls;
using Windows.Foundation;

namespace Syno.TinyTorrent.Views;

public sealed partial class SettingsPage
{
    private readonly DispatcherTimer _outlineTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private Highlight? _highlight;

    private sealed record Highlight(
        FrameworkElement Card,
        Control Focus,
        bool TabStop,
        bool FocusVisuals
    );

    private void ShowDestination(Control control)
    {
        ClearOutline();
        FrameworkElement? block = null;
        var focus = control;
        for (
            DependencyObject? parent = control is SettingsSection section ? section.Card : control;
            parent is not null && parent != BodyContent;
            parent = VisualTreeHelper.GetParent(parent)
        )
        {
            if (parent is SettingsRow row)
                focus = row;
            if (parent is FrameworkElement surface && SettingsSection.GetIsCard(surface))
            {
                block = surface;
                break;
            }
        }
        if (block is null)
            return;
        _highlight = new(block, focus, focus.IsTabStop, focus.UseSystemFocusVisuals);
        focus.IsTabStop = true;
        focus.UseSystemFocusVisuals = false;
        focus.Focus(FocusState.Programmatic);
        focus.LostFocus += OnFocusLost;
        _outlineTimer.Start();
        block.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false });
        PositionOutline();
    }

    private void PositionOutline()
    {
        if (_highlight is not { } highlight)
            return;
        var block = highlight.Card;
        if (!block.IsLoaded)
        {
            ClearOutline();
            return;
        }
        for (
            DependencyObject? parent = block;
            parent != BodyContent;
            parent = VisualTreeHelper.GetParent(parent)
        )
        {
            if (parent is null || parent is UIElement { Visibility: Visibility.Collapsed })
            {
                ClearOutline();
                return;
            }
        }
        var point = block.TransformToVisual(BodyContent).TransformPoint(new Point());
        Canvas.SetLeft(DestinationOutline, point.X);
        Canvas.SetTop(DestinationOutline, point.Y);
        DestinationOutline.Width = block.ActualWidth;
        DestinationOutline.Height = block.ActualHeight;
        DestinationOutline.Visibility = Visibility.Visible;
    }

    private void ClearOutline()
    {
        _outlineTimer.Stop();
        var highlight = _highlight;
        _highlight = null;
        DestinationOutline.Visibility = Visibility.Collapsed;
        if (highlight is not null)
        {
            highlight.Focus.LostFocus -= OnFocusLost;
            highlight.Focus.IsTabStop = highlight.TabStop;
            highlight.Focus.UseSystemFocusVisuals = highlight.FocusVisuals;
        }
    }

    private void OnFocusLost(object sender, RoutedEventArgs args)
    {
        if (_highlight?.Focus.FocusState == FocusState.Unfocused)
            ClearOutline();
    }
}
