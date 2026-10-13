using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Syno.TinyTorrent.Controls;
using Syno.TinyTorrent.Models;
using Windows.Foundation;

namespace Syno.TinyTorrent.Views;

public sealed partial class SettingsPage
{
    private readonly DispatcherTimer _outlineTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private FrameworkElement? _destination;
    private Control? _focusSurface;
    private bool _tabStop;
    private bool _focusVisuals;

    private void ShowDestination(Control control)
    {
        ClearOutline();
        FrameworkElement block = control;
        if (control is SettingsSection section)
            block = section.Surface;
        else if (ReferenceEquals(control, _scheduler.Map))
            block = _scheduler.Surface;
        else
        {
            for (DependencyObject? parent = control; parent is not null; parent = VisualTreeHelper.GetParent(parent))
                if (parent is SettingsRow row)
                {
                    block = row;
                    break;
                }
        }
        _focusSurface = block as Control ?? control;
        var name = control switch
        {
            SettingsSection group => group.Header,
            _ when block is SettingsRow row => row.Header,
            _ => AutomationProperties.GetName(control),
        };
        AutomationProperties.SetName(_focusSurface, name);
        _tabStop = _focusSurface.IsTabStop;
        _focusVisuals = _focusSurface.UseSystemFocusVisuals;
        _focusSurface.IsTabStop = true;
        _focusSurface.UseSystemFocusVisuals = false;
        _focusSurface.Focus(FocusState.Programmatic);
        block.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false });
        _destination = block;
        PositionOutline();
        DestinationOutline.Visibility = Visibility.Visible;
        _outlineTimer.Stop();
        _outlineTimer.Start();
    }

    private void PositionOutline()
    {
        if (_destination is not { IsLoaded: true } block)
            return;
        var point = block.TransformToVisual(BodyContent).TransformPoint(new Point());
        Canvas.SetLeft(DestinationOutline, point.X);
        Canvas.SetTop(DestinationOutline, point.Y);
        DestinationOutline.Width = block.ActualWidth;
        DestinationOutline.Height = block.ActualHeight;
    }

    private void ClearOutline()
    {
        _outlineTimer.Stop();
        if (_focusSurface is { } control)
        {
            control.IsTabStop = _tabStop;
            control.UseSystemFocusVisuals = _focusVisuals;
            _focusSurface = null;
        }
        _destination = null;
        DestinationOutline.Visibility = Visibility.Collapsed;
    }

    private void OnFocus(object sender, RoutedEventArgs args)
    {
        if (_focusSurface is not null && !ReferenceEquals(args.OriginalSource, _focusSurface))
            ClearOutline();
    }
}
