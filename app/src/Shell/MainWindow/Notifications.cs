using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Syno.TinyTorrent.Controls;
using Syno.TinyTorrent.Shared;

namespace Syno.TinyTorrent.Shell;

public sealed partial class MainWindow
{
    private readonly DispatcherTimer _toastTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(250),
    };
    private bool _stackHovered;
    // The More button whose menu is open. The pointer leaves the stack for
    // the menu, so the menu pauses the toasts itself, or its toast would
    // close under it, and keeps its More showing.
    private Button? _moreButton;
    private FrameworkElement? _pointedToast;
    private bool _windowActive = true;

    private void ConfigureNotifications()
    {
        AppStatusBar.RegisterPropertyChangedCallback(
            InfoBar.MessageProperty,
            (_, _) =>
            {
                if (!AppStatusBar.IsOpen || AppStatusBar.Message.Length == 0)
                    return;
                // InfoBar does not announce message changes while it stays open.
                AppStatusBar.IsOpen = false;
                AppStatusBar.IsOpen = true;
            }
        );
        Model.Notifications.Toasts.CollectionChanged += (_, _) =>
        {
            UpdateToastTimer();
            if (Model.Notifications.Toasts.Count == 0)
            {
                // A toast that closes under the pointer may leave without
                // a PointerExited, which would pause the next toasts forever.
                _stackHovered = false;
            }
            // Retiring shown toasts can change the collection again.
            DispatcherQueue.TryEnqueue(UpdateToasts);
        };
        Model.PropertyChanged += (_, args) =>
        {
            if (
                string.IsNullOrEmpty(args.PropertyName)
                || args.PropertyName
                    is nameof(MainViewModel.IsClosing)
                        or nameof(MainViewModel.IsPicking)
            )
                UpdateToasts();
            if (args.PropertyName == nameof(MainViewModel.ShowsStatus))
                PlaceToasts();
        };
        Root.GotFocus += (_, _) => UpdateToasts();
        Root.LostFocus += (_, _) =>
            DispatcherQueue.TryEnqueue(
                Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                UpdateToasts
            );
        Root.SizeChanged += (_, _) => UpdateToasts();
        ToastStack.SizeChanged += (_, _) => PlaceToasts();
        AppStatusLayer.SizeChanged += (_, _) => PlaceToasts();
        // A toast that arrives while the person works in another window
        // waits until they return, as Windows and VS Code notifications do.
        Activated += (_, args) =>
        {
            _windowActive = args.WindowActivationState != WindowActivationState.Deactivated;
            UpdateToastTimer();
        };
        _toastTimer.Tick += (_, _) =>
        {
            UpdateToasts();
            if (
                ToastStack.Visibility != Visibility.Visible
                || _stackHovered
                || _moreButton is not null
                || !_windowActive
                || HasToastKeyboardFocus()
            )
                return;
            Model.Notifications.Elapse(_toastTimer.Interval);
        };
        Closed += (_, _) => _toastTimer.Stop();
    }

    private void UpdateToastTimer()
    {
        if (_windowActive && Model.Notifications.Toasts.Count > 0)
            _toastTimer.Start();
        else
            _toastTimer.Stop();
    }

    // The popup's corner is the stack's bottom-right, so it follows the
    // stack's size as toasts come and go. The stack sits above the message
    // bar while the bar shows, so neither covers the other.
    private void PlaceToasts()
    {
        var bar = Model.ShowsStatus ? AppStatusLayer.ActualHeight + 8 : 0;
        ToastLayer.HorizontalOffset = -ToastStack.Width;
        ToastLayer.VerticalOffset = -ToastStack.ActualHeight - bar;
    }

    // Only keyboard focus pauses the toasts: a click on a toast's button
    // usually closes that toast, and focus that WinUI then moves to a
    // neighbouring toast is not the person reading it.
    private bool HasToastKeyboardFocus()
    {
        return FocusManager.GetFocusedElement(Root.XamlRoot)
                is Control { FocusState: FocusState.Keyboard } control
            && IsInside(control, ToastStack);
    }

    private static bool IsInside(DependencyObject? element, DependencyObject container)
    {
        while (element is not null)
        {
            if (ReferenceEquals(element, container))
                return true;
            element = VisualTreeHelper.GetParent(element);
        }
        return false;
    }

    private void OnToastPointerEntered(object sender, PointerRoutedEventArgs args)
    {
        _pointedToast = (FrameworkElement)sender;
        ShowToastMore(_pointedToast);
    }

    private void OnToastPointerExited(object sender, PointerRoutedEventArgs args)
    {
        if (ReferenceEquals(_pointedToast, sender))
            _pointedToast = null;
        ShowToastMore((FrameworkElement)sender);
    }

    private void OnToastGotFocus(object sender, RoutedEventArgs args) =>
        ShowToastMore((FrameworkElement)sender);

    // Focus has not reached its next element yet when LostFocus runs.
    private void OnToastLostFocus(object sender, RoutedEventArgs args) =>
        DispatcherQueue.TryEnqueue(() => ShowToastMore((FrameworkElement)sender));

    // More shows while the pointer is over its toast, focus is inside it or
    // its menu is open, as the engine's popup shows More only under the
    // pointer. Hidden, it keeps its space, so the title does not reflow and
    // Tab still reaches it.
    private void ShowToastMore(FrameworkElement toast)
    {
        if (toast.FindName("ToastMore") is not Button more || toast.XamlRoot is null)
            return;
        var shows =
            ReferenceEquals(_pointedToast, toast)
            || ReferenceEquals(_moreButton, more)
            || IsInside(FocusManager.GetFocusedElement(toast.XamlRoot) as DependencyObject, toast);
        more.Opacity = shows ? 1 : 0;
    }

    // Toasts show above an open dialog, so a result reported while it is
    // open is seen at once. New toasts wait while they would cover the
    // editor the person is typing in.
    private void UpdateToasts()
    {
        if (Root.XamlRoot is null)
            return;
        ToastStack.Width = Math.Min(400, Math.Max(0, Root.ActualWidth - 32));
        var visible =
            Model.Notifications.Toasts.Count > 0
            && !HasEditorFocus()
            && !Model.IsClosing
            && !Model.IsPicking;
        Model.Notifications.SetVisible(visible);
        ToastStack.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        PlaceToasts();
        ToastLayer.IsOpen = visible;
        if (!visible)
            _stackHovered = false;
    }

    // A popup draws above the popups opened before it, so the toasts reopen
    // above each dialog that opens.
    private void RaiseToasts()
    {
        if (!ToastLayer.IsOpen)
            return;
        ToastLayer.IsOpen = false;
        ToastLayer.IsOpen = true;
    }

    private void OnStackPointerEntered(object sender, PointerRoutedEventArgs args) =>
        _stackHovered = true;

    private void OnStackPointerExited(object sender, PointerRoutedEventArgs args) =>
        _stackHovered = false;

    // A tooltip that repeats readable text adds nothing, so a toast line has
    // one only while it is cut off. The tooltip follows the line's text,
    // which can change while it stays cut off.
    private void OnToastTextTrimmed(TextBlock sender, IsTextTrimmedChangedEventArgs args)
    {
        if (!sender.IsTextTrimmed)
        {
            ToolTipService.SetToolTip(sender, null);
            return;
        }
        var tip = new ToolTip();
        tip.SetBinding(
            ContentControl.ContentProperty,
            new Binding { Source = sender, Path = new PropertyPath(nameof(TextBlock.Text)) }
        );
        ToolTipService.SetToolTip(sender, tip);
    }

    private void OnDismissStatus(InfoBar sender, object args) => Model.DismissStatus();

    private void OnCloseToast(object sender, RoutedEventArgs args) =>
        Model.Notifications.Dismiss((Toast)((FrameworkElement)sender).DataContext);

    private void OnToastMore(object sender, RoutedEventArgs args)
    {
        var button = (Button)sender;
        var toast = (Toast)button.DataContext;
        if (toast.Switch is not { } @switch)
            return;
        var menu = MenuItems.Flyout(
            () =>
                [
                    new MenuCommand(
                        Model.Text.Get("notifications", "off_" + @switch),
                        Lucide.BellMinus,
                        new RelayCommand(() => Model.TurnOffToasts(toast), () => Model.CanEdit)
                    ),
                    new MenuCommand(
                        Model.Text.Get("notifications", "off_all"),
                        Lucide.BellOff,
                        new RelayCommand(() => Model.TurnOffAllToasts(toast), () => Model.CanEdit)
                    ),
                    new MenuCommand(
                        Model.Text.Get("notifications", "notification_settings"),
                        Lucide.Settings,
                        new RelayCommand(() => Model.ShowNotificationSettings(toast), () => true)
                    ),
                ],
            () => Model.Text
        );
        menu.Opened += (_, _) => _moreButton = button;
        menu.Closed += (_, _) =>
        {
            _moreButton = null;
            // Focus returns to More after Closed, so the toast decides then.
            DispatcherQueue.TryEnqueue(() =>
            {
                DependencyObject? element = button;
                while (element is not null and not FrameworkElement { Name: "ToastContainer" })
                    element = VisualTreeHelper.GetParent(element);
                if (element is FrameworkElement shown)
                    ShowToastMore(shown);
            });
        };
        // The toasts sit at the window's right edge, so the menu grows left
        // from More instead of past the edge, where the window cuts it off.
        menu.ShowAt(
            button,
            new FlyoutShowOptions { Placement = FlyoutPlacementMode.TopEdgeAlignedRight }
        );
    }
}
